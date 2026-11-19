using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PixelWizard.Signaling
{
    /// <summary>Result of <see cref="SignalingClient.RegisterAsync"/> / <see cref="SignalingClient.ResumeAsync"/>.</summary>
    public sealed record SignalingRegistration(string Code, string ResumeToken);

    /// <summary>
    /// Client for the router's <c>/signal</c> WebSocket (docs/SIGNALING.md). It moves SDP strings and
    /// candidate records between two peers; it knows nothing about peer connections.
    ///
    /// One client is one socket and one session. Host: <see cref="RegisterAsync"/>, then
    /// <see cref="WaitForPeerAsync"/>. Viewer: <see cref="JoinAsync"/>. Afterwards, send with the
    /// Send* methods and consume <see cref="ReadMessagesAsync"/>, which yields offer, answer,
    /// candidate, end-of-candidates and bye as they arrive and ends with peer-left.
    ///
    /// Reconnecting: if a registered, unpaired host socket drops, create a NEW client and call
    /// <see cref="ResumeAsync"/> with the token from the first registration, within the router's
    /// 60 s window. It yields the same code. There is no automatic retry.
    /// </summary>
    public sealed class SignalingClient : IAsyncDisposable
    {
        private const int MaxFrame = 64 * 1024; // router limit (docs/SIGNALING.md)

        private readonly Uri _uri;
        private readonly ClientWebSocket _socket = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1); // ClientWebSocket allows one send at a time
        private readonly Channel<SignalingMessage> _incoming = Channel.CreateUnbounded<SignalingMessage>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly CancellationTokenSource _lifetime = new();
        private Task? _receiveLoop;
        private bool _started;
        private int _disposed;

        /// <param name="routerHost">Host, optionally with http:// or https:// (https gives wss).</param>
        /// <param name="routerPort">Router port; 443 for https and 80 for http are left out of the URL.</param>
        public SignalingClient(string routerHost, int routerPort) => _uri = SignalUri(routerHost, routerPort);

        /// <summary>The room code once registered, resumed or joined.</summary>
        public string? Code { get; private set; }

        internal static Uri SignalUri(string routerHost, int routerPort)
        {
            string http = RouterHttpClient.BaseUrl(routerHost, routerPort);
            string ws = http.StartsWith("https://", StringComparison.Ordinal) ? "wss://" + http.Substring(8) : "ws://" + http.Substring(7);
            return new Uri(ws + "/signal");
        }

        /// <summary>Host: opens a socket and registers. The router replies with the code and a resume token.</summary>
        public async Task<SignalingRegistration> RegisterAsync(CancellationToken ct = default)
        {
            await ConnectAsync(new RegisterMessage(), ct).ConfigureAwait(false);
            var reply = await ExpectAsync<RegisteredMessage>(ct).ConfigureAwait(false);
            Code = reply.Code;
            StartReceiving();
            return new SignalingRegistration(reply.Code, reply.ResumeToken);
        }

        /// <summary>Host: opens a socket and resumes a registration whose socket dropped. Same code as before.</summary>
        public async Task<SignalingRegistration> ResumeAsync(string resumeToken, CancellationToken ct = default)
        {
            await ConnectAsync(new ResumeMessage(resumeToken), ct).ConfigureAwait(false);
            var reply = await ExpectAsync<ResumedMessage>(ct).ConfigureAwait(false);
            Code = reply.Code;
            StartReceiving();
            return new SignalingRegistration(reply.Code, resumeToken);
        }

        /// <summary>Viewer: opens a socket and joins the room. The reply may carry TURN credentials.</summary>
        public async Task<JoinedMessage> JoinAsync(string code, CancellationToken ct = default)
        {
            await ConnectAsync(new JoinMessage(code), ct).ConfigureAwait(false);
            var reply = await ExpectAsync<JoinedMessage>(ct).ConfigureAwait(false);
            Code = code;
            StartReceiving();
            return reply;
        }

        /// <summary>Host: waits until a viewer joins. Must be the first read after register/resume.</summary>
        public async Task<PeerJoinedMessage> WaitForPeerAsync(CancellationToken ct = default)
        {
            if (!await _incoming.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("The signaling socket closed before a peer joined.");
            if (_incoming.Reader.TryRead(out var msg) && msg is PeerJoinedMessage pj) return pj;
            throw new InvalidDataException($"Expected peer-joined, got {msg?.Type}.");
        }

        /// <summary>
        /// Relayed messages in arrival order, each yielded as soon as it is read. Ends after
        /// <see cref="PeerLeftMessage"/> (yielded last) or when the socket closes. A router
        /// <c>error</c> or a broken socket surfaces as an exception.
        /// </summary>
        public IAsyncEnumerable<SignalingMessage> ReadMessagesAsync(CancellationToken ct = default) =>
            _incoming.Reader.ReadAllAsync(ct);

        public Task SendOfferAsync(string sdp, CancellationToken ct = default) => SendAsync(new OfferMessage(sdp), ct);
        public Task SendAnswerAsync(string sdp, CancellationToken ct = default) => SendAsync(new AnswerMessage(sdp), ct);
        public Task SendCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex, CancellationToken ct = default) =>
            SendAsync(new CandidateMessage(candidate, sdpMid, sdpMLineIndex), ct);
        public Task SendEndOfCandidatesAsync(CancellationToken ct = default) => SendAsync(new EndOfCandidatesMessage(), ct);
        public Task SendByeAsync(string? reason = null, CancellationToken ct = default) => SendAsync(new ByeMessage(reason), ct);

        private async Task SendAsync(SignalingMessage msg, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(SignalingJson.Serialize(msg));
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try { await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { _sendLock.Release(); }
        }

        private async Task ConnectAsync(SignalingMessage first, CancellationToken ct)
        {
            if (_started) throw new InvalidOperationException("A SignalingClient is one session; create a new one.");
            _started = true;
            try
            {
                await _socket.ConnectAsync(_uri, ct).ConfigureAwait(false);
                await SendAsync(first, ct).ConfigureAwait(false);
            }
            catch { await DisposeAsync().ConfigureAwait(false); throw; }
        }

        // Handshake replies are read inline, before the receive loop exists, so there is only ever one reader.
        private async Task<T> ExpectAsync<T>(CancellationToken ct) where T : SignalingMessage
        {
            try
            {
                while (true)
                {
                    string? frame = await ReceiveFrameAsync(ct).ConfigureAwait(false);
                    if (frame == null) throw new InvalidOperationException("The router closed the signaling socket.");
                    var msg = SignalingJson.Parse(frame);
                    if (msg == null) continue; // unknown type: ignored
                    if (msg is ErrorMessage e) throw new SignalingErrorException(e.Code, e.Message);
                    if (msg is T t) return t;
                    throw new InvalidDataException($"Unexpected '{msg.Type}' during handshake.");
                }
            }
            catch { await DisposeAsync().ConfigureAwait(false); throw; }
        }

        private void StartReceiving() => _receiveLoop = Task.Run(() => ReceiveLoopAsync(_lifetime.Token));

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    string? frame = await ReceiveFrameAsync(ct).ConfigureAwait(false);
                    if (frame == null) break;
                    var msg = SignalingJson.Parse(frame);
                    if (msg == null) continue; // unknown type: ignored
                    if (msg is ErrorMessage e) throw new SignalingErrorException(e.Code, e.Message);
                    _incoming.Writer.TryWrite(msg);
                    if (msg is PeerLeftMessage) break;
                }
                _incoming.Writer.TryComplete();
            }
            catch (OperationCanceledException) { _incoming.Writer.TryComplete(); }
            catch (Exception ex) { _incoming.Writer.TryComplete(ex); }
        }

        /// <summary>One text frame, or null when the peer closed the socket.</summary>
        private async Task<string?> ReceiveFrameAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                if (r.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Binary signaling frame.");
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > MaxFrame) throw new InvalidDataException("Signaling frame too large.");
                if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }

        /// <summary>Closes the socket (close handshake, bounded to a few seconds) and stops the receive loop.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).ConfigureAwait(false); }
                    catch { /* already gone */ }
                    if (_receiveLoop != null)
                        try { await _receiveLoop.WaitAsync(timeout.Token).ConfigureAwait(false); }
                        catch { /* peer slow to answer the close: cancel below */ }
                }
            }
            finally
            {
                _lifetime.Cancel();
                if (_receiveLoop != null) try { await _receiveLoop.ConfigureAwait(false); } catch { }
                _incoming.Writer.TryComplete();
                _socket.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
