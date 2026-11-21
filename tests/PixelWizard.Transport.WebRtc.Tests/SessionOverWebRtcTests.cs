using System.Collections.Concurrent;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Signaling;
using Xunit;

namespace PixelWizard.Transport.WebRtc.Tests;

/// <summary>
/// HostSession and ViewerSession over the real WebRtcTransport (paired through the real router), with the
/// Handshake run with an empty secret, the same trust model as direct LAN (PHASE4-PLAN D3). Proves the D4
/// consent rule over the real transport: before Approve() nothing on the media plane leaves the host and
/// inbound input, clipboard and chat are dropped; deny, a throwing decider and a late approval all end
/// with nothing sent.
/// </summary>
[Collection("router")]
public class SessionOverWebRtcTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly HelloMessage OurHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };
    private static readonly ScreenDelta Frame = new() { X = 0, Y = 0, Width = 1, Height = 1, ImageData = new byte[] { 1, 2, 3 } };

    private readonly RouterFixture _router;
    public SessionOverWebRtcTests(RouterFixture router) => _router = router;

    private sealed class Pair : IDisposable
    {
        public WebRtcTransport HostT = new() { EstablishTimeout = TimeSpan.FromSeconds(30) };
        public WebRtcTransport ViewerT = new() { EstablishTimeout = TimeSpan.FromSeconds(30) };
        public HostSession Host = null!;
        public ViewerSession Viewer = null!;

        // Counted by handlers subscribed AFTER the sessions' own, so by the time a count is reached the
        // session has already classified (and, if it were going to, raised events for) that message.
        public int HostTransportMessages, ViewerTransportMessages;
        public int HostEvents; // input, clipboard, chat raised by the host session
        public int ViewerEvents; // frames, clipboard, chat raised by the viewer session
        public readonly TaskCompletionSource HandshakeVerified = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource HandshakeOkSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource HostDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ViewerDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ApprovalPendingAtViewerDisconnect;
        public int ViewerDisconnects;
        public readonly ConcurrentQueue<string> Got = new();

        public void Dispose() { HostT.Dispose(); ViewerT.Dispose(); }
    }

    private async Task<Pair> ConnectVerifiedAsync()
    {
        var p = new Pair();
        // Sessions first: the viewer session sends Hello from the transport's Connected event.
        p.Host = new HostSession(p.HostT, OurHello, "");
        p.Viewer = new ViewerSession(() => p.ViewerT, OurHello, "");
        p.Host.HandshakeVerified += () => p.HandshakeVerified.TrySetResult();
        p.Host.Disconnected += () => p.HostDisconnected.TrySetResult();
        p.Viewer.Disconnected += () =>
        {
            Interlocked.Increment(ref p.ViewerDisconnects);
            p.ApprovalPendingAtViewerDisconnect = p.Viewer.ApprovalPendingAtDisconnect;
            p.ViewerDisconnected.TrySetResult();
        };
        p.Host.MouseMoveReceived += _ => { Interlocked.Increment(ref p.HostEvents); p.Got.Enqueue("mouse"); };
        p.Host.MouseClickReceived += _ => Interlocked.Increment(ref p.HostEvents);
        p.Host.KeyPressReceived += _ => Interlocked.Increment(ref p.HostEvents);
        p.Host.KeyReleaseReceived += _ => Interlocked.Increment(ref p.HostEvents);
        p.Host.ClipboardReceived += _ => Interlocked.Increment(ref p.HostEvents);
        p.Host.ChatReceived += _ => { Interlocked.Increment(ref p.HostEvents); p.Got.Enqueue("host-chat"); };
        p.Viewer.FullScreenReceived += _ => { Interlocked.Increment(ref p.ViewerEvents); p.Got.Enqueue("full"); };
        p.Viewer.ScreenDeltaReceived += _ => Interlocked.Increment(ref p.ViewerEvents);
        p.Viewer.ClipboardReceived += _ => Interlocked.Increment(ref p.ViewerEvents);
        p.Viewer.ChatReceived += _ => { Interlocked.Increment(ref p.ViewerEvents); p.Got.Enqueue("viewer-chat"); };
        p.HostT.MessageReceived += _ => Interlocked.Increment(ref p.HostTransportMessages);
        p.ViewerT.MessageReceived += m =>
        {
            Interlocked.Increment(ref p.ViewerTransportMessages);
            if (m.Type == MessageType.HandshakeOk) p.HandshakeOkSeen.TrySetResult();
        };

        using var cts = new CancellationTokenSource(Limit);
        var ct = cts.Token;
        var hostSig = new SignalingClient("http://127.0.0.1", _router.Port);
        var viewerSig = new SignalingClient("http://127.0.0.1", _router.Port);
        var reg = await hostSig.RegisterAsync(ct);
        var waitForPeer = hostSig.WaitForPeerAsync(ct);
        var joined = await viewerSig.JoinAsync(reg.Code, ct);
        var peerJoined = await waitForPeer;
        await Task.WhenAll(
            p.HostT.EstablishAsync(hostSig, peerJoined.Turn, WebRtcRole.Host, ct: ct),
            p.ViewerT.EstablishAsync(viewerSig, joined.Turn, WebRtcRole.Viewer, ct: ct)).WaitAsync(Limit);

        // Hello, HelloAck, Handshake (empty secret), HandshakeOk all ran by themselves.
        await p.HandshakeVerified.Task.WaitAsync(Limit);
        await p.HandshakeOkSeen.Task.WaitAsync(Limit);
        Assert.False(p.Host.IsApproved);
        return p;
    }

    private static async Task WaitUntil(Func<bool> cond, string what)
    {
        var until = DateTime.UtcNow + Limit;
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [RouterFact]
    public async Task Before_approval_the_host_sends_no_media_and_nothing_reaches_the_viewer()
    {
        using var p = await ConnectVerifiedAsync();
        int viewerSeen = p.ViewerTransportMessages; // HelloAck + HandshakeOk

        await p.Host.SendFrameAsync(Frame, full: true);
        await p.Host.SendFrameAsync(Frame, full: false);
        await p.Host.SendClipboardAsync("secret");
        await p.Host.SendChatAsync("hi");
        // Fence on the control channel: a raw Pong (8 bytes) is delivered in order after anything the host
        // session might have wrongly sent on it, and the viewer session turns it into LatencyMeasured.
        var fence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Viewer.LatencyMeasured += _ => fence.TrySetResult();
        await p.HostT.SendMessageAsync(new NetworkMessage { Type = MessageType.Pong, Data = BitConverter.GetBytes(DateTime.UtcNow.Ticks) });
        await fence.Task.WaitAsync(Limit);

        Assert.Equal(viewerSeen + 1, p.ViewerTransportMessages); // only the fence
        Assert.Equal(0, p.ViewerEvents);
        Assert.Equal(0, p.HostT.MediaBytesSent);
        Assert.Equal(0, p.ViewerT.MediaBytesReceived);
        Assert.True(p.Host.IsConnected && p.Viewer.IsConnected);
    }

    [RouterFact]
    public async Task Before_approval_viewer_input_clipboard_and_chat_are_dropped()
    {
        using var p = await ConnectVerifiedAsync();
        int hostSeen = p.HostTransportMessages; // Hello + Handshake

        await p.Viewer.SendMouseMoveAsync(5, 6);
        await p.Viewer.SendMouseClickAsync(5, 6, true);
        await p.Viewer.SendMouseDownAsync(5, 6, true);
        await p.Viewer.SendMouseUpAsync(5, 6, true);
        await p.Viewer.SendMouseWheelAsync(5, 6, 0, 1);
        await p.Viewer.SendKeyAsync(65, true);
        await p.Viewer.SendKeyAsync(65, false);
        await p.Viewer.SendClipboardAsync("x");
        await p.Viewer.SendChatAsync("x");
        await p.Viewer.SendPingAsync();
        await WaitUntil(() => p.HostTransportMessages >= hostSeen + 10, "host to receive all 10 messages");
        await Task.Delay(200);

        Assert.Equal(0, p.HostEvents);
        Assert.Equal(0, p.ViewerEvents);
        Assert.Equal(0, p.HostT.MediaBytesSent);
        Assert.Equal(0, p.ViewerT.MediaBytesReceived);
        Assert.Equal(hostSeen + 10, p.HostTransportMessages);
        Assert.Equal(0, p.ViewerEvents);
    }

    [RouterFact]
    public async Task After_approval_frames_chat_and_input_flow()
    {
        using var p = await ConnectVerifiedAsync();
        p.Host.Approve();
        Assert.True(p.Host.IsApproved);

        await p.Host.SendFrameAsync(Frame, full: true);
        await p.Host.SendChatAsync("hello viewer");
        await WaitUntil(() => p.Got.Contains("full") && p.Got.Contains("viewer-chat"), "frame and chat on the viewer");
        Assert.True(p.HostT.MediaBytesSent > 0);
        Assert.True(p.ViewerT.MediaBytesReceived > 0);

        await p.Viewer.SendMouseMoveAsync(1, 2);
        await p.Viewer.SendChatAsync("hello host");
        await WaitUntil(() => p.Got.Contains("mouse") && p.Got.Contains("host-chat"), "input and chat on the host");
    }

    private async Task AssertDeniedAsync(Pair p, Task<bool> resolve)
    {
        Assert.False(await resolve.WaitAsync(Limit));
        await p.ViewerDisconnected.Task.WaitAsync(Limit);
        await p.HostDisconnected.Task.WaitAsync(Limit);
        await Task.Delay(300); // a second raise would show up by now
        Assert.True(p.ApprovalPendingAtViewerDisconnect);
        Assert.Equal(1, p.ViewerDisconnects);
        Assert.False(p.Host.IsApproved);
        Assert.False(p.HostT.IsConnected);
        Assert.False(p.ViewerT.IsConnected);
        Assert.Equal(0, p.HostT.MediaBytesSent);
        Assert.Equal(0, p.ViewerT.MediaBytesReceived);
        Assert.Equal(0, p.ViewerEvents);
        await p.Host.SendFrameAsync(Frame, full: true); // silently ignored
        Assert.Equal(0, p.HostT.MediaBytesSent);
    }

    [RouterFact]
    public async Task Deny_tears_down_the_peer_connection_and_sends_no_media()
    {
        using var p = await ConnectVerifiedAsync();
        await AssertDeniedAsync(p, p.Host.ResolveConsentAsync(_ => Task.FromResult(false), "viewer"));
    }

    [RouterFact]
    public async Task A_decider_that_throws_is_a_denial()
    {
        using var p = await ConnectVerifiedAsync();
        await AssertDeniedAsync(p, p.Host.ResolveConsentAsync(_ => throw new InvalidOperationException("dialog crashed"), "viewer"));
    }

    [RouterFact]
    public async Task A_late_approval_after_the_viewer_left_changes_nothing()
    {
        using var p = await ConnectVerifiedAsync();
        p.Viewer.Disconnect();
        await p.HostDisconnected.Task.WaitAsync(Limit);
        long controlSent = p.HostT.ControlBytesSent;

        p.Host.Approve();
        Assert.False(p.Host.IsApproved);
        await p.Host.SendFrameAsync(Frame, full: true);
        await p.Host.SendChatAsync("too late");
        Assert.Equal(0, p.HostT.MediaBytesSent);
        Assert.Equal(controlSent, p.HostT.ControlBytesSent);
        Assert.Equal(0, p.ViewerEvents);
    }
}
