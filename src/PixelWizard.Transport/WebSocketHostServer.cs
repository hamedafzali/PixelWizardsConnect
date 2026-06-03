using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelWizard.Core.Protocol;

namespace PixelWizard.Transport
{
    /// <summary>
    /// Starts an HTTP server on the given port that:
    ///   GET /         → serves the embedded web viewer HTML
    ///   GET /stream   → upgrades to WebSocket and streams JPEG frames
    /// Consumers call <see cref="BroadcastFrameAsync"/> to push frames to all connected browser clients.
    /// </summary>
    public class WebSocketHostServer : IDisposable
    {
        private readonly int _port;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();

        public event Action<string>? Log;

        public WebSocketHostServer(int port = 9001)
        {
            _port = port;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");

            try { _listener.Start(); }
            catch
            {
                // Fall back to localhost-only if binding to + fails without elevation
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Start();
            }

            Log?.Invoke($"Web viewer available at http://localhost:{_port}/");
            Task.Run(() => AcceptLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            foreach (var ws in _clients.Values)
            {
                try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None).GetAwaiter().GetResult(); }
                catch { /* ignore */ }
            }
            _clients.Clear();
            _listener?.Stop();
        }

        public async Task BroadcastFrameAsync(byte[] jpegBytes)
        {
            if (_clients.IsEmpty) return;

            // Prefix with a 1-byte type tag: 0x01 = JPEG full frame
            var payload = new byte[1 + jpegBytes.Length];
            payload[0] = 0x01;
            Buffer.BlockCopy(jpegBytes, 0, payload, 1, jpegBytes.Length);
            var segment = new ArraySegment<byte>(payload);

            foreach (var (id, ws) in _clients)
            {
                if (ws.State != WebSocketState.Open)
                {
                    _clients.TryRemove(id, out _);
                    continue;
                }
                try
                {
                    await ws.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                catch
                {
                    _clients.TryRemove(id, out _);
                }
            }
        }

        public async Task BroadcastDeltaAsync(ScreenDelta delta)
        {
            if (_clients.IsEmpty) return;

            // Prefix: 0x02 = delta, then 4-byte x, y, w, h, then JPEG
            var header = new byte[1 + 4 * 4];
            header[0] = 0x02;
            BitConverter.GetBytes(delta.X).CopyTo(header, 1);
            BitConverter.GetBytes(delta.Y).CopyTo(header, 5);
            BitConverter.GetBytes(delta.Width).CopyTo(header, 9);
            BitConverter.GetBytes(delta.Height).CopyTo(header, 13);

            var payload = new byte[header.Length + delta.ImageData.Length];
            Buffer.BlockCopy(header, 0, payload, 0, header.Length);
            Buffer.BlockCopy(delta.ImageData, 0, payload, header.Length, delta.ImageData.Length);

            var segment = new ArraySegment<byte>(payload);

            foreach (var (id, ws) in _clients)
            {
                if (ws.State != WebSocketState.Open)
                {
                    _clients.TryRemove(id, out _);
                    continue;
                }
                try { await ws.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None); }
                catch { _clients.TryRemove(id, out _); }
            }
        }

