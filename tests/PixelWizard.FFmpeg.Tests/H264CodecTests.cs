using PixelWizard.Core;
using PixelWizard.Media.H264;
using Xunit;
using Xunit.Abstractions;

namespace PixelWizard.FFmpeg.Tests;

/// <summary>P4.8a: the H264Encoder / H264Decoder wrappers against the vendored FFmpeg.</summary>
public sealed class H264CodecTests
{
    private readonly ITestOutputHelper _out;
    public H264CodecTests(ITestOutputHelper output) => _out = output;

    private static void Load()
    {
        Assert.True(FfmpegLoader.TryLoad(Environment.GetEnvironmentVariable(FactIfFfmpegAttribute.EnvVar)!, out var err), err);
    }

    private static H264Encoder NewEncoder(int w, int h, ITestOutputHelper o, int gop = 30)
    {
        Load();
        Assert.True(H264Encoder.TryCreate(w, h, 30, 4_000_000, out var enc, out var reason, gop), "no encoder opened: " + reason);
        o.WriteLine($"encoder: {enc!.EncoderName}");
        foreach (var d in enc.Diagnostics) o.WriteLine("  " + d);
        return enc;
    }

    // Colour bars with a horizontal gradient overlay in the lower third. BGRA, opaque.
    private static byte[] Pattern(int w, int h, int shift = 0)
    {
        var bars = new (byte r, byte g, byte b)[]
        {
            (235, 235, 235), (235, 235, 16), (16, 235, 235), (16, 235, 16), (235, 16, 235), (235, 16, 16), (16, 16, 235), (16, 16, 16),
        };
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                byte r, g, b;
                if (y < h * 2 / 3) (r, g, b) = bars[((x + shift) * 8 / w) % 8];
                else { var v = (byte)(x * 255 / w); (r, g, b) = (v, v, v); }
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
            }
        return px;
    }

    private static List<EncodedAccessUnit> EncodeN(H264Encoder enc, int w, int h, int n)
    {
        var all = new List<EncodedAccessUnit>();
        var px = Pattern(w, h);
        for (var i = 0; i < n; i++) all.AddRange(enc.Encode(px, w * 4));
        return all;
    }

    [FactIfFfmpeg]
    public void Encoder_opens_an_lgpl_encoder_and_first_access_unit_is_a_keyframe_with_in_band_parameter_sets()
    {
        using var enc = NewEncoder(640, 360, _out);
        Assert.Contains(enc.EncoderName, H264Encoder.CandidateEncoders());
        var aus = EncodeN(enc, 640, 360, 10);
        Assert.NotEmpty(aus);
        Assert.True(aus[0].IsKeyframe);
        var types = AnnexB.NalTypes(aus[0].AnnexB);
        _out.WriteLine("first AU NAL types: " + string.Join(",", types));
        Assert.Contains(7, types);
        Assert.Contains(8, types);
        Assert.Contains(5, types);
        Assert.True(AnnexB.HasSpsPpsIdr(aus[0].AnnexB));
    }

    [FactIfFfmpeg]
    public void Round_trip_decoded_pixels_are_close_to_the_source()
    {
        const int w = 640, h = 360;
        using var enc = NewEncoder(w, h, _out);
        Assert.True(H264Decoder.TryCreate(out var dec, out var why), why);
        using var _ = dec;
        var src = Pattern(w, h);
        DecodedFrame? last = null;
        var decoded = 0;
        for (var i = 0; i < 30; i++)
            foreach (var au in enc.Encode(src, w * 4))
                foreach (var f in dec!.Decode(au.AnnexB)) { last = f; decoded++; }
        Assert.True(decoded > 0, "decoder produced no frames");
        Assert.Equal(w, last!.Width);
        Assert.Equal(h, last.Height);

        long sum = 0; long count = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var si = (y * w + x) * 4; var di = y * last.Stride + x * 4;
                for (var c = 0; c < 3; c++) { sum += Math.Abs(src[si + c] - last.Bgra[di + c]); count++; }
            }
        var mae = sum / (double)count;
        _out.WriteLine($"{enc.EncoderName}: {decoded} frames decoded, mean abs error per channel {mae:F2}");
        Assert.True(mae < 12, $"mean abs error {mae:F2} too high");
    }

    [FactIfFfmpeg]
    public void RequestKeyframe_makes_the_next_output_an_idr_with_parameter_sets()
    {
        using var enc = NewEncoder(640, 360, _out, gop: 1000);
        var px = Pattern(640, 360);
        for (var i = 0; i < 8; i++) enc.Encode(px, 640 * 4);
        enc.RequestKeyframe();
        var next = enc.Encode(px, 640 * 4);
        // An encoder with latency may emit later; pump a few more frames and look at the first keyframe after the request.
        var all = new List<EncodedAccessUnit>(next);
        for (var i = 0; i < 10 && !all.Any(a => a.IsKeyframe); i++) all.AddRange(enc.Encode(px, 640 * 4));
        var key = all.FirstOrDefault(a => a.IsKeyframe);
        Assert.NotNull(key);
        Assert.True(AnnexB.HasSpsPpsIdr(key!.AnnexB), "types: " + string.Join(",", AnnexB.NalTypes(key.AnnexB)));
        _out.WriteLine("flags after request: " + string.Join("", all.Select(a => a.IsKeyframe ? "K" : "p")));
        // gop is 1000, so a keyframe can only come from the request. It must be the frame submitted
        // right after it (pts 8); encoders with pipeline latency (VideoToolbox) emit earlier delta frames first.
        Assert.Equal(8, key.Pts);
    }

    [FactIfFfmpeg]
    public void Decoder_handles_a_resolution_change()
    {
        Load();
        Assert.True(H264Decoder.TryCreate(out var dec, out var why), why);
        using var _ = dec;
        foreach (var (w, h) in new[] { (640, 360), (320, 240), (640, 360) })
        {
            using var enc = NewEncoder(w, h, _out);
            // Encoders buffer a few frames (h264_mf more than VideoToolbox), so pump until the IDR is out.
            var px = Pattern(w, h);
            var aus = new List<EncodedAccessUnit>();
            var sent = 0;
            while (!aus.Any(a => a.IsKeyframe) && sent < 30) { aus.AddRange(enc.Encode(px, w * 4)); sent++; }
            _out.WriteLine($"{w}x{h}: first keyframe out after {sent} frames in");
            var key = aus.First(a => a.IsKeyframe);
            var frames = dec!.Decode(key.AnnexB);
            Assert.NotEmpty(frames);
            Assert.Equal((w, h), (frames[0].Width, frames[0].Height));
            Assert.Equal(w * h * 4, frames[0].Bgra.Length);
        }
    }

    [FactIfFfmpeg]
    public void TryCreate_reports_a_reason_when_no_candidate_opens()
    {
        Load();
        Assert.False(H264Encoder.TryCreate(640, 360, 30, 1_000_000, out var enc, out var reason, 30, new[] { "h264_does_not_exist" }));
        Assert.Null(enc);
        Assert.Contains("h264_does_not_exist", reason);
    }
}
