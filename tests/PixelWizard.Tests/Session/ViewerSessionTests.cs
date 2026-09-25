using System;
using PixelWizard.Protocol;
using PixelWizard.Session;
using Xunit;

namespace PixelWizard.Tests.Session;

/// <summary>
/// T9.1's behavioral gate for ViewerSession -- see HostSessionTests for the rationale.
/// Dispatch classification (OnViewerMessage) isn't here yet -- that's T9.2a.
/// </summary>
public class ViewerSessionTests
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
        var session = new ViewerSession(() => fake, TestHello);
        bool fired = false;
        session.Connected += () => fired = true;

        fake.RaiseConnected();

        Assert.True(fired);
    }

    [Fact]
    public void Disconnected_IsForwarded()
    {
        var fake = new FakeSessionTransport();
        var session = new ViewerSession(() => fake, TestHello);
        bool fired = false;
        session.Disconnected += () => fired = true;

        fake.RaiseDisconnected();

        Assert.True(fired);
    }

    [Fact]
    public void Error_IsForwardedWithSameException()
    {
        var fake = new FakeSessionTransport();
        var session = new ViewerSession(() => fake, TestHello);
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
        var session = new ViewerSession(() => fake, TestHello);
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
        var session = new ViewerSession(() => fake, TestHello);
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
        var session = new ViewerSession(() => fake, TestHello);

        Assert.True(session.IsConnected);
    }

    [Fact]
    public async System.Threading.Tasks.Task ConnectAsync_DelegatesToTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new ViewerSession(() => fake, TestHello);

        await session.ConnectAsync("10.0.0.5", 5555, useTls: false);

        Assert.Equal(("10.0.0.5", 5555, false), fake.LastConnectArgs);
    }

    [Fact]
    public async System.Threading.Tasks.Task SendMessageAsync_DelegatesToTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new ViewerSession(() => fake, TestHello);
        var msg = new NetworkMessage { Type = MessageType.ChatMessage, Data = new byte[] { 1, 2, 3 } };

        await session.SendMessageAsync(msg);

        Assert.Same(msg, fake.LastSentMessage);
    }

    [Fact]
    public void Disconnect_DelegatesToTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new ViewerSession(() => fake, TestHello);

        session.Disconnect();

        Assert.Equal(1, fake.DisconnectCallCount);
    }

    [Fact]
    public void Dispose_DisposesTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new ViewerSession(() => fake, TestHello);

        session.Dispose();

        Assert.True(fake.Disposed);
    }

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewerSession(null!, TestHello));
    }

    [Fact]
    public void Constructor_FactoryReturnsNull_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new ViewerSession(() => null!, TestHello));
    }

    [Fact]
    public void Constructor_NullHello_Throws()
    {
        var fake = new FakeSessionTransport();
        Assert.Throws<ArgumentNullException>(() => new ViewerSession(() => fake, null!));
    }

    [Fact]
    public void Constructor_InvokesFactoryExactlyOnce()
    {
        int calls = 0;
        var fake = new FakeSessionTransport();
        _ = new ViewerSession(() => { calls++; return fake; }, TestHello);

        Assert.Equal(1, calls);
    }

    // ── T9.3a: Hello-on-connect and awaiting-Hello tracking ──────────────────

    [Fact]
    public void Connected_SendsOurHello()
    {
        var fake = new FakeSessionTransport();
        _ = new ViewerSession(() => fake, TestHello);

        fake.RaiseConnected();

        Assert.Equal(MessageType.Hello, fake.LastSentMessage!.Type);
        Assert.Equal(TestHello.Serialize(), fake.LastSentMessage.Data);
    }

    [Fact]
    public void NoHelloSent_BeforeConnected()
    {
        var fake = new FakeSessionTransport();
        _ = new ViewerSession(() => fake, TestHello);

        Assert.Null(fake.LastSentMessage);
    }

    [Fact]
    public void DisconnectAfterHello_WithNoReply_ReportsHelloUnanswered()
    {
        var fake = new FakeSessionTransport();
        var session = new ViewerSession(() => fake, TestHello);
        bool? seen = null;
        session.Disconnected += () => seen = session.HelloUnansweredAtDisconnect;

        fake.RaiseConnected();
        fake.RaiseDisconnected();

        Assert.True(seen);
    }

    [Fact]
    public void AnyReply_ClearsHelloUnanswered_EvenARejection()
    {
        var fake = new FakeSessionTransport();
        var session = new ViewerSession(() => fake, TestHello);
        bool? seen = null;
        session.Disconnected += () => seen = session.HelloUnansweredAtDisconnect;

        fake.RaiseConnected();
        fake.RaiseMessageReceived(new NetworkMessage
        {
            Type = MessageType.HelloRejected,
            Data = new HelloRejectedMessage { Reason = HelloRejectReason.VersionMismatch, Message = "no" }.Serialize()
        });
        fake.RaiseDisconnected();

        Assert.False(seen);
    }

    [Fact]
    public void DisconnectWithoutEverConnecting_DoesNotReportHelloUnanswered()
    {
        // TcpTransport.ConnectAsync's failure path calls Disconnect() without ever raising
        // Connected -- that's a plain connect failure, not an older host.
        var fake = new FakeSessionTransport();
        var session = new ViewerSession(() => fake, TestHello);
        bool? seen = null;
        session.Disconnected += () => seen = session.HelloUnansweredAtDisconnect;

        fake.RaiseDisconnected();

        Assert.False(seen);
    }

    [Fact]
    public void RepeatedDisconnectReports_OnlyFirstReportsHelloUnanswered()
    {
        // TcpTransport.Disconnect() raises Disconnected on every call (user disconnect plus
        // the receive loop's own exit can both report) -- the flag is cleared by the first.
        var fake = new FakeSessionTransport();
        var session = new ViewerSession(() => fake, TestHello);
        var seen = new System.Collections.Generic.List<bool>();
        session.Disconnected += () => seen.Add(session.HelloUnansweredAtDisconnect);

        fake.RaiseConnected();
        fake.RaiseDisconnected();
        fake.RaiseDisconnected();

        Assert.Equal(new[] { true, false }, seen);
    }

    [Fact]
    public void Reconnect_RearmsHelloTracking_AndResendsHello()
    {
        var fake = new FakeSessionTransport();
        var session = new ViewerSession(() => fake, TestHello);
        var seen = new System.Collections.Generic.List<bool>();
        session.Disconnected += () => seen.Add(session.HelloUnansweredAtDisconnect);

        fake.RaiseConnected();
        fake.RaiseMessageReceived(new NetworkMessage { Type = MessageType.HandshakeOk, Data = Array.Empty<byte>() });
        fake.RaiseDisconnected();
        fake.RaiseConnected();
        Assert.Equal(MessageType.Hello, fake.LastSentMessage!.Type);
        fake.RaiseDisconnected();

        Assert.Equal(new[] { false, true }, seen);
    }
}
