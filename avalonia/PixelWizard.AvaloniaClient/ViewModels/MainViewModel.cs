using System;
using System.IO;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ReactiveUI;
using PixelWizard.AvaloniaClient.Platform;
using PixelWizard.Core.Interfaces;
using PixelWizard.Core.Models;
using PixelWizard.Core.Protocol;
using PixelWizard.Transport;

namespace PixelWizard.AvaloniaClient.ViewModels;

public enum AppScreen { ModeSelection, Viewer, Host, LiveScreen }

public class MainViewModel : ReactiveObject, IDisposable
{
    // ── Navigation ────────────────────────────────────────────────────────────

    private AppScreen _screen = AppScreen.ModeSelection;
    private AppScreen Screen
    {
        get => _screen;
        set
        {
            _screen = value;
            this.RaisePropertyChanged(nameof(ShowModeSelection));
            this.RaisePropertyChanged(nameof(ShowViewerPanel));
            this.RaisePropertyChanged(nameof(ShowHostPanel));
            this.RaisePropertyChanged(nameof(ShowScreenDisplay));
        }
    }

    public bool ShowModeSelection => _screen == AppScreen.ModeSelection;
    public bool ShowViewerPanel   => _screen == AppScreen.Viewer;
    public bool ShowHostPanel     => _screen == AppScreen.Host;
    public bool ShowScreenDisplay => _screen == AppScreen.LiveScreen;

    // ── Platform ──────────────────────────────────────────────────────────────

    private readonly IHostProvider _hostProvider = HostProviderFactory.Create();
    public bool CanHost => _hostProvider.IsAvailable;

    // ── Shared state ──────────────────────────────────────────────────────────

