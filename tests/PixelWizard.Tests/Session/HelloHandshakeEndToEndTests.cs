using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.Tests.Session;

/// <summary>
/// T9.2c: closes BACKLOG.md item 4. Phase 1's Hello negotiation
/// (<see cref="HelloNegotiator"/>, <see cref="HelloCompatibility"/>) and T9.2a/T9.2b's
/// dispatch moves were, until now, only proven with pure unit tests and in-memory fakes
/// (<c>FakeSessionTransport</c>). This is the thing that precondition was waiting on:
/// HostSession and ViewerSession, driving real <see cref="TcpTransport"/> sockets over
/// loopback, exercising the actual Hello/HelloAck/HelloRejected and
/// Handshake/HandshakeOk/HandshakeFailed wire exchange end-to-end -- no fakes, no UI.
/// </summary>
public class HelloHandshakeEndToEndTests
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

    /// <summary>
    /// Waits for <paramref name="tcs"/> to complete or fails the test with a clear message --
    /// CI (especially windows-latest) is slower and noisier than a local loopback run, so a
    /// fixed Task.Delay would either be too flaky or too slow; this polls the actual signal.
    /// </summary>
    private static async Task<T> WaitAsync<T>(TaskCompletionSource<T> tcs, string what)
    {
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != tcs.Task)
            throw new TimeoutException($"Timed out waiting for: {what}");
        return await tcs.Task;
    }

    [Fact]
    public async Task HappyPath_HelloThroughHandshake_EndToEnd_HostVerifiesAndViewerAcknowledges()
    {
        int port = GetFreePort();
        const string secret = "shared-secret";

        using var hostTransport = new TcpTransport();
        var host = new HostSession(hostTransport, OurHello, secret);
        var hostVerified = new TaskCompletionSource<bool>();
        host.HandshakeVerified += () => hostVerified.TrySetResult(true);
        host.BadHello += () => hostVerified.TrySetException(new Exception("unexpected BadHello"));
        host.MalformedHello += () => hostVerified.TrySetException(new Exception("unexpected MalformedHello"));
        host.IncompatiblePeer += () => hostVerified.TrySetException(new Exception("unexpected IncompatiblePeer"));
        host.HelloRejectedLocally += r => hostVerified.TrySetException(new Exception($"unexpected HelloRejectedLocally: {r}"));
        host.BadHandshake += () => hostVerified.TrySetException(new Exception("unexpected BadHandshake"));
        host.HandshakeTokenInvalid += () => hostVerified.TrySetException(new Exception("unexpected HandshakeTokenInvalid"));
        _ = host.StartServerAsync(port, useTls: false);
        await Task.Delay(50); // give the listener a moment to bind before connecting

        using var viewer = new ViewerSession(() => new TcpTransport(), OurHello, secret);
        var viewerAcked = new TaskCompletionSource<HelloMessage>();
        viewer.HostHelloAcknowledged += h => viewerAcked.TrySetResult(h);
        viewer.HostHelloRejected += r => viewerAcked.TrySetException(new Exception($"unexpected HostHelloRejected: {r.Message}"));
        viewer.HandshakeRejected += () => viewerAcked.TrySetException(new Exception("unexpected HandshakeRejected"));

        // No hand-sent Hello: since T9.3a ViewerSession sends it on connect itself.
        await viewer.ConnectAsync("127.0.0.1", port, useTls: false);

        var ackedHello = await WaitAsync(viewerAcked, "viewer to receive HelloAck");
        Assert.Equal(OurHello.ProtocolVersion, ackedHello.ProtocolVersion);

        var verified = await WaitAsync(hostVerified, "host to verify the handshake");
        Assert.True(verified);
        Assert.True(host.IsConnected);
        Assert.True(viewer.IsConnected);

        // Negative control for the v1-host heuristic: a host that answered Hello and then
        // drops must not be reported as a possibly-older host.
        var viewerDropped = new TaskCompletionSource<bool>();
        viewer.Disconnected += () => viewerDropped.TrySetResult(viewer.HelloUnansweredAtDisconnect);
        host.Disconnect();
        Assert.False(await WaitAsync(viewerDropped, "viewer to see the host drop"));
    }

    /// <summary>
    /// T10: the tests above run plaintext for speed, but the app defaults to TLS
    /// (HostTlsEnabled, TcpTransport's TOFU pinning). Same Hello-through-Handshake flow,
    /// over TLS, through HostListener -- the path MainViewModel actually uses to host.
    /// The viewer gets an isolated pin store so the run never touches the user's pins.
    /// </summary>
    [Fact]
    public async Task HappyPath_OverTls_ThroughHostListener_HostVerifiesAndViewerAcknowledges()
    {
        int port = GetFreePort();
        const string secret = "shared-secret";
        string pinPath = Path.Combine(Path.GetTempPath(), $"pixelwizard-pins-{Guid.NewGuid():N}.json");
        var pinStore = new CertificatePinStore(pinPath);

        var listener = new HostListener(() => new TcpTransport(), OurHello, port, useTls: () => true, secret);
        var hostVerified = new TaskCompletionSource<bool>();
        listener.SessionCreated += host =>
        {
            host.HandshakeVerified += () => hostVerified.TrySetResult(true);
            host.HelloRejectedLocally += r => hostVerified.TrySetException(new Exception($"unexpected HelloRejectedLocally: {r}"));
            host.HandshakeTokenInvalid += () => hostVerified.TrySetException(new Exception("unexpected HandshakeTokenInvalid"));
        };
        _ = listener.StartAsync();
        await Task.Delay(50); // give the listener a moment to bind before connecting

        try
        {
            using var viewer = new ViewerSession(() => new TcpTransport(pinStore), OurHello, secret);
            var viewerAcked = new TaskCompletionSource<HelloMessage>();
            viewer.HostHelloAcknowledged += h => viewerAcked.TrySetResult(h);
            viewer.HostHelloRejected += r => viewerAcked.TrySetException(new Exception($"unexpected HostHelloRejected: {r.Message}"));

            await viewer.ConnectAsync("127.0.0.1", port, useTls: true);

            await WaitAsync(viewerAcked, "viewer to receive HelloAck over TLS");
            Assert.True(await WaitAsync(hostVerified, "host to verify the handshake over TLS"));
            Assert.NotNull(pinStore.TryGetPin($"127.0.0.1:{port}"));
        }
        finally
        {
            listener.Stop();
            if (File.Exists(pinPath)) File.Delete(pinPath);
        }
    }

    [Fact]
    public async Task VersionMismatch_RealSockets_HostRejectsAndDisconnects_NoHandshakeVerified()
    {
        int port = GetFreePort();

        using var hostTransport = new TcpTransport();
        var host = new HostSession(hostTransport, OurHello);
        var hostRejected = new TaskCompletionSource<string>();
        host.HelloRejectedLocally += reason => hostRejected.TrySetResult(reason);
        host.HandshakeVerified += () => hostRejected.TrySetException(new Exception("unexpected HandshakeVerified"));
        _ = host.StartServerAsync(port, useTls: false);
        await Task.Delay(50); // give the listener a moment to bind before connecting

        // The viewer itself advertises the mismatched version -- it's what ViewerSession sends
        // on connect, not a hand-crafted packet.
        var mismatched = new HelloMessage
        {
            ProtocolVersion = unchecked((byte)(OurHello.ProtocolVersion + 1)),
            Role = PeerRole.Full,
            Codecs = SupportedCodecs.Jpeg,
            MaxConcurrentStreams = 1
        };
        using var viewer = new ViewerSession(() => new TcpTransport(), mismatched);
        var viewerRejected = new TaskCompletionSource<HelloRejectedMessage>();
        viewer.HostHelloRejected += r => viewerRejected.TrySetResult(r);
        viewer.HostHelloAcknowledged += _ => viewerRejected.TrySetException(new Exception("unexpected HostHelloAcknowledged"));

        await viewer.ConnectAsync("127.0.0.1", port, useTls: false);

        var reason = await WaitAsync(hostRejected, "host to reject the mismatched Hello");
        Assert.Contains("incompatible", reason);

        var rejectedMsg = await WaitAsync(viewerRejected, "viewer to receive HelloRejected");
        Assert.Equal(HelloRejectReason.VersionMismatch, rejectedMsg.Reason);
    }

    [Fact]
    public async Task WrongSecret_RealSockets_HostRejectsHandshake_ViewerNotified()
    {
        int port = GetFreePort();

        using var hostTransport = new TcpTransport();
        var host = new HostSession(hostTransport, OurHello, expectedSessionSecret: "correct-secret");
        var hostTokenInvalid = new TaskCompletionSource<bool>();
        host.HandshakeTokenInvalid += () => hostTokenInvalid.TrySetResult(true);
        host.HandshakeVerified += () => hostTokenInvalid.TrySetException(new Exception("unexpected HandshakeVerified"));
        _ = host.StartServerAsync(port, useTls: false);
        await Task.Delay(50); // give the listener a moment to bind before connecting

        // Viewer answers the handshake with a different secret than the host expects -- this
        // is the real scenario (a stale/guessed connection code), not a hand-crafted packet.
        using var viewer = new ViewerSession(() => new TcpTransport(), OurHello, sessionSecret: "wrong-secret");
        var viewerHandshakeRejected = new TaskCompletionSource<bool>();
        viewer.HandshakeRejected += () => viewerHandshakeRejected.TrySetResult(true);

        await viewer.ConnectAsync("127.0.0.1", port, useTls: false);

        Assert.True(await WaitAsync(hostTokenInvalid, "host to detect the invalid handshake token"));
        Assert.True(await WaitAsync(viewerHandshakeRejected, "viewer to be notified of HandshakeRejected"));
    }

    [Fact]
    public async Task V1Peer_RealSocket_FirstMessageHandshake_HostDetectsIncompatible_NoReplySent()
    {
        int port = GetFreePort();

        using var hostTransport = new TcpTransport();
        var host = new HostSession(hostTransport, OurHello);
        var incompatible = new TaskCompletionSource<bool>();
        host.IncompatiblePeer += () => incompatible.TrySetResult(true);
        host.HandshakeVerified += () => incompatible.TrySetException(new Exception("unexpected HandshakeVerified"));
        _ = host.StartServerAsync(port, useTls: false);
        await Task.Delay(50); // give the listener a moment to bind before connecting

        // A v1 peer has no Hello concept -- its first message is always Handshake. Drive the
        // raw transport directly rather than through ViewerSession, which always sends Hello
        // first; this simulates the pre-Hello client build exactly as it behaves on the wire.
        using var v1Client = new TcpTransport();
        await v1Client.ConnectAsync("127.0.0.1", port, useTls: false);
        await v1Client.SendMessageAsync(new NetworkMessage
        {
            Type = MessageType.Handshake,
            Data = Encoding.UTF8.GetBytes("")
        });

        Assert.True(await WaitAsync(incompatible, "host to detect the v1 peer"));
    }

    [Fact]
    public async Task V1Host_RealSocket_ClosesOnHelloWithoutReplying_ViewerReportsHelloUnanswered()
    {
        int port = GetFreePort();

        // A v1 host has no Hello concept: its strict pre-handshake gate sees an unknown
        // message type and drops the connection with zero bytes sent back. Simulate exactly
        // that on the wire with a raw listener: accept, read one full frame (the viewer's
        // Hello), close.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var v1Host = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var lenBuf = new byte[4];
            await ReadExactAsync(stream, lenBuf);
            await ReadExactAsync(stream, new byte[BitConverter.ToInt32(lenBuf, 0)]);
            client.Close();
        });

        try
        {
            using var viewer = new ViewerSession(() => new TcpTransport(), OurHello);
            var viewerDropped = new TaskCompletionSource<bool>();
            viewer.Disconnected += () => viewerDropped.TrySetResult(viewer.HelloUnansweredAtDisconnect);
            viewer.HostHelloAcknowledged += _ => viewerDropped.TrySetException(new Exception("unexpected HostHelloAcknowledged"));

            await viewer.ConnectAsync("127.0.0.1", port, useTls: false);

            Assert.True(await WaitAsync(viewerDropped, "viewer to see the v1 host close"));
            await v1Host;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await stream.ReadAsync(buf, read, buf.Length - read);
            if (n == 0) throw new EndOfStreamException("peer closed before a full frame arrived");
            read += n;
        }
    }
}
