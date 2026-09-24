using System;
using System.Text;
using System.Threading.Tasks;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;

namespace PixelWizard.Session;

/// <summary>
/// Host-side session: owns the transport a viewer connects to and classifies inbound
/// messages (formerly <c>MainViewModel.OnHostMessage</c>/<c>HandleHello</c>/
/// <c>HandleHandshake</c>, moved here in T9.2b). See <see cref="ViewerSession"/> for the
/// zero-Dispatcher rationale and the events-outward pattern; the same applies here.
///
/// <c>_helloComplete</c>/<c>_handshakeComplete</c> move fully into this class -- unlike
/// ViewerSession's shared fields, nothing outside the old OnHostMessage/HandleHello/
/// HandleHandshake trio ever read or wrote them. <c>_expectedSessionSecret</c> and
/// <c>ourHello</c> are supplied at construction, the same way ViewerSession takes
/// <c>sessionSecret</c> -- both are per-connection config already known before the
/// transport is built.
///
/// Deliberately NOT constructor-injected: the consent callback. It shows an Avalonia
/// dialog (<c>ConsentDialog.Show()</c> in App.axaml.cs), which requires UI-thread
/// affinity -- exactly what this project cannot reference. Handing the delegate to this
/// class and awaiting it here would run that dialog off the UI thread. So the handshake
/// only gets as far as the protocol-level "token verified, HandshakeOk sent" step and
/// raises <see cref="HandshakeVerified"/>; MainViewModel keeps the
/// <c>Dispatcher.UIThread.InvokeAsync(async () => await ConsentCallback(...))</c> block
/// exactly as it stood before this move. This is a deliberate deviation from the T9.1
/// plan note anticipating a Func&lt;string, Task&lt;bool&gt;&gt; constructor parameter --
/// once HandleHandshake's actual code was in front of me, injecting the delegate here
/// turned out to be the wrong shape, not just an unused option.
/// </summary>
public sealed class HostSession : IDisposable
{
    private readonly ISessionTransport _transport;
    private readonly HelloMessage _ourHello;
    private readonly string _expectedSessionSecret;
    private bool _helloComplete;
    private bool _handshakeComplete;

    public HostSession(ISessionTransport transport, HelloMessage ourHello, string expectedSessionSecret = "")
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ourHello = ourHello ?? throw new ArgumentNullException(nameof(ourHello));
        _expectedSessionSecret = expectedSessionSecret;
        _transport.Connected += () =>
        {
            _helloComplete = false;
            _handshakeComplete = false;
            Connected?.Invoke();
        };
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

    // Hello phase.
    public event Action? IncompatiblePeer;
    public event Action? BadHello;
    public event Action? MalformedHello;
    public event Action<string>? HelloRejectedLocally;

    // Handshake phase.
    public event Action? BadHandshake;
    public event Action? HandshakeTokenInvalid;
    // Fired once the token is verified and HandshakeOk is already on the wire -- consent
    // (UI-thread, may show a dialog) is MainViewModel's job from here, per the class comment.
    public event Action? HandshakeVerified;

    // Post-handshake: fired for every message before classification, mirroring the
    // ResetSessionWatchdog() call site in the original OnHostMessage.
    public event Action? PostHandshakeMessageReceived;

    public event Action<MouseMoveMessage>? MouseMoveReceived;
    public event Action<MouseClickMessage>? MouseClickReceived;
    public event Action<MouseClickMessage>? MouseButtonDownReceived;
    public event Action<MouseClickMessage>? MouseButtonUpReceived;
    public event Action<KeyMessage>? KeyPressReceived;
    public event Action<KeyMessage>? KeyReleaseReceived;
    public event Action<int>? QualityChanged;
    public event Action<string>? ClipboardReceived;
    public event Action<string>? ChatReceived;

    public Task StartServerAsync(int port, bool useTls = true) => _transport.StartServerAsync(port, useTls);
    public Task SendMessageAsync(NetworkMessage message) => _transport.SendMessageAsync(message);
    public void Disconnect() => _transport.Disconnect();