    private string _status = "Choose a mode to begin";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            this.RaiseAndSetIfChanged(ref _isConnected, value);
            this.RaisePropertyChanged(nameof(ShowConnectPanel));
        }
    }

    // ── Viewer properties ─────────────────────────────────────────────────────

    private string _hostAddress = "127.0.0.1";
    public string HostAddress { get => _hostAddress; set => this.RaiseAndSetIfChanged(ref _hostAddress, value); }

    private string _connectionCode = "";
    public string ConnectionCode { get => _connectionCode; set => this.RaiseAndSetIfChanged(ref _connectionCode, value); }

    private string _routerAddress = "localhost:9000";
    public string RouterAddress { get => _routerAddress; set => this.RaiseAndSetIfChanged(ref _routerAddress, value); }

    private bool _useTls = true;
    public bool UseTls { get => _useTls; set => this.RaiseAndSetIfChanged(ref _useTls, value); }

    private bool _isConnecting;
    public bool IsConnecting
    {
        get => _isConnecting;
        set
        {
            this.RaiseAndSetIfChanged(ref _isConnecting, value);
            this.RaisePropertyChanged(nameof(InputEnabled));
        }
    }

    public bool ShowConnectPanel => !_isConnected;
    public bool InputEnabled     => !_isConnecting;

    private Bitmap? _remoteScreen;
    public Bitmap? RemoteScreen { get => _remoteScreen; set => this.RaiseAndSetIfChanged(ref _remoteScreen, value); }

    private bool _keyboardActive;
    public bool KeyboardActive { get => _keyboardActive; set => this.RaiseAndSetIfChanged(ref _keyboardActive, value); }

    // ── Host properties ───────────────────────────────────────────────────────

    private string _hostPort = "8888";
    public string HostPort { get => _hostPort; set => this.RaiseAndSetIfChanged(ref _hostPort, value); }

    private string _hostRouterAddress = "localhost:9000";
    public string HostRouterAddress { get => _hostRouterAddress; set => this.RaiseAndSetIfChanged(ref _hostRouterAddress, value); }

    private bool _useRouterForHost;
    public bool UseRouterForHost
    {
        get => _useRouterForHost;
        set
        {
            this.RaiseAndSetIfChanged(ref _useRouterForHost, value);
            this.RaisePropertyChanged(nameof(ShowDirectHostPanel));
            this.RaisePropertyChanged(nameof(ShowRouterHostPanel));
        }
    }
    public bool ShowDirectHostPanel => !_useRouterForHost;
    public bool ShowRouterHostPanel =>  _useRouterForHost;

    private int _hostQualityIndex = 1;
    public int HostQualityIndex { get => _hostQualityIndex; set => this.RaiseAndSetIfChanged(ref _hostQualityIndex, value); }

    private bool _hostTlsEnabled = true;
    public bool HostTlsEnabled { get => _hostTlsEnabled; set => this.RaiseAndSetIfChanged(ref _hostTlsEnabled, value); }

    private string _hostConnectionCode = "";
    public string HostConnectionCode { get => _hostConnectionCode; set => this.RaiseAndSetIfChanged(ref _hostConnectionCode, value); }

    private string _hostStatus = "Not started";
    public string HostStatus { get => _hostStatus; set => this.RaiseAndSetIfChanged(ref _hostStatus, value); }

    private bool _isHostRunning;
    public bool IsHostRunning { get => _isHostRunning; set => this.RaiseAndSetIfChanged(ref _isHostRunning, value); }

    private bool _showCodeCard;
    public bool ShowCodeCard { get => _showCodeCard; set => this.RaiseAndSetIfChanged(ref _showCodeCard, value); }

    private string _webViewerUrl = "";
    public string WebViewerUrl { get => _webViewerUrl; set => this.RaiseAndSetIfChanged(ref _webViewerUrl, value); }

    private bool _showWebViewer;
    public bool ShowWebViewer { get => _showWebViewer; set => this.RaiseAndSetIfChanged(ref _showWebViewer, value); }

    // ── Metrics ───────────────────────────────────────────────────────────────

    private string _fpsText = "FPS: —";
    public string FpsText { get => _fpsText; set => this.RaiseAndSetIfChanged(ref _fpsText, value); }

    private string _latencyText = "Latency: —";
    public string LatencyText { get => _latencyText; set => this.RaiseAndSetIfChanged(ref _latencyText, value); }

    private string _bandwidthText = "↓ —";
    public string BandwidthText { get => _bandwidthText; set => this.RaiseAndSetIfChanged(ref _bandwidthText, value); }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> GoHostCommand          { get; }
    public ReactiveCommand<Unit, Unit> GoViewerCommand        { get; }
    public ReactiveCommand<Unit, Unit> BackCommand            { get; }
    public ReactiveCommand<Unit, Unit> ConnectDirectCommand   { get; }
    public ReactiveCommand<Unit, Unit> ConnectViaCodeCommand  { get; }
    public ReactiveCommand<Unit, Unit> StartDirectHostCommand { get; }
    public ReactiveCommand<Unit, Unit> RegisterHostCommand    { get; }
    public ReactiveCommand<Unit, Unit> StopHostCommand        { get; }
    public ReactiveCommand<Unit, Unit> CopyCodeCommand        { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand      { get; }

    // ── Internal state ────────────────────────────────────────────────────────

    private ISessionTransport?    _transport;
    private ISessionTransport?    _hostTransport;
    private readonly IRouterClient _router = new RouterHttpClient();
    private IScreenCapture?       _capture;
    private IInputInjector?       _input;
    private WebSocketHostServer?  _wsServer;
    private DispatcherTimer?      _captureTimer;
    private bool                  _isSendingFrame;

    private RenderTargetBitmap? _canvas;
    private int _canvasWidth, _canvasHeight;
    private int _renderedFrames, _receivedBytes, _lastLatencyMs = -1;
    private (int x, int y) _lastMousePos;

    private System.Timers.Timer? _metricsTimer;
    private System.Timers.Timer? _pingTimer;
    private System.Timers.Timer? _sessionWatchdog;

    // Session secret: set before connecting/hosting, consumed during TCP handshake.
    private string _sessionSecret         = "";  // viewer side — sent to host
    private string _expectedSessionSecret = "";  // host side — validated against viewer's secret

    // Handshake gate: set false when viewer connects, set true after secret verified.
    private bool _hostHandshakeComplete;

    // Active host port — needed to re-listen after a viewer disconnects.
    private int _activeHostPort = 8888;

    // Set by App.axaml.cs — shows a consent dialog and returns allow/deny
    public Func<string, Task<bool>>? ConsentCallback { get; set; }
    // Set by App.axaml.cs — copies text to clipboard via TopLevel
    public Func<string, Task>? ClipboardCallback { get; set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel()
    {
        GoHostCommand          = ReactiveCommand.Create(() => { Screen = AppScreen.Host; });
        GoViewerCommand        = ReactiveCommand.Create(() => { Screen = AppScreen.Viewer; });
        BackCommand            = ReactiveCommand.Create(GoBack);
        ConnectDirectCommand   = ReactiveCommand.CreateFromTask(ConnectDirect);
        ConnectViaCodeCommand  = ReactiveCommand.CreateFromTask(ConnectViaCode);
        StartDirectHostCommand = ReactiveCommand.CreateFromTask(StartDirectHost);
        RegisterHostCommand    = ReactiveCommand.CreateFromTask(RegisterWithRouter);
        StopHostCommand        = ReactiveCommand.Create(StopHost);
        CopyCodeCommand        = ReactiveCommand.Create(CopyCode);
        DisconnectCommand      = ReactiveCommand.Create(DisconnectViewer);

        StartMetricsTimer();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void GoBack()
    {
        if (IsHostRunning) StopHost();
        DisconnectViewer();
        Screen = AppScreen.ModeSelection;
    }

    // ── Viewer connect ────────────────────────────────────────────────────────

    private async Task ConnectDirect()
    {
        _sessionSecret = "";   // no shared secret for direct LAN connections
        IsConnecting = true;
        Status = $"Connecting to {HostAddress}…";
        try
        {
            _transport = BuildViewerTransport();
            await _transport.ConnectAsync(HostAddress.Trim(), 8888, UseTls);
        }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; IsConnecting = false; }
    }

    private async Task ConnectViaCode()
    {
        string code = ConnectionCode.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code)) { Status = "Enter a connection code"; return; }
        var (host, port) = ParseAddress(RouterAddress);
        IsConnecting = true;
        Status = "Resolving via router…";
        try
        {
            var result = await _router.ResolveEndpointAsync(host, port, code);
            _sessionSecret = result.SessionSecret;
            var parts = result.HostEndpoint.Split(':');
            _transport = BuildViewerTransport();
            await _transport.ConnectAsync(parts[0], int.Parse(parts[1]), UseTls);
        }
        catch (Exception ex) { Status = $"Router error: {ex.Message}"; IsConnecting = false; }
    }

    private ISessionTransport BuildViewerTransport()
    {
        var t = new TcpTransport();
        t.Connected += async () =>
        {
            // Send the session secret as the first message so the host can verify us.
            await t.SendMessageAsync(new NetworkMessage
            {
                Type = MessageType.Handshake,
                Data = Encoding.UTF8.GetBytes(_sessionSecret)
            });
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected  = true;
                IsConnecting = false;
                Screen       = AppScreen.LiveScreen;
                Status       = "Connected";
                _pingTimer?.Start();
            });
        };
        t.Disconnected += () => Dispatcher.UIThread.Post(() =>
        {
            IsConnected  = false;
            IsConnecting = false;
            Screen       = AppScreen.Viewer;
            Status       = "Disconnected";
            _pingTimer?.Stop();
            KeyboardActive = false;
        });
        t.MessageReceived += OnViewerMessage;
        t.BytesReceived   += b => _receivedBytes += b;
        t.Error += ex => Dispatcher.UIThread.Post(() => Status = $"Error: {ex.Message}");
        return t;
    }

    private void DisconnectViewer()
    {
        _transport?.Disconnect();
        _transport   = null;
        IsConnected  = false;
        IsConnecting = false;
        RemoteScreen = null;
        _canvas?.Dispose(); _canvas = null;
        KeyboardActive = false;
        if (Screen == AppScreen.LiveScreen) Screen = AppScreen.Viewer;
    }

    // ── Host mode ─────────────────────────────────────────────────────────────

    private async Task StartDirectHost()
    {
        if (!_hostProvider.IsAvailable) { Status = "Host not available on this platform"; return; }
        if (!int.TryParse(HostPort, out int port)) port = 8888;
        _expectedSessionSecret = "";  // no secret for direct LAN connections
        _activeHostPort        = port;
        Status = "Starting host…";
        try
        {
            SetupHostServices();
            _hostTransport = BuildHostTransport();
            await _hostTransport.StartServerAsync(port, HostTlsEnabled);
            StartCaptureTimer();
            string lanIp = GetLanIp();
            HostStatus = $"Listening — tell viewer to connect to {lanIp}:{port}";
            Status     = $"Host ready. Viewer address: {lanIp}:{port}";
        }
        catch (Exception ex) { Status = $"Host error: {ex.Message}"; }
    }

    private static string GetLanIp()
    {
        foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !System.Net.IPAddress.IsLoopback(ip))
                return ip.ToString();
        return "127.0.0.1";
    }

    private async Task RegisterWithRouter()
    {
        if (!_hostProvider.IsAvailable) { Status = "Host not available on this platform"; return; }
        var (host, port) = ParseAddress(HostRouterAddress);
        Status = "Registering with router…";
        try
        {
            string localIp   = GetLocalEndpoint(host);
            var result       = await _router.RegisterHostAsync(host, port, localIp);
            _expectedSessionSecret = result.SessionSecret;
            _activeHostPort        = 8888;

            HostConnectionCode = result.ConnectionCode;
            ShowCodeCard = true;

            SetupHostServices();
            _hostTransport = BuildHostTransport();
            await _hostTransport.StartServerAsync(8888, HostTlsEnabled);
            StartCaptureTimer();
            HostStatus = $"Ready — code: {result.ConnectionCode}";
            Status = $"Host registered. Share code: {result.ConnectionCode}";
        }
        catch (Exception ex) { Status = $"Router error: {ex.Message}"; }
    }

    private void SetupHostServices()
    {
        var settings = StreamingSettings.FromPresetIndex(HostQualityIndex);
        _capture = _hostProvider.CreateCapture(settings.FullRefreshInterval);
        _input   = _hostProvider.CreateInput();

        _wsServer = new WebSocketHostServer(9001);
        _wsServer.Log += msg => Dispatcher.UIThread.Post(() => Status = msg);
        _wsServer.Start();
        WebViewerUrl  = "http://localhost:9001/";
        ShowWebViewer = true;
        IsHostRunning = true;
    }

    private ISessionTransport BuildHostTransport()
    {
        var t = new TcpTransport();
        t.Connected += () =>
        {
            _hostHandshakeComplete = false;
            Dispatcher.UIThread.Post(() =>
            {
                HostStatus = "Viewer connecting — authenticating…";
                Status     = "Authenticating viewer…";
            });
        };
        t.Disconnected += () => Dispatcher.UIThread.Post(async () =>
        {
            _sessionWatchdog?.Stop();
            _hostHandshakeComplete = false;
            HostStatus = "Client disconnected";
            Status     = "Client disconnected";
            if (IsHostRunning)
                await RelistenHostAsync();
        });
        t.MessageReceived += OnHostMessage;
        t.Error += ex => Dispatcher.UIThread.Post(() => Status = $"Host error: {ex.Message}");
        return t;
    }

    private async Task RelistenHostAsync()
    {
        HostStatus = "Waiting for next connection…";
        Status     = "Waiting for next connection…";
        try
        {
            _hostTransport?.Dispose();
            _hostTransport = BuildHostTransport();
            await _hostTransport.StartServerAsync(_activeHostPort, HostTlsEnabled);
        }
        catch (Exception ex)
        {
            Status = $"Re-listen error: {ex.Message}";
            IsHostRunning = false;
        }
    }

    private void StopHost()
    {
        _captureTimer?.Stop();
        _sessionWatchdog?.Stop();
        _sessionWatchdog?.Dispose();
        _sessionWatchdog = null;
        _hostTransport?.Disconnect();
        _capture?.Dispose();
        _wsServer?.Stop();
        _hostTransport         = null;
        _capture               = null;
        _input                 = null;
        _wsServer              = null;
        _hostHandshakeComplete = false;
        IsHostRunning  = false;
        ShowCodeCard   = false;
        ShowWebViewer  = false;
        HostStatus     = "Stopped";
        Status         = "Host stopped";
    }

    private void CopyCode()
    {
        if (!string.IsNullOrEmpty(HostConnectionCode) && ClipboardCallback != null)
        {
            _ = ClipboardCallback(HostConnectionCode);
            Status = "Code copied to clipboard";
        }
    }

    // ── Session watchdog (host side) ──────────────────────────────────────────

    private void StartSessionWatchdog()
    {
        _sessionWatchdog?.Dispose();
        _sessionWatchdog = new System.Timers.Timer(30_000) { AutoReset = false };
        _sessionWatchdog.Elapsed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            HostStatus = "Session timed out — viewer inactive";
            Status     = "Session timed out";
            _hostTransport?.Disconnect();
        });
        _sessionWatchdog.Start();
    }

    private void ResetSessionWatchdog()
    {
        _sessionWatchdog?.Stop();
        _sessionWatchdog?.Start();
    }

    // ── Capture loop ──────────────────────────────────────────────────────────

    private void StartCaptureTimer()
    {
        var settings = StreamingSettings.FromPresetIndex(HostQualityIndex);
        _captureTimer = new DispatcherTimer { Interval = settings.FrameInterval };
        _captureTimer.Tick += async (_, _) => await CaptureTickAsync();
        _captureTimer.Start();
    }

    private async Task CaptureTickAsync()
    {
        if (_capture == null || _hostTransport?.IsConnected != true || _isSendingFrame) return;
        _isSendingFrame = true;
        try
        {
            var settings = StreamingSettings.FromPresetIndex(HostQualityIndex);
            var deltas   = _capture.Capture(false, settings.JpegQuality);
            foreach (var delta in deltas)
            {
                bool full = delta.X == 0 && delta.Y == 0 &&
                            delta.Width  == _capture.Resolution.Width &&
                            delta.Height == _capture.Resolution.Height;

                await _hostTransport.SendMessageAsync(new NetworkMessage
                {
                    Type = full ? MessageType.FullScreen : MessageType.ScreenDelta,
                    Data = full ? delta.ImageData : delta.Serialize()
                });

                if (_wsServer != null)
                {
                    if (full) await _wsServer.BroadcastFrameAsync(delta.ImageData);
                    else      await _wsServer.BroadcastDeltaAsync(delta);
                }
            }
        }
        catch (Exception ex) { Status = $"Capture error: {ex.Message}"; }
        finally { _isSendingFrame = false; }
    }

    // ── Incoming messages ─────────────────────────────────────────────────────

    private void OnViewerMessage(NetworkMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.FullScreen:  ApplyFullScreen(msg.Data);                                   break;
            case MessageType.ScreenDelta: ApplyDelta(ScreenDelta.Deserialize(msg.Data));               break;
            case MessageType.HandshakeOk:
                // Host accepted — screen frames will start arriving; nothing to do here.
                break;
            case MessageType.HandshakeFailed:
                Dispatcher.UIThread.Post(() =>
                {
                    Status = "Host rejected: invalid session token";
                    DisconnectViewer();
                });
                break;
            case MessageType.Pong:
                if (msg.Data.Length >= 8)
                    _lastLatencyMs = (int)Math.Max(0,
                        (DateTime.UtcNow - new DateTime(BitConverter.ToInt64(msg.Data, 0), DateTimeKind.Utc))
                        .TotalMilliseconds);
                break;
        }
    }

    private void OnHostMessage(NetworkMessage msg)
    {
        if (!_hostHandshakeComplete)
        {
            HandleHandshake(msg);
            return;
        }

        // Reset the inactivity watchdog on every message.
        ResetSessionWatchdog();

        switch (msg.Type)
        {
            case MessageType.MouseMove:
                var mv = MouseMoveMessage.Deserialize(msg.Data);
                Dispatcher.UIThread.Post(() => _input?.MoveMouse(mv.X, mv.Y));
                break;
            case MessageType.MouseClick:
                var cl = MouseClickMessage.Deserialize(msg.Data);
                Dispatcher.UIThread.Post(() => _input?.Click(cl.X, cl.Y, cl.LeftButton));
                break;
            case MessageType.KeyPress:
                var kp = KeyMessage.Deserialize(msg.Data);
                Dispatcher.UIThread.Post(() => _input?.SendKey(kp.VirtualKey, true));
                break;
            case MessageType.KeyRelease:
                var kr = KeyMessage.Deserialize(msg.Data);
                Dispatcher.UIThread.Post(() => _input?.SendKey(kr.VirtualKey, false));
                break;
            case MessageType.Ping:
                _ = _hostTransport?.SendMessageAsync(new NetworkMessage { Type = MessageType.Pong, Data = msg.Data });
                break;
            case MessageType.QualityPreset:
                if (msg.Data.Length >= 4)
                    Dispatcher.UIThread.Post(() => HostQualityIndex = BitConverter.ToInt32(msg.Data, 0));
                break;
        }
    }

    private void HandleHandshake(NetworkMessage msg)
    {
        if (msg.Type != MessageType.Handshake)
        {
            Dispatcher.UIThread.Post(() => { HostStatus = "Bad handshake — disconnecting"; Status = "Bad handshake"; });
            _hostTransport?.Disconnect();
            return;
        }

        string secret = Encoding.UTF8.GetString(msg.Data);
        if (secret != _expectedSessionSecret)
        {
            _ = _hostTransport?.SendMessageAsync(new NetworkMessage { Type = MessageType.HandshakeFailed });
            Dispatcher.UIThread.Post(() => { HostStatus = "Rejected: invalid token"; Status = "Rejected: invalid session token"; });
            _hostTransport?.Disconnect();
            return;
        }

        _hostHandshakeComplete = true;
        var transport = _hostTransport;
        _ = transport?.SendMessageAsync(new NetworkMessage { Type = MessageType.HandshakeOk });

        // ShowDialog requires the UI thread — use InvokeAsync so the consent dialog
        // and all follow-up state updates run there.
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            bool allowed = ConsentCallback != null
                ? await ConsentCallback("incoming viewer")
                : true;

            if (!allowed)
            {
                HostStatus = "Connection denied";
                Status     = "Connection denied";
                transport?.Disconnect();
                return;
            }

            StartSessionWatchdog();
            HostStatus    = "Client connected — sharing screen";
            IsHostRunning = true;
            Status        = "Client connected";
        });
    }

    // ── Screen rendering ──────────────────────────────────────────────────────

    private void ApplyFullScreen(byte[] data)
    {
        _canvas?.Dispose(); _canvas = null;
        using var ms = new MemoryStream(data);
        var decoded  = new Bitmap(ms);
        int w = (int)decoded.Size.Width, h = (int)decoded.Size.Height;
        EnsureCanvas(w, h);
        using var dc = _canvas!.CreateDrawingContext();
        dc.DrawImage(decoded, new Rect(0, 0, w, h));
        Dispatcher.UIThread.Post(() => { RemoteScreen = _canvas; _renderedFrames++; });
    }

    private void ApplyDelta(ScreenDelta delta)
    {
        EnsureCanvas(
            Math.Max(_canvasWidth,  delta.X + delta.Width),
            Math.Max(_canvasHeight, delta.Y + delta.Height));
        using var ms = new MemoryStream(delta.ImageData);
        var patch    = new Bitmap(ms);
        using var dc = _canvas!.CreateDrawingContext();
        dc.DrawImage(patch, new Rect(delta.X, delta.Y, delta.Width, delta.Height));
        Dispatcher.UIThread.Post(() => { RemoteScreen = _canvas; _renderedFrames++; });
    }

    private void EnsureCanvas(int w, int h)
    {
        if (_canvas != null && _canvasWidth >= w && _canvasHeight >= h) return;
        var old  = _canvas;
        _canvas  = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        _canvasWidth = w; _canvasHeight = h;
        if (old != null)
        {
            using var dc = _canvas.CreateDrawingContext();
            dc.DrawImage(old, new Rect(0, 0, old.Size.Width, old.Size.Height));
            old.Dispose();
        }
    }

    // ── Input forwarding (called from View code-behind) ───────────────────────

    public async void SendMouseMove(int rx, int ry)
    {
        if (_transport?.IsConnected != true) return;
        if (Math.Abs(rx - _lastMousePos.x) < 2 && Math.Abs(ry - _lastMousePos.y) < 2) return;
        _lastMousePos = (rx, ry);
        await _transport.SendMessageAsync(new NetworkMessage
        {
            Type = MessageType.MouseMove,
            Data = new MouseMoveMessage { X = rx, Y = ry }.Serialize()
        });
    }

    public async void SendMouseClick(int rx, int ry, bool leftButton)
    {
        if (_transport?.IsConnected != true) return;
        await _transport.SendMessageAsync(new NetworkMessage
        {
            Type = MessageType.MouseClick,
            Data = new MouseClickMessage { X = rx, Y = ry, LeftButton = leftButton, RightButton = !leftButton }.Serialize()
        });
    }

    public async void SendKey(int vk, bool isDown)
    {
        if (_transport?.IsConnected != true || vk == 0) return;
        await _transport.SendMessageAsync(new NetworkMessage
        {
            Type = isDown ? MessageType.KeyPress : MessageType.KeyRelease,
            Data = new KeyMessage { VirtualKey = vk, IsKeyDown = isDown }.Serialize()
        });
    }

    // ── Metrics ───────────────────────────────────────────────────────────────

    private void StartMetricsTimer()
    {
        _metricsTimer = new System.Timers.Timer(1000);
        _metricsTimer.Elapsed += (_, _) =>
        {
            int fps = _renderedFrames, bytes = _receivedBytes;
            _renderedFrames = 0; _receivedBytes = 0;
            string lat = _lastLatencyMs >= 0 ? $"{_lastLatencyMs} ms" : "—";
            Dispatcher.UIThread.Post(() =>
            {
                FpsText       = $"FPS: {fps}";
                LatencyText   = $"Latency: {lat}";
                BandwidthText = $"↓ {bytes / 1024.0:0} KB/s";
            });
        };
        _metricsTimer.Start();

        _pingTimer = new System.Timers.Timer(2000) { AutoReset = true };
        _pingTimer.Elapsed += async (_, _) =>
        {
            if (_transport?.IsConnected == true)
                await _transport.SendMessageAsync(new NetworkMessage
                {
                    Type = MessageType.Ping,
                    Data = BitConverter.GetBytes(DateTime.UtcNow.Ticks)
                });
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string host, int port) ParseAddress(string addr)
    {
        var p = addr.Split(':');
        return (p[0], p.Length > 1 ? int.Parse(p[1]) : 9000);
    }

    private static string GetLocalEndpoint(string routerHost)
    {
        if (routerHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            routerHost == "127.0.0.1")
            return "localhost:8888";

        return $"{GetLanIp()}:8888";
    }

    public void Dispose()
    {
        StopHost();
        DisconnectViewer();
        _metricsTimer?.Dispose();
        _pingTimer?.Dispose();
        _sessionWatchdog?.Dispose();
        _canvas?.Dispose();
    }
}
