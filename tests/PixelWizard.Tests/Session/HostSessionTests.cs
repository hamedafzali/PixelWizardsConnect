using System;
using System.Text;
using PixelWizard.Protocol;
using PixelWizard.Session;
using Xunit;

namespace PixelWizard.Tests.Session;

/// <summary>
/// T9.1's behavioral gate: HostSession must forward its transport's lifecycle events
/// untouched (no logic, no dropped events) and forward method calls to the same transport
/// instance it was constructed with. Dispatch classification (OnHostMessage/HandleHello/
/// HandleHandshake) isn't here yet -- that's T9.2b, gated separately by the live Hello
/// socket test.
/// </summary>
public class HostSessionTests
{
    private static readonly HelloMessage TestHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };

    [Fact]
    public void Connected_IsForwarded()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        bool fired = false;
        session.Connected += () => fired = true;

        fake.RaiseConnected();

        Assert.True(fired);
    }

    [Fact]
    public void Disconnected_IsForwarded()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        bool fired = false;
        session.Disconnected += () => fired = true;

        fake.RaiseDisconnected();

        Assert.True(fired);
    }

    [Fact]
    public void Error_IsForwardedWithSameException()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        Exception? seen = null;
        session.Error += ex => seen = ex;
        var thrown = new InvalidOperationException("boom");

        fake.RaiseError(thrown);

        Assert.Same(thrown, seen);
    }

    [Fact]
    public void HandlerError_IsForwardedWithSameException()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        Exception? seen = null;
        session.HandlerError += ex => seen = ex;
        var thrown = new InvalidOperationException("handler boom");

        fake.RaiseHandlerError(thrown);

        Assert.Same(thrown, seen);
    }

    [Fact]
    public void BytesReceivedAndSent_AreForwarded()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        int? received = null, sent = null;
        session.BytesReceived += n => received = n;
        session.BytesSent += n => sent = n;

        fake.RaiseBytesReceived(42);
        fake.RaiseBytesSent(7);

        Assert.Equal(42, received);
        Assert.Equal(7, sent);
    }

    [Fact]
    public void IsConnected_ReflectsTransport()
    {
        var fake = new FakeSessionTransport { IsConnected = true };
        var session = new HostSession(fake, TestHello);

        Assert.True(session.IsConnected);
    }

    [Fact]
    public async System.Threading.Tasks.Task StartServerAsync_DelegatesToTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);

        await session.StartServerAsync(5555, useTls: false);

        Assert.Equal((5555, false), fake.LastStartServerArgs);
    }

    [Fact]
    public async System.Threading.Tasks.Task SendMessageAsync_DelegatesToTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        var msg = new NetworkMessage { Type = MessageType.ChatMessage, Data = new byte[] { 1, 2, 3 } };

        await session.SendMessageAsync(msg);

        Assert.Same(msg, fake.LastSentMessage);
    }

    [Fact]
    public void Disconnect_DelegatesToTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);

        session.Disconnect();

        Assert.Equal(1, fake.DisconnectCallCount);
    }

    [Fact]
    public void Dispose_DisposesTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);

        session.Dispose();

        Assert.True(fake.Disposed);
    }

    [Fact]
    public void Constructor_NullTransport_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new HostSession(null!, TestHello));
    }

    // ── T9.2b: Hello/Handshake/dispatch behavioral gate ─────────────────────────────────
    // Per the standing note carried from T9.2a's directive: consent is safety-critical UI,
    // not just control flow, and a refactor is exactly where a branch quietly disappears.
    // These tests drive HostSession purely through its public surface (no reading the
    // implementation) and assert on what actually went out over the fake transport and
    // which events actually fired -- proving the state machine, not the diff.

    private static NetworkMessage HelloMsg(HelloMessage hello) =>
        new() { Type = MessageType.Hello, Data = hello.Serialize() };

    [Fact]
    public void ValidHello_SendsHelloAck_AndDoesNotFireHandshakeVerified()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        bool handshakeVerified = false;
        session.HandshakeVerified += () => handshakeVerified = true;

        fake.RaiseMessageReceived(HelloMsg(TestHello));

        Assert.Equal(MessageType.HelloAck, fake.LastSentMessage!.Type);
        Assert.Equal(TestHello.Serialize(), fake.LastSentMessage.Data);
        Assert.False(handshakeVerified);
    }

    [Fact]
    public void ValidHandshake_AfterHello_SendsHandshakeOk_AndFiresHandshakeVerifiedOnce()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello, expectedSessionSecret: "s3cr3t");
        int handshakeVerifiedCount = 0;
        session.HandshakeVerified += () => handshakeVerifiedCount++;

        fake.RaiseMessageReceived(HelloMsg(TestHello));
        fake.RaiseMessageReceived(new NetworkMessage
        {
            Type = MessageType.Handshake,
            Data = Encoding.UTF8.GetBytes("s3cr3t")
        });

        Assert.Equal(MessageType.HandshakeOk, fake.LastSentMessage!.Type);
        Assert.Equal(1, handshakeVerifiedCount);
    }

    [Fact]
    public void V1Peer_FirstMessageHandshake_FiresIncompatiblePeer_Disconnects_NoHandshakeVerified()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        bool incompatible = false, handshakeVerified = false;
        session.IncompatiblePeer += () => incompatible = true;
        session.HandshakeVerified += () => handshakeVerified = true;

        fake.RaiseMessageReceived(new NetworkMessage { Type = MessageType.Handshake, Data = Array.Empty<byte>() });

        Assert.True(incompatible);
        Assert.Equal(1, fake.DisconnectCallCount);
        Assert.False(handshakeVerified);
        Assert.Null(fake.LastSentMessage); // v1 detection sends nothing back
    }

    [Fact]
    public void BadHelloType_FiresBadHello_Disconnects()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        bool badHello = false;
        session.BadHello += () => badHello = true;

        fake.RaiseMessageReceived(new NetworkMessage { Type = MessageType.ChatMessage, Data = Array.Empty<byte>() });

        Assert.True(badHello);
        Assert.Equal(1, fake.DisconnectCallCount);
    }

    [Fact]
    public void MalformedHelloPayload_FiresMalformedHello_Disconnects()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        bool malformed = false;
        session.MalformedHello += () => malformed = true;

        // HelloMessage.Deserialize reads 4 fixed bytes -- an empty payload throws.
        fake.RaiseMessageReceived(new NetworkMessage { Type = MessageType.Hello, Data = Array.Empty<byte>() });

        Assert.True(malformed);
        Assert.Equal(1, fake.DisconnectCallCount);
    }

    [Fact]
    public void VersionMismatchHello_RejectsWithCorrectReason_Disconnects_NoHandshakeVerified()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        string? rejectedReason = null;
        bool handshakeVerified = false;
        session.HelloRejectedLocally += reason => rejectedReason = reason;
        session.HandshakeVerified += () => handshakeVerified = true;

        var mismatched = new HelloMessage
        {
            ProtocolVersion = unchecked((byte)(TestHello.ProtocolVersion + 1)),
            Role = PeerRole.Full,
            Codecs = SupportedCodecs.Jpeg,
            MaxConcurrentStreams = 1
        };

        fake.RaiseMessageReceived(HelloMsg(mismatched));

        Assert.NotNull(rejectedReason);
        Assert.Contains("incompatible", rejectedReason);
        Assert.Equal(MessageType.HelloRejected, fake.LastSentMessage!.Type);
        var sent = HelloRejectedMessage.Deserialize(fake.LastSentMessage.Data);
        Assert.Equal(HelloRejectReason.VersionMismatch, sent.Reason);
        Assert.Equal(1, fake.DisconnectCallCount);
        Assert.False(handshakeVerified);
    }

    [Fact]
    public void IncompatibleCodecsHello_RejectsWithCorrectReason_Disconnects()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        string? rejectedReason = null;
        session.HelloRejectedLocally += reason => rejectedReason = reason;

        var noCodecOverlap = new HelloMessage
        {
            ProtocolVersion = TestHello.ProtocolVersion,
            Role = PeerRole.Full,
            Codecs = SupportedCodecs.None,
            MaxConcurrentStreams = 1
        };

        fake.RaiseMessageReceived(HelloMsg(noCodecOverlap));

        Assert.NotNull(rejectedReason);
        var sent = HelloRejectedMessage.Deserialize(fake.LastSentMessage!.Data);
        Assert.Equal(HelloRejectReason.IncompatibleCapabilities, sent.Reason);
        Assert.Equal(1, fake.DisconnectCallCount);
    }

    [Fact]
    public void BadHandshakeType_AfterHello_FiresBadHandshake_Disconnects_NoHandshakeVerified()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        bool badHandshake = false, handshakeVerified = false;
        session.BadHandshake += () => badHandshake = true;
        session.HandshakeVerified += () => handshakeVerified = true;

        fake.RaiseMessageReceived(HelloMsg(TestHello));
        fake.RaiseMessageReceived(new NetworkMessage { Type = MessageType.ChatMessage, Data = Array.Empty<byte>() });

        Assert.True(badHandshake);
        Assert.Equal(1, fake.DisconnectCallCount);
        Assert.False(handshakeVerified);
    }

    [Fact]
    public void WrongSecret_AfterHello_FiresHandshakeTokenInvalid_SendsHandshakeFailed_NoHandshakeVerified()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello, expectedSessionSecret: "correct");
        bool tokenInvalid = false, handshakeVerified = false;
        session.HandshakeTokenInvalid += () => tokenInvalid = true;
        session.HandshakeVerified += () => handshakeVerified = true;

        fake.RaiseMessageReceived(HelloMsg(TestHello));
        fake.RaiseMessageReceived(new NetworkMessage
        {
            Type = MessageType.Handshake,
            Data = Encoding.UTF8.GetBytes("wrong")
        });

        Assert.True(tokenInvalid);
        Assert.Equal(MessageType.HandshakeFailed, fake.LastSentMessage!.Type);
        Assert.Equal(1, fake.DisconnectCallCount);
        Assert.False(handshakeVerified);
    }

    [Fact]
    public void DispatchEvents_DoNotFire_BeforeHelloAndHandshakeComplete()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello);
        bool mouseMoveFired = false;
        session.MouseMoveReceived += _ => mouseMoveFired = true;

        // A MouseMove message arriving before Hello is complete is routed through
        // HandleHello, not the dispatch switch -- it's not a Hello, so it's rejected as bad,
        // never reaches MessageDispatch.ClassifyForHost.
        fake.RaiseMessageReceived(new NetworkMessage
        {
            Type = MessageType.MouseMove,
            Data = new MouseMoveMessage { X = 1, Y = 2 }.Serialize()
        });

        Assert.False(mouseMoveFired);
    }

    [Fact]
    public void PostHandshake_MouseMove_FiresMouseMoveReceived_AndPostHandshakeMessageReceived()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello, expectedSessionSecret: "s3cr3t");
        MouseMoveMessage? received = null;
        bool postHandshakeFired = false;
        session.MouseMoveReceived += m => received = m;
        session.PostHandshakeMessageReceived += () => postHandshakeFired = true;

        fake.RaiseMessageReceived(HelloMsg(TestHello));
        fake.RaiseMessageReceived(new NetworkMessage
        {
            Type = MessageType.Handshake,
            Data = Encoding.UTF8.GetBytes("s3cr3t")
        });
        fake.RaiseMessageReceived(new NetworkMessage
        {
            Type = MessageType.MouseMove,
            Data = new MouseMoveMessage { X = 10, Y = 20 }.Serialize()
        });

        Assert.True(postHandshakeFired);
        Assert.NotNull(received);
        Assert.Equal(10, received!.X);
        Assert.Equal(20, received.Y);
    }

    [Fact]
    public void PostHandshake_Ping_SendsPongDirectly_NoEventNeeded()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello, expectedSessionSecret: "s3cr3t");

        fake.RaiseMessageReceived(HelloMsg(TestHello));
        fake.RaiseMessageReceived(new NetworkMessage
        {
            Type = MessageType.Handshake,
            Data = Encoding.UTF8.GetBytes("s3cr3t")
        });
        var pingData = new byte[] { 9, 9 };
        fake.RaiseMessageReceived(new NetworkMessage { Type = MessageType.Ping, Data = pingData });

        Assert.Equal(MessageType.Pong, fake.LastSentMessage!.Type);
        Assert.Equal(pingData, fake.LastSentMessage.Data);
    }

    [Fact]
    public void Reconnect_ResetsHelloAndHandshakeState()
    {
        // Guards the Connected handler's reset of _helloComplete/_handshakeComplete: a second
        // connection on the same HostSession instance must go through Hello/Handshake again,
        // not skip straight to dispatch because a prior connection already completed it.
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake, TestHello, expectedSessionSecret: "s3cr3t");
        fake.RaiseMessageReceived(HelloMsg(TestHello));
        fake.RaiseMessageReceived(new NetworkMessage
        {
            Type = MessageType.Handshake,
            Data = Encoding.UTF8.GetBytes("s3cr3t")
        });

        fake.RaiseConnected();
        bool mouseMoveFired = false;
        session.MouseMoveReceived += _ => mouseMoveFired = true;
        fake.RaiseMessageReceived(new NetworkMessage
        {
            Type = MessageType.MouseMove,
            Data = new MouseMoveMessage { X = 1, Y = 2 }.Serialize()
        });

        Assert.False(mouseMoveFired);
    }
}
