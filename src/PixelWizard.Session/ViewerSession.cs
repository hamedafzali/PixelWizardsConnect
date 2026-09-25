using System;
using System.Text;
using System.Threading.Tasks;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;

namespace PixelWizard.Session;

/// <summary>
/// Viewer-side session: owns the transport to a host and classifies inbound messages
/// (formerly <c>MainViewModel.OnViewerMessage</c>, moved here in T9.2a). See
/// <see cref="HostSession"/> for the zero-Dispatcher rationale; the same applies here --
/// this class raises plain events for whatever a case needs to do to UI-bound state
/// (<c>MainViewModel</c>'s fields, <c>Dispatcher.UIThread</c>) and only does the
/// protocol-level work (deserializing, replying on the wire) itself.
///
/// <c>_hostPeerRole</c>/<c>_sessionSecret</c>/<c>_lastLatencyMs</c> stay MainViewModel
/// fields, not properties here: each is also read or written from code that has not moved
/// (input-send guards, ConnectDirect/ConnectViaCode, the metrics timer), so the session
/// only ever reports a new value outward -- it never becomes the owner of state something
/// outside dispatch depends on.
///
/// T9.3a moved the connect lifecycle's protocol half here: sending Hello on connect and
/// tracking whether it has been answered (formerly MainViewModel's
/// <c>_awaitingHelloResponse</c>). The transport comes from an injected factory so this
/// project never references a concrete transport (Transport.Tcp today, WebRTC in Phase 4).
/// </summary>
public sealed class ViewerSession : IDisposable
{
    private readonly ISessionTransport _transport;
    private readonly HelloMessage _ourHello;
    private readonly string _sessionSecret;

    // True from the moment Hello is sent until either any reply arrives or the connection
    // drops -- the viewer-side heuristic for detecting a v1 host (see
    // HelloUnansweredAtDisconnect).
    private bool _awaitingHelloResponse;

    public ViewerSession(Func<ISessionTransport> transportFactory, HelloMessage ourHello, string sessionSecret = "")
    {
        if (transportFactory == null) throw new ArgumentNullException(nameof(transportFactory));
        _transport = transportFactory() ?? throw new InvalidOperationException("transportFactory returned null");
        _ourHello = ourHello ?? throw new ArgumentNullException(nameof(ourHello));
        _sessionSecret = sessionSecret;
        _transport.Connected += async () =>
        {
            _awaitingHelloResponse = true;
            await _transport.SendMessageAsync(new NetworkMessage
            {
                Type = MessageType.Hello,
                Data = _ourHello.Serialize()
            });
        };
        _transport.Connected += () => Connected?.Invoke();
        _transport.Disconnected += () =>
        {
            HelloUnansweredAtDisconnect = _awaitingHelloResponse;
            _awaitingHelloResponse = false;
            Disconnected?.Invoke();
        };
        _transport.Error += ex => Error?.Invoke(ex);
        _transport.HandlerError += ex => HandlerError?.Invoke(ex);
        _transport.BytesReceived += n => BytesReceived?.Invoke(n);
        _transport.BytesSent += n => BytesSent?.Invoke(n);
        _transport.MessageReceived += OnTransportMessageReceived;
    }

    public bool IsConnected => _transport.IsConnected;

    // A v1 host has no concept of Hello: its own strict pre-handshake gate sees an
    // unrecognized message type and disconnects immediately with zero bytes sent back
    // (nothing like HelloAck/HelloRejected is possible from a build that predates them). So
    // "we sent Hello and the connection closed before any reply arrived" is the only
    // observable signal from this side -- not a positive identification (an ordinary network
    // drop in that same narrow window looks identical), so callers should word it hedged.
    // Snapshotted when the transport reports Disconnected, just before this session's
    // Disconnected fires; read it synchronously inside that handler -- the transport can
    // report Disconnected more than once, and each report takes a fresh snapshot.
    public bool HelloUnansweredAtDisconnect { get; private set; }

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<Exception>? Error;
    public event Action<Exception>? HandlerError;
    public event Action<int>? BytesReceived;
    public event Action<int>? BytesSent;

    public event Action<byte[]>? FullScreenReceived;
    public event Action<ScreenDelta>? ScreenDeltaReceived;
    public event Action<HelloMessage>? HostHelloAcknowledged;
    public event Action<HelloRejectedMessage>? HostHelloRejected;
    public event Action? HandshakeRejected;
    public event Action<int>? LatencyMeasured;
    public event Action<string>? ClipboardReceived;
    public event Action<string>? ChatReceived;

