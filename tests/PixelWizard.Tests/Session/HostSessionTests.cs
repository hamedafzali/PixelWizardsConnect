using System;
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
    [Fact]
    public void Connected_IsForwarded()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake);
        bool fired = false;
        session.Connected += () => fired = true;

        fake.RaiseConnected();

        Assert.True(fired);
    }

    [Fact]
    public void Disconnected_IsForwarded()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake);
        bool fired = false;
        session.Disconnected += () => fired = true;

        fake.RaiseDisconnected();

        Assert.True(fired);
    }

    [Fact]
    public void Error_IsForwardedWithSameException()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake);
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
        var session = new HostSession(fake);
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
        var session = new HostSession(fake);
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
        var session = new HostSession(fake);

        Assert.True(session.IsConnected);
    }

    [Fact]
    public async System.Threading.Tasks.Task StartServerAsync_DelegatesToTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake);

        await session.StartServerAsync(5555, useTls: false);

        Assert.Equal((5555, false), fake.LastStartServerArgs);
    }

    [Fact]
    public async System.Threading.Tasks.Task SendMessageAsync_DelegatesToTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake);
        var msg = new NetworkMessage { Type = MessageType.ChatMessage, Data = new byte[] { 1, 2, 3 } };

        await session.SendMessageAsync(msg);

        Assert.Same(msg, fake.LastSentMessage);
    }

    [Fact]
    public void Disconnect_DelegatesToTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake);

        session.Disconnect();

        Assert.Equal(1, fake.DisconnectCallCount);
    }

    [Fact]
    public void Dispose_DisposesTransport()
    {
        var fake = new FakeSessionTransport();
        var session = new HostSession(fake);

        session.Dispose();

        Assert.True(fake.Disposed);
    }

    [Fact]
    public void Constructor_NullTransport_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new HostSession(null!));
    }
}