    private void OnTransportMessageReceived(NetworkMessage msg)
    {
        if (!_helloComplete)
        {
            HandleHello(msg);
            return;
        }

        if (!_handshakeComplete)
        {
            HandleHandshake(msg);
            return;
        }

        PostHandshakeMessageReceived?.Invoke();

        // What to do with this message is a pure function of its MessageType
        // (MessageDispatch.ClassifyForHost, exhaustively unit tested in
        // MessageDispatchTests) -- only how each category is carried out below still
        // touches state.
        switch (MessageDispatch.ClassifyForHost(msg.Type))
        {
            case HostDispatchAction.MouseMove:
                MouseMoveReceived?.Invoke(MouseMoveMessage.Deserialize(msg.Data));
                break;
            case HostDispatchAction.MouseClick:
                MouseClickReceived?.Invoke(MouseClickMessage.Deserialize(msg.Data));
                break;
            case HostDispatchAction.MouseButtonDown:
                MouseButtonDownReceived?.Invoke(MouseClickMessage.Deserialize(msg.Data));
                break;
            case HostDispatchAction.MouseButtonUp:
                MouseButtonUpReceived?.Invoke(MouseClickMessage.Deserialize(msg.Data));
                break;
            case HostDispatchAction.KeyPress:
                KeyPressReceived?.Invoke(KeyMessage.Deserialize(msg.Data));
                break;
            case HostDispatchAction.KeyRelease:
                KeyReleaseReceived?.Invoke(KeyMessage.Deserialize(msg.Data));
                break;
            case HostDispatchAction.PingReply:
                _ = _transport.SendMessageAsync(new NetworkMessage { Type = MessageType.Pong, Data = msg.Data });
                break;
            case HostDispatchAction.QualityChanged:
                if (msg.Data.Length >= 4)
                    QualityChanged?.Invoke(BitConverter.ToInt32(msg.Data, 0));
                break;
            case HostDispatchAction.Clipboard:
                ClipboardReceived?.Invoke(Encoding.UTF8.GetString(msg.Data));
                break;
            case HostDispatchAction.Chat:
                ChatReceived?.Invoke(Encoding.UTF8.GetString(msg.Data));
                break;
        }
    }

    private void HandleHello(NetworkMessage msg)
    {
        if (HelloCompatibility.LooksLikeV1Peer(msg.Type))
        {
            // A v1 viewer has no concept of Hello -- its first message is always Handshake.
            // This is a distinct, positively-identified case, not a generic bad-hello
            // failure: the peer isn't malformed, it's just too old to negotiate at all.
            IncompatiblePeer?.Invoke();
            _transport.Disconnect();
            return;
        }

        if (msg.Type != MessageType.Hello)
        {
            BadHello?.Invoke();
            _transport.Disconnect();
            return;
        }

        HelloMessage remoteHello;
        try
        {
            remoteHello = HelloMessage.Deserialize(msg.Data);
        }
        catch (Exception)
        {
            MalformedHello?.Invoke();
            _transport.Disconnect();
            return;
        }

        var rejectReason = HelloNegotiator.Evaluate(_ourHello, remoteHello);
        if (rejectReason != null)
        {
            string reasonText = rejectReason == HelloRejectReason.VersionMismatch
                ? $"viewer protocol v{remoteHello.ProtocolVersion} is incompatible with this host's v{ProtocolVersions.Current}"
                : "viewer and host share no compatible codec";
            _ = _transport.SendMessageAsync(new NetworkMessage
            {
                Type = MessageType.HelloRejected,
                Data = new HelloRejectedMessage { Reason = rejectReason.Value, Message = reasonText }.Serialize()
            });
            HelloRejectedLocally?.Invoke(reasonText);
            _transport.Disconnect();
            return;
        }

        _helloComplete = true;
        _ = _transport.SendMessageAsync(new NetworkMessage
        {
            Type = MessageType.HelloAck,
            Data = _ourHello.Serialize()
        });
    }

    private void HandleHandshake(NetworkMessage msg)
    {
        if (msg.Type != MessageType.Handshake)
        {
            BadHandshake?.Invoke();
            _transport.Disconnect();
            return;
        }

        string secret = Encoding.UTF8.GetString(msg.Data);
        // For a direct (non-router) connection, _expectedSessionSecret is never set and
        // stays "", matching the "" the host sends for the same reason -- there is no
        // channel to share a secret out-of-band when a user just types an IP address, so
        // this check is a deliberate no-op on that path, not a bug. The consent dialog
        // (MainViewModel, after HandshakeVerified) is the sole gate for direct connections.
        // See README "Security" for the full trust model of both connection paths.
        if (secret != _expectedSessionSecret)
        {
            _ = _transport.SendMessageAsync(new NetworkMessage { Type = MessageType.HandshakeFailed });
            HandshakeTokenInvalid?.Invoke();
            _transport.Disconnect();
            return;
        }

        _handshakeComplete = true;
        _ = _transport.SendMessageAsync(new NetworkMessage { Type = MessageType.HandshakeOk });
        HandshakeVerified?.Invoke();
    }

    public void Dispose() => _transport.Dispose();
}
