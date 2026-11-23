using System.Collections.Concurrent;
using PixelWizard.Core;
using PixelWizard.Core.Interfaces;
using PixelWizard.Media.H264;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Signaling;
using Xunit;
using Xunit.Abstractions;

namespace PixelWizard.Transport.WebRtc.Tests;

/// <summary>
/// P4.8: HostSession and ViewerSession over the real WebRtcTransport with the H.264 track. Proves the D4 consent rule
/// for video (no capture, no encoder, no access unit, no RTP before Approve; deny closes with none sent), the
/// negotiation rules (H.264 only when both peers advertise it, otherwise media-jpeg exactly as before), a
/// working capture-encode-track-decode path, PLI to keyframe, and stop on disconnect.
/// </summary>
[Collection("router")]
public class VideoSessionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private const int W = 640, H = 360;
    private const SupportedCodecs Both = SupportedCodecs.Jpeg | SupportedCodecs.H264;

    private readonly RouterFixture _router;
    private readonly ITestOutputHelper _out;
    public VideoSessionTests(RouterFixture router, ITestOutputHelper output) { _router = router; _out = output; }

    private static HelloMessage Hello(SupportedCodecs codecs) => new()
    {
        ProtocolVersion = ProtocolVersions.Current, Role = PeerRole.Full, Codecs = codecs, MaxConcurrentStreams = 1,
    };

    private sealed class SyntheticCapture : IRawScreenCapture
    {
        public int Calls;
        private int _shift;
        public string DisplayId => "synthetic";
        public RawFrame? CaptureRaw()
        {
            Interlocked.Increment(ref Calls);
            var f = RawFrame.Rent(W, H, DisplayId);
            f.CopyFrom(FfmpegSupport.Pattern(W, H, _shift++ % 64), W * 4);
            return f;
        }
        public void Dispose() { }
    }

    private sealed class FailingPipeline : IHostVideoPipeline
    {
        public bool TryStart(out string reason) { reason = "encoder unavailable (test)"; return false; }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class Rig : IDisposable
    {
        public WebRtcTransport HostT = null!, ViewerT = null!;
        public HostSession Host = null!;
        public ViewerSession Viewer = null!;
        public SyntheticCapture Capture = new();
        public H264HostPipeline? Pipeline;
        public H264ViewerReceiver? Receiver;
        public int FactoryCalls;
        public readonly ConcurrentQueue<DecodedFrame> Frames = new();
        public int FullScreens;
        public readonly TaskCompletionSource HandshakeVerified = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource HostDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ViewerDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            Receiver?.Dispose();
            Pipeline?.Dispose();
            HostT.Dispose(); ViewerT.Dispose();
        }
    }

    private async Task<Rig> ConnectAsync(SupportedCodecs hostCodecs, SupportedCodecs viewerCodecs, bool hostTrack, bool viewerTrack,
        Func<Rig, IHostVideoPipeline?>? pipeline = null, int gopSeconds = 60)
    {
        var r = new Rig();
        r.HostT = new WebRtcTransport { EstablishTimeout = Limit, EnableVideoTrack = hostTrack };
        r.ViewerT = new WebRtcTransport { EstablishTimeout = Limit, EnableVideoTrack = viewerTrack };
        r.Host = new HostSession(r.HostT, Hello(hostCodecs), "");
        r.Viewer = new ViewerSession(() => r.ViewerT, Hello(viewerCodecs), "");
        r.Host.VideoPipelineFactory = () =>
        {
            Interlocked.Increment(ref r.FactoryCalls);
            if (pipeline != null) return pipeline(r);
            return r.Pipeline = new H264HostPipeline(r.HostT, r.Capture, fps: 15, gopSeconds: gopSeconds);
        };
        r.Host.HandshakeVerified += () => r.HandshakeVerified.TrySetResult();
        r.Host.Disconnected += () => r.HostDisconnected.TrySetResult();
        r.Viewer.Disconnected += () => r.ViewerDisconnected.TrySetResult();
        r.Viewer.FullScreenReceived += _ => Interlocked.Increment(ref r.FullScreens);
        if (viewerCodecs.HasFlag(SupportedCodecs.H264) && r.Viewer.VideoTrack != null)
        {
            Assert.True(H264ViewerReceiver.TryCreate(r.Viewer.VideoTrack, f => r.Frames.Enqueue(f), out r.Receiver, out var why), why);
        }

        using var cts = new CancellationTokenSource(Limit);
        var ct = cts.Token;
        var hostSig = new SignalingClient("http://127.0.0.1", _router.Port);
        var viewerSig = new SignalingClient("http://127.0.0.1", _router.Port);
        var reg = await hostSig.RegisterAsync(ct);
        var waitForPeer = hostSig.WaitForPeerAsync(ct);
        var joined = await viewerSig.JoinAsync(reg.Code, ct);
        var peerJoined = await waitForPeer;
        await Task.WhenAll(
            r.HostT.EstablishAsync(hostSig, peerJoined.Turn, WebRtcRole.Host, ct: ct),
            r.ViewerT.EstablishAsync(viewerSig, joined.Turn, WebRtcRole.Viewer, ct: ct)).WaitAsync(Limit);
        await r.HandshakeVerified.Task.WaitAsync(Limit);
        Assert.False(r.Host.IsApproved);
        return r;
    }

    private static async Task WaitUntil(Func<bool> cond, string what, TimeSpan? limit = null)
    {
        var until = DateTime.UtcNow + (limit ?? Limit);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [RouterAndFfmpegFact]
    public async Task Before_approval_no_capture_no_encoder_and_no_video_is_sent()
    {
        FfmpegSupport.Load();
        using var r = await ConnectAsync(Both, Both, true, true);
        Assert.True(r.Host.UseH264Video); // everything is agreed; only the host's consent is missing

        await Task.Delay(1500); // long enough for any wrongly started pipeline to have sent several frames
        Assert.Equal(0, r.FactoryCalls);
        Assert.Equal(0, r.Capture.Calls);
        Assert.False(r.Host.VideoActive);
        Assert.Equal(0, r.HostT.VideoAccessUnitsSent);
        Assert.Equal(0, r.ViewerT.VideoAccessUnitsReceived);
        Assert.Equal(0, r.Receiver!.UnitsReceived);
        Assert.Equal(0, r.HostT.MediaBytesSent);
        Assert.True(r.Host.IsConnected && r.Viewer.IsConnected);
    }

    [RouterAndFfmpegFact]
    public async Task Deny_closes_the_connection_and_no_video_was_ever_sent()
    {
        FfmpegSupport.Load();
        using var r = await ConnectAsync(Both, Both, true, true);

        bool approved = await r.Host.ResolveConsentAsync(_ => Task.FromResult(false), "viewer");
        Assert.False(approved);
        await r.ViewerDisconnected.Task.WaitAsync(Limit);
        await r.HostDisconnected.Task.WaitAsync(Limit);
        Assert.Equal(0, r.FactoryCalls);
        Assert.Equal(0, r.Capture.Calls);
        Assert.False(r.Host.VideoActive);
        Assert.Equal(0, r.HostT.VideoAccessUnitsSent);
        Assert.Equal(0, r.ViewerT.VideoAccessUnitsReceived);
        Assert.Equal(0, r.HostT.MediaBytesSent);
        Assert.Empty(r.Frames);
    }

    [RouterAndFfmpegFact]
    public async Task After_approval_captured_frames_arrive_decoded_over_the_track_and_not_over_media_jpeg()
    {
        FfmpegSupport.Load();
        using var r = await ConnectAsync(Both, Both, true, true);

        r.Host.Approve();
        Assert.True(r.Host.IsApproved);
        Assert.True(r.Host.VideoActive, r.Host.VideoFallbackReason);
        Assert.Equal(1, r.FactoryCalls);
        await WaitUntil(() => r.Frames.Count >= 8, "8 decoded frames");

        var src = FfmpegSupport.Pattern(W, H, 0);
        DecodedFrame? last = null;
        while (r.Frames.TryDequeue(out var f)) last = f;
        Assert.Equal((W, H), (last!.Width, last.Height));
        // The synthetic bars scroll by one column step per frame, so compare against the nearest shift.
        double best = double.MaxValue;
        for (int s = 0; s < 64; s++) best = Math.Min(best, FfmpegSupport.MeanAbsError(FfmpegSupport.Pattern(W, H, s), last.Bgra));
        _out.WriteLine($"encoder {r.Pipeline!.EncoderName}: {r.Receiver!.FramesDecoded} frames decoded, mean abs error {best:0.00}, keyframes sent {r.Pipeline.KeyframesSent}");
        Assert.True(best < 8, $"mean abs error {best:0.00} is too high");
        Assert.True(r.Pipeline.KeyframesSent >= 1);
        Assert.True(r.HostT.VideoAccessUnitsSent > 0);
        Assert.Equal(0, r.HostT.MediaBytesSent); // media-jpeg stayed idle
        Assert.Equal(0, r.Receiver.KeyframeRequests); // a clean stream needs no PLI
    }

    private async Task AssertJpegFallback(Rig r)
    {
        Assert.False(r.Host.UseH264Video);
        r.Host.Approve();
        Assert.True(r.Host.IsApproved);
        Assert.False(r.Host.VideoActive);
        Assert.Equal(0, r.FactoryCalls);
        await r.Host.SendFrameAsync(new ScreenDelta { Width = 1, Height = 1, ImageData = new byte[] { 1, 2, 3 } }, full: true);
        await WaitUntil(() => Volatile.Read(ref r.FullScreens) == 1, "JPEG frame over media-jpeg");
        Assert.True(r.HostT.MediaBytesSent > 0);
        Assert.Equal(0, r.HostT.VideoAccessUnitsSent);
        Assert.Equal(0, r.Capture.Calls);
    }

    [RouterFact]
    public async Task H264_is_not_used_when_the_viewer_does_not_advertise_it()
    {
        using var r = await ConnectAsync(Both, SupportedCodecs.Jpeg, true, false);
        await AssertJpegFallback(r);
    }

    [RouterFact]
    public async Task H264_is_not_used_when_the_host_does_not_advertise_it()
    {
        using var r = await ConnectAsync(SupportedCodecs.Jpeg, Both, false, true);
        await AssertJpegFallback(r);
    }

    [RouterFact]
    public async Task A_pipeline_that_cannot_start_falls_back_to_JPEG_with_the_reason()
    {
        using var r = await ConnectAsync(Both, Both, true, true, _ => new FailingPipeline());
        Assert.True(r.Host.UseH264Video);
        r.Host.Approve();
        Assert.True(r.Host.IsApproved);
        Assert.False(r.Host.VideoActive);
        Assert.Equal("encoder unavailable (test)", r.Host.VideoFallbackReason);
        await r.Host.SendFrameAsync(new ScreenDelta { Width = 1, Height = 1, ImageData = new byte[] { 9 } }, full: true);
        await WaitUntil(() => Volatile.Read(ref r.FullScreens) == 1, "JPEG frame after fallback");
        Assert.Equal(0, r.HostT.VideoAccessUnitsSent);
    }

    [RouterAndFfmpegFact]
    public async Task A_viewer_keyframe_request_makes_the_host_send_a_keyframe()
    {
        FfmpegSupport.Load();
        // A two-minute GOP, so the only IDRs are the first frame and the one the PLI asks for.
        using var r = await ConnectAsync(Both, Both, true, true, gopSeconds: 120);
        r.Host.Approve();
        Assert.True(r.Host.VideoActive, r.Host.VideoFallbackReason);
        await WaitUntil(() => r.Frames.Count >= 5, "stream running");
        Assert.Equal(1, r.Pipeline!.KeyframesSent);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        r.Viewer.VideoTrack!.RequestKeyframe();
        // The encoder has pipeline latency, so the IDR is not the very next unit; allow for it.
        await WaitUntil(() => r.Pipeline.KeyframesSent >= 2, "an IDR after the PLI", TimeSpan.FromSeconds(10));
        _out.WriteLine($"PLI to IDR leaving the host: {sw.ElapsedMilliseconds} ms ({r.Pipeline.EncoderName})");
        // ... and the viewer still decodes after it.
        long before = r.Receiver!.FramesDecoded;
        await WaitUntil(() => r.Receiver.FramesDecoded > before + 3, "frames after the IDR");
    }

    [RouterAndFfmpegFact]
    public async Task Disconnect_stops_the_pipeline_and_releases_the_video()
    {
        FfmpegSupport.Load();
        using var r = await ConnectAsync(Both, Both, true, true);
        r.Host.Approve();
        Assert.True(r.Host.VideoActive, r.Host.VideoFallbackReason);
        await WaitUntil(() => r.Frames.Count >= 3, "stream running");

        r.Viewer.Disconnect();
        await r.HostDisconnected.Task.WaitAsync(Limit);
        Assert.False(r.Host.VideoActive);
        await Task.Delay(300); // let a frame already in flight finish
        long sent = r.Pipeline!.AccessUnitsSent;
        await Task.Delay(700);
        Assert.Equal(sent, r.Pipeline.AccessUnitsSent);
        Assert.Null(r.Pipeline.EncoderName); // encoder released
    }
}
