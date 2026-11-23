using System.Collections.Concurrent;
using PixelWizard.Media.H264;
using PixelWizard.Signaling;
using Xunit;
using Xunit.Abstractions;

namespace PixelWizard.Transport.WebRtc.Tests;

/// <summary>P4.8: the H.264 video track of the WebRtcTransport, paired through the real router.</summary>
[Collection("router")]
public class VideoTrackTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private readonly RouterFixture _router;
    private readonly ITestOutputHelper _out;

    public VideoTrackTests(RouterFixture router, ITestOutputHelper output) { _router = router; _out = output; }

    private async Task<(WebRtcTransport host, WebRtcTransport viewer)> PairAsync(bool hostVideo, bool viewerVideo)
    {
        using var cts = new CancellationTokenSource(Limit);
        var ct = cts.Token;
        var hostSig = new SignalingClient("http://127.0.0.1", _router.Port);
        var viewerSig = new SignalingClient("http://127.0.0.1", _router.Port);
        var reg = await hostSig.RegisterAsync(ct);
        var waitForPeer = hostSig.WaitForPeerAsync(ct);
        var joined = await viewerSig.JoinAsync(reg.Code, ct);
        var peerJoined = await waitForPeer;
        var host = new WebRtcTransport { EstablishTimeout = Limit, EnableVideoTrack = hostVideo };
        var viewer = new WebRtcTransport { EstablishTimeout = Limit, EnableVideoTrack = viewerVideo };
        await Task.WhenAll(
            host.EstablishAsync(hostSig, peerJoined.Turn, WebRtcRole.Host, ct: ct),
            viewer.EstablishAsync(viewerSig, joined.Turn, WebRtcRole.Viewer, ct: ct)).WaitAsync(Limit);
        return (host, viewer);
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
    public async Task Encoded_frames_cross_the_track_and_decode_to_the_source_picture()
    {
        FfmpegSupport.Load();
        const int w = 640, h = 360;
        var (host, viewer) = await PairAsync(true, true);
        using var _h = host; using var _v = viewer;
        Assert.True(host.VideoTrackNegotiated && viewer.VideoTrackNegotiated);

        var units = new ConcurrentQueue<byte[]>();
        viewer.VideoAccessUnitReceived += (au, _) => units.Enqueue(au);

        Assert.True(H264Encoder.TryCreate(w, h, 15, 2_000_000, out var enc, out var why, gopSize: 30), why);
        using var _e = enc;
        Assert.True(H264Decoder.TryCreate(out var dec, out why), why);
        using var _d = dec;

        var src = FfmpegSupport.Pattern(w, h);
        var frames = new List<DecodedFrame>();
        var sentUnits = 0;
        var until = DateTime.UtcNow + Limit;
        while (frames.Count < 5 && DateTime.UtcNow < until)
        {
            foreach (var au in enc!.Encode(src, w * 4)) { host.SendVideoAccessUnit(au.AnnexB, 6000); sentUnits++; }
            while (units.TryDequeue(out var got)) frames.AddRange(dec!.Decode(got));
            await Task.Delay(20);
        }
        Assert.True(frames.Count >= 5, $"decoded only {frames.Count} frames from {sentUnits} access units");
        var last = frames[^1];
        Assert.Equal((w, h), (last.Width, last.Height));
        var mae = FfmpegSupport.MeanAbsError(src, last.Bgra);
        _out.WriteLine($"encoder {enc!.EncoderName}: {frames.Count} frames decoded, mean abs error {mae:0.00}");
        Assert.True(mae < 8, $"mean abs error {mae:0.00} is too high");
    }

    [RouterFact]
    public async Task A_viewer_without_video_leaves_the_track_unnegotiated_on_both_sides()
    {
        var (host, viewer) = await PairAsync(true, false);
        using var _h = host; using var _v = viewer;
        Assert.False(host.VideoTrackNegotiated);
        Assert.False(viewer.VideoTrackNegotiated);
        host.SendVideoAccessUnit(new byte[] { 0, 0, 0, 1, 0x65, 1, 2, 3 }, 6000); // dropped, not sent
        Assert.Equal(0, host.VideoAccessUnitsSent);
        Assert.True(host.IsConnected && viewer.IsConnected); // the data channels are unaffected
    }

    [RouterFact]
    public async Task A_viewer_keyframe_request_reaches_the_host()
    {
        var (host, viewer) = await PairAsync(true, true);
        using var _h = host; using var _v = viewer;
        Assert.True(host.VideoTrackNegotiated && viewer.VideoTrackNegotiated);
        int requests = 0;
        host.KeyframeRequested += () => Interlocked.Increment(ref requests);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        viewer.RequestKeyframe();
        await WaitUntil(() => Volatile.Read(ref requests) >= 1, "PLI at the host");
        _out.WriteLine($"PLI delivered in {sw.ElapsedMilliseconds} ms");
    }
}