    public Task ConnectAsync(string host, int port, bool useTls = true) => _transport.ConnectAsync(host, port, useTls);
    public void Disconnect() => _transport.Disconnect();

    // Outbound message construction (formerly inline in MainViewModel's input/ping/quality/
    // clipboard/chat senders, moved here in T9.4). Guards -- IsConnected, the host's
    // PeerRole, mouse-move de-dupe -- stay with the caller: they read MainViewModel state.
    public Task SendMouseMoveAsync(int x, int y) => Send(MessageType.MouseMove,
        new MouseMoveMessage { X = x, Y = y }.Serialize());
    public Task SendMouseClickAsync(int x, int y, bool leftButton) => Send(MessageType.MouseClick, Click(x, y, leftButton));
    public Task SendMouseDownAsync(int x, int y, bool leftButton) => Send(MessageType.MouseButtonDown, Click(x, y, leftButton));
    public Task SendMouseUpAsync(int x, int y, bool leftButton) => Send(MessageType.MouseButtonUp, Click(x, y, leftButton));
    public Task SendKeyAsync(int virtualKey, bool isDown) => Send(isDown ? MessageType.KeyPress : MessageType.KeyRelease,
        new KeyMessage { VirtualKey = virtualKey, IsKeyDown = isDown }.Serialize());
    public Task SendQualityPresetAsync(int index) => Send(MessageType.QualityPreset, BitConverter.GetBytes(index));
    // Carries UtcNow ticks; the host echoes them in a Pong, which LatencyMeasured reports.
    public Task SendPingAsync() => Send(MessageType.Ping, BitConverter.GetBytes(DateTime.UtcNow.Ticks));
    public Task SendClipboardAsync(string text) => Send(MessageType.ClipboardText, Encoding.UTF8.GetBytes(text));
    public Task SendChatAsync(string text) => Send(MessageType.ChatMessage, Encoding.UTF8.GetBytes(text));

    private static byte[] Click(int x, int y, bool leftButton) =>
        new MouseClickMessage { X = x, Y = y, LeftButton = leftButton, RightButton = !leftButton }.Serialize();
    private Task Send(MessageType type, byte[] data) =>
        _transport.SendMessageAsync(new NetworkMessage { Type = type, Data = data });

    // What to do with a message is a pure function of its MessageType
    // (MessageDispatch.ClassifyForViewer, exhaustively unit tested in MessageDispatchTests)
    // -- only how each category is carried out below still touches state, and here that
    // means either replying on the wire or raising an event for MainViewModel to act on.
    private void OnTransportMessageReceived(NetworkMessage msg)
    {
        // Unconditional, before classification: any reply at all proves the host is
        // Hello-aware, regardless of what it turns out to be.
        _awaitingHelloResponse = false;

        switch (MessageDispatch.ClassifyForViewer(msg.Type))
        {
            case ViewerDispatchAction.ApplyFullScreen:
                FullScreenReceived?.Invoke(msg.Data);
                break;
            case ViewerDispatchAction.ApplyScreenDelta:
                ScreenDeltaReceived?.Invoke(ScreenDelta.Deserialize(msg.Data));
                break;
            case ViewerDispatchAction.HostHelloAck:
                var hostHello = HelloMessage.Deserialize(msg.Data);
                _ = _transport.SendMessageAsync(new NetworkMessage
                {
                    Type = MessageType.Handshake,
                    Data = Encoding.UTF8.GetBytes(_sessionSecret)
                });
                HostHelloAcknowledged?.Invoke(hostHello);
                break;
            case ViewerDispatchAction.HostHelloRejected:
                HostHelloRejected?.Invoke(HelloRejectedMessage.Deserialize(msg.Data));
                break;
            case ViewerDispatchAction.HandshakeAcknowledged:
                break;
            case ViewerDispatchAction.HandshakeRejected:
                HandshakeRejected?.Invoke();
                break;
            case ViewerDispatchAction.LatencyPong:
                if (msg.Data.Length >= 8)
                    LatencyMeasured?.Invoke((int)Math.Max(0,
                        (DateTime.UtcNow - new DateTime(BitConverter.ToInt64(msg.Data, 0), DateTimeKind.Utc))
                        .TotalMilliseconds));
                break;
            case ViewerDispatchAction.Clipboard:
                ClipboardReceived?.Invoke(Encoding.UTF8.GetString(msg.Data));
                break;
            case ViewerDispatchAction.Chat:
                ChatReceived?.Invoke(Encoding.UTF8.GetString(msg.Data));
                break;
        }
    }

    public void Dispose() => _transport.Dispose();
}
