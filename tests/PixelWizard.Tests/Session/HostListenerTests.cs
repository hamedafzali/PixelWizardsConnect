using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PixelWizard.Protocol;
using PixelWizard.Session;
using Xunit;

namespace PixelWizard.Tests.Session;

/// <summary>
/// T9.3b: HostListener's listen/relisten mechanics against in-memory transports. The
/// real-socket gate (two sequential viewers, stop halts relisten) is in
/// HostListenerEndToEndTests.
/// </summary>
public class HostListenerTests
{
    private static readonly HelloMessage TestHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };

    // Hands out a fresh fake per factory call and remembers each one, in order.
    private sealed class FakeFactory
    {
        public readonly List<FakeSessionTransport> Created = new();
        public Func<FakeSessionTransport> Make = () => new FakeSessionTransport();
        public FakeSessionTransport Next()
        {
            var t = Make();
            Created.Add(t);
            return t;
        }
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new HostListener(null!, TestHello, 1, () => false));
        Assert.Throws<ArgumentNullException>(() => new HostListener(() => new FakeSessionTransport(), null!, 1, () => false));
        Assert.Throws<ArgumentNullException>(() => new HostListener(() => new FakeSessionTransport(), TestHello, 1, null!));
    }

    [Fact]
    public void Constructor_DoesNotCreateATransport()
    {
        var factory = new FakeFactory();
        var listener = new HostListener(factory.Next, TestHello, 1, () => false);

        Assert.Empty(factory.Created);
        Assert.Null(listener.Current);
    }

    [Fact]
    public async Task StartAsync_CreatesOneSession_RaisesSessionCreatedBeforeListening()
    {
        var factory = new FakeFactory();
        var listener = new HostListener(factory.Next, TestHello, 5555, () => true);
        HostSession? created = null;
        (int, bool)? argsWhenCreated = (0, false);
        listener.SessionCreated += s =>
        {
            created = s;
            argsWhenCreated = factory.Created[0].LastStartServerArgs;
        };

        await listener.StartAsync();

        Assert.Single(factory.Created);
        Assert.Same(listener.Current, created);
        Assert.Null(argsWhenCreated); // wiring happens before the session starts listening
        Assert.Equal((5555, true), factory.Created[0].LastStartServerArgs);
    }

    [Fact]
    public async Task RelistenAsync_DisposesOldSession_ListensWithAFreshOne()
    {
        var factory = new FakeFactory();
        var listener = new HostListener(factory.Next, TestHello, 5555, () => false);
        var created = new List<HostSession>();
        listener.SessionCreated += created.Add;
        await listener.StartAsync();

        Assert.True(await listener.RelistenAsync());

        Assert.Equal(2, factory.Created.Count);
        Assert.True(factory.Created[0].Disposed);
        Assert.False(factory.Created[1].Disposed);
        Assert.Equal(2, created.Count);
        Assert.NotSame(created[0], created[1]);
        Assert.Same(created[1], listener.Current);
        Assert.Equal((5555, false), factory.Created[1].LastStartServerArgs);
        Assert.False(listener.IsRelistening);
    }

    [Fact]
    public async Task RelistenAsync_WhileOneIsInFlight_ReturnsFalseAndDoesNothing()
    {
        var factory = new FakeFactory();
        var listener = new HostListener(factory.Next, TestHello, 5555, () => false);
        await listener.StartAsync();

        var gate = new TaskCompletionSource<bool>();
        factory.Make = () => new FakeSessionTransport { StartServerGate = gate };
        var first = listener.RelistenAsync();
        Assert.True(listener.IsRelistening);

        Assert.False(await listener.RelistenAsync());
        Assert.Equal(2, factory.Created.Count);

        gate.SetResult(true);
        Assert.True(await first);
        Assert.False(listener.IsRelistening);
    }

    [Fact]
    public async Task UseTls_IsReadOnEveryListen()
    {
        bool tls = false;
        var factory = new FakeFactory();
        var listener = new HostListener(factory.Next, TestHello, 5555, () => tls);
        await listener.StartAsync();
        tls = true;

        await listener.RelistenAsync();

        Assert.Equal((5555, false), factory.Created[0].LastStartServerArgs);
        Assert.Equal((5555, true), factory.Created[1].LastStartServerArgs);
    }

    [Fact]
    public async Task Stop_DisconnectsCurrent_AndMakesRelistenANoOp()
    {
        var factory = new FakeFactory();
        var listener = new HostListener(factory.Next, TestHello, 5555, () => false);
        bool createdAfterStop = false;
        await listener.StartAsync();

        listener.Stop();
        listener.SessionCreated += _ => createdAfterStop = true;

        Assert.Equal(1, factory.Created[0].DisconnectCallCount);
        Assert.Null(listener.Current);
        Assert.False(await listener.RelistenAsync());
        Assert.False(createdAfterStop);
        Assert.Single(factory.Created);
    }

    [Fact]
    public async Task RelistenAsync_FactoryThrows_PropagatesAndClearsGuard()
    {
        var factory = new FakeFactory();
        var listener = new HostListener(factory.Next, TestHello, 5555, () => false);
        await listener.StartAsync();
        factory.Make = () => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => listener.RelistenAsync());

        Assert.False(listener.IsRelistening);
        Assert.True(factory.Created[0].Disposed);
    }
}
