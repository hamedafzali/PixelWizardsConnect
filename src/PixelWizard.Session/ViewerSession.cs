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
/// <c>_hostPeerRole</c>/<c>_sessionSecret</c>/<c>_awaitingHelloResponse</c>/
/// <c>_lastLatencyMs</c> stay MainViewModel fields, not properties here: each is also
/// read or written from code that has not moved (input-send guards, ConnectDirect/
/// ConnectViaCode, the metrics timer), so the session only ever reports a new value
/// outward -- it never becomes the owner of state something outside dispatch depends on.
/// </summary>
public sealed class ViewerSession : IDisposable
{
    private readonly ISessionTransport _transport;
    private readonly string _sessionSecret;

    public ViewerSession(ISessionTransport transport, string sessionSecret = "")
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _sessionSecret = sessionSecret;
        _transport.Connected += () => Connected?.Invoke();
        _transport.Disconnected += () => Disconnected?.Invoke();
        _transport.Error += ex => Error?.Invoke(ex);
        _transport.HandlerError += ex => HandlerError?.Invoke(ex);
        _transport.BytesReceived += n => BytesReceived?.Invoke(n);
        _transport.BytesSent += n => BytesSent?.Invoke(n);
        _transport.MessageReceived += OnTransportMessageReceived;
    }

    public bool IsConnected => _transport.IsConnected;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<Exception>? Error;
    public event Action<Exception>? HandlerError;
    public event Action<int>? BytesReceived;
    public event Action<int>? BytesSent;

    // Fired for every inbound message, before classification -- mirrors the unconditional
    // "_awaitingHelloResponse = false" that used to sit at the top of OnViewerMessage: any
    // reply at all proves the host is Hello-aware, regardless of what it turns out to be.
    public event Action? AnyMessageReceived;

    public event Action<byte[]>? FullScreenReceived;
    public event Action<ScreenDelta>? ScreenDeltaReceived;
    public event Action<HelloMessage>? HostHelloAcknowledged;
    public event Action<HelloRejectedMessage>? HostHelloRejected;
    public event Action? HandshakeRejected;
    public event Action<int>? LatencyMeasured;
    public event Action<string>? ClipboardReceived;
    public event Action<string>? ChatReceived;

    public Task ConnectAsync(string host, int port, bool useTls = true) => _transport.ConnectAsync(host, port, useTls);
    public Task SendMessageAsync(NetworkMessage message) => _transport.SendMessageAsync(message);
    public void Disconnect() => _transport.Disconnect();

    // What to do with a message is a pure function of its MessageType
    // (MessageDispatch.ClassifyForViewer, exhaustively unit tested in MessageDispatchTests)
    // -- only how each category is carried out below still touches state, and here that
    // means either replying on the wire or raising an event for MainViewModel to act on.
    private void OnTransportMessageReceived(NetworkMessage msg)
    {
        AnyMessageReceived?.Invoke();

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