        // ── Internal HTTP accept loop ─────────────────────────────────────────

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener!.GetContextAsync();
                    _ = HandleRequestAsync(ctx, token);
                }
                catch (HttpListenerException) when (token.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Log?.Invoke($"Accept error: {ex.Message}"); }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken token)
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (ctx.Request.IsWebSocketRequest && path == "/stream")
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                string id = Guid.NewGuid().ToString("N");
                _clients[id] = wsCtx.WebSocket;
                Log?.Invoke($"Web viewer connected: {id}");
                await DrainWebSocket(id, wsCtx.WebSocket, token);
            }
            else if (path == "/" || path == "/index.html")
            {
                byte[] html = Encoding.UTF8.GetBytes(GetViewerHtml());
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = html.Length;
                await ctx.Response.OutputStream.WriteAsync(html, 0, html.Length, token);
                ctx.Response.Close();
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }

        private async Task DrainWebSocket(string id, WebSocket ws, CancellationToken token)
        {
            var buf = new byte[1024];
            try
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            catch { /* client disconnected */ }
            finally
            {
                _clients.TryRemove(id, out _);
                Log?.Invoke($"Web viewer disconnected: {id}");
            }
        }

        // ── Embedded HTML viewer ──────────────────────────────────────────────

        private string GetViewerHtml() => """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>PixelWizard Connect — Web Viewer</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { background: #0d1117; color: #e6edf3; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; display: flex; flex-direction: column; height: 100vh; }
  #toolbar { background: #161b22; border-bottom: 1px solid #30363d; padding: 10px 16px; display: flex; align-items: center; gap: 16px; flex-shrink: 0; }
  #toolbar h1 { font-size: 15px; font-weight: 600; color: #e6edf3; }
  #status { font-size: 12px; color: #8b949e; }
  #stats { font-size: 12px; color: #8b949e; margin-left: auto; }
  #dot { width: 8px; height: 8px; border-radius: 50%; background: #f85149; flex-shrink: 0; }
  #dot.connected { background: #3fb950; }
  #viewport { flex: 1; overflow: auto; display: flex; align-items: center; justify-content: center; background: #010409; }
  canvas { max-width: 100%; max-height: 100%; display: block; image-rendering: auto; }
  #placeholder { text-align: center; color: #484f58; }
  #placeholder p { margin-top: 8px; font-size: 13px; }
</style>
</head>
<body>
<div id="toolbar">
  <div id="dot"></div>
  <h1>PixelWizard Connect</h1>
  <span id="status">Connecting…</span>
  <span id="stats"></span>
</div>
<div id="viewport">
  <canvas id="screen" style="display:none"></canvas>
  <div id="placeholder">
    <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="#484f58" stroke-width="1.5">
      <rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8m-4-4v4"/>
    </svg>
    <p>Waiting for screen data…</p>
  </div>
</div>
<script>
const canvas = document.getElementById('screen');
const ctx = canvas.getContext('2d');
const placeholder = document.getElementById('placeholder');
const statusEl = document.getElementById('status');
const statsEl = document.getElementById('stats');
const dotEl = document.getElementById('dot');

let ws, compositeBitmap = null;
let frames = 0, bytes = 0, latencyMs = -1, pingTime = 0;

function connect() {
  const host = location.host;
  ws = new WebSocket(`ws://${host}/stream`);
  ws.binaryType = 'arraybuffer';

  ws.onopen = () => {
    statusEl.textContent = 'Connected';
    dotEl.classList.add('connected');
    setInterval(sendPing, 2000);
  };

  ws.onclose = () => {
    statusEl.textContent = 'Disconnected — reconnecting…';
    dotEl.classList.remove('connected');
    setTimeout(connect, 3000);
  };

  ws.onerror = () => {};

  ws.onmessage = e => {
    bytes += e.data.byteLength;
    const view = new DataView(e.data);
    const type = view.getUint8(0);

    if (type === 0x01) {
      // Full frame
      const blob = new Blob([new Uint8Array(e.data, 1)], { type: 'image/jpeg' });
      const url = URL.createObjectURL(blob);
      const img = new Image();
      img.onload = () => {
        canvas.width = img.naturalWidth;
        canvas.height = img.naturalHeight;
        ctx.drawImage(img, 0, 0);
        URL.revokeObjectURL(url);
        showCanvas();
        frames++;
      };
      img.src = url;

    } else if (type === 0x02) {
      // Delta patch
      const x = view.getInt32(1, true), y = view.getInt32(5, true);
      const w = view.getInt32(9, true), h = view.getInt32(13, true);
      const blob = new Blob([new Uint8Array(e.data, 17)], { type: 'image/jpeg' });
      const url = URL.createObjectURL(blob);
      const img = new Image();
      img.onload = () => {
        ctx.drawImage(img, x, y, w, h);
        URL.revokeObjectURL(url);
        frames++;
      };
      img.src = url;

    } else if (type === 0x10) {
      // Pong
      latencyMs = Date.now() - pingTime;
    }
  };
}

function showCanvas() {
  canvas.style.display = 'block';
  placeholder.style.display = 'none';
}

function sendPing() {
  if (ws?.readyState !== WebSocket.OPEN) return;
  pingTime = Date.now();
  const buf = new Uint8Array(1);
  buf[0] = 0x10;
  ws.send(buf);
}

// Stats update
setInterval(() => {
  const fps = frames; frames = 0;
  const kb = (bytes / 1024).toFixed(0); bytes = 0;
  statsEl.textContent = `FPS: ${fps}  |  Down: ${kb} KB/s${latencyMs >= 0 ? '  |  Latency: ' + latencyMs + ' ms' : ''}`;
}, 1000);

connect();
</script>
</body>
</html>
""";

        public void Dispose() => Stop();
    }
}
