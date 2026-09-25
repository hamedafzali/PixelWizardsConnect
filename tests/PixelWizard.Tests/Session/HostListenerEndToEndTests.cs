using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.Tests.Session;

/// <summary>
/// T9.3b's gate: HostListener over real <see cref="TcpTransport"/> sockets. The relisten
/// trigger here stands in for MainViewModel's (relisten on the served session's first
/// Disconnected) -- the policy stays in MainViewModel, the mechanics are what's under test.
/// </summary>
public class HostListenerEndToEndTests
{
    private static readonly HelloMessage OurHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<T> WaitAsync<T>(TaskCompletionSource<T> tcs, string what)
    {
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != tcs.Task)
            throw new TimeoutException($"Timed out waiting for: {what}");
        return await tcs.Task;
    }

    [Fact]
    public async Task TwoSequentialViewers_SameListener_EachGetsAFreshSessionAndReachesTheConsentGate()
    {
        int port = GetFreePort();
        const string secret = "s3cret";
        var listener = new HostListener(() => new TcpTransport(), OurHello, port, () => false, secret);

        var sessions = new ConcurrentQueue<HostSession>();
        var verifiedCounts = new ConcurrentDictionary<HostSession, int>();
        var firstVerified = new TaskCompletionSource<HostSession>();
        var secondVerified = new TaskCompletionSource<HostSession>();
        var secondCreated = new TaskCompletionSource<HostSession>();
        int relistenTriggered = 0;

        listener.SessionCreated += s =>
        {
            sessions.Enqueue(s);
            if (sessions.Count == 2) secondCreated.TrySetResult(s);
            s.HandshakeVerified += () =>
            {
                int n = verifiedCounts.AddOrUpdate(s, 1, (_, c) => c + 1);
                if (n == 1)
                {
                    if (!firstVerified.TrySetResult(s))
                        secondVerified.TrySetResult(s);
                }
            };
            // Stand-in for MainViewModel's policy: relisten once, after the first viewer
            // leaves. Off the receive-loop thread, as MainViewModel's Post is.
            s.Disconnected += () =>
            {
                if (sessions.Count == 1 && Interlocked.Exchange(ref relistenTriggered, 1) == 0)
                    _ = Task.Run(() => listener.RelistenAsync());
            };
        };

        _ = listener.StartAsync();
        await Task.Delay(50); // give the listener a moment to bind before connecting

        try
        {
            using (var viewer1 = new ViewerSession(() => new TcpTransport(), OurHello, secret))
            {
                await viewer1.ConnectAsync("127.0.0.1", port, useTls: false);
                var s1 = await WaitAsync(firstVerified, "first viewer to reach HandshakeVerified");
                Assert.Same(s1, listener.Current);
                viewer1.Disconnect();
            }

            var s2Created = await WaitAsync(secondCreated, "listener to create a second session");
            await Task.Delay(50); // bind race, as above

            using var viewer2 = new ViewerSession(() => new TcpTransport(), OurHello, secret);
            var viewer2Acked = new TaskCompletionSource<HelloMessage>();
            viewer2.HostHelloAcknowledged += h => viewer2Acked.TrySetResult(h);
            await viewer2.ConnectAsync("127.0.0.1", port, useTls: false);

            await WaitAsync(viewer2Acked, "second viewer to receive HelloAck");
            var s2 = await WaitAsync(secondVerified, "second viewer to reach HandshakeVerified");

            var first = firstVerified.Task.Result;
            Assert.NotSame(first, s2);
            Assert.Same(s2Created, s2);
            Assert.Same(s2, listener.Current);
            Assert.Equal(1, verifiedCounts[first]);
            Assert.Equal(1, verifiedCounts[s2]);
            Assert.Equal(2, sessions.Count);
            Assert.False(first.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Stop_WhileViewerConnected_DisconnectsIt_AndNoFurtherListenHappens()
    {
        int port = GetFreePort();
        var listener = new HostListener(() => new TcpTransport(), OurHello, port, () => false);
        int created = 0;
        var verified = new TaskCompletionSource<bool>();
        listener.SessionCreated += s =>
        {
            Interlocked.Increment(ref created);
            s.HandshakeVerified += () => verified.TrySetResult(true);
        };
        _ = listener.StartAsync();
        await Task.Delay(50);

        using var viewer = new ViewerSession(() => new TcpTransport(), OurHello);
        var viewerDropped = new TaskCompletionSource<bool>();
        viewer.Disconnected += () => viewerDropped.TrySetResult(true);
        await viewer.ConnectAsync("127.0.0.1", port, useTls: false);
        await WaitAsync(verified, "viewer to reach HandshakeVerified");

        listener.Stop();

        Assert.True(await WaitAsync(viewerDropped, "viewer to see the host stop"));
        Assert.Null(listener.Current);
        Assert.False(await listener.RelistenAsync());
        Assert.Equal(1, Volatile.Read(ref created));

        // Nothing is listening on the port any more: TcpTransport stops its TcpListener once
        // it accepts, and Stop left no relisten behind to open a new one.
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port));
    }
}
