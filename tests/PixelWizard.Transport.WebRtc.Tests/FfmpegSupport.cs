using PixelWizard.Media.H264;
using Xunit;

namespace PixelWizard.Transport.WebRtc.Tests;

/// <summary>
/// [Fact] for tests that need both the router and the vendored FFmpeg. Reported as SKIPPED (never silently
/// passed) when either PIXELWIZARD_ROUTER_EXE or PIXELWIZARD_FFMPEG_DIR is missing; CI sets both and its
/// assert step fails on any skip.
/// </summary>
public sealed class RouterAndFfmpegFactAttribute : FactAttribute
{
    public const string FfmpegEnvVar = "PIXELWIZARD_FFMPEG_DIR";

    public RouterAndFfmpegFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RouterFactAttribute.EnvVar)))
            Skip = $"{RouterFactAttribute.EnvVar} is not set; these tests need the built router executable.";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(FfmpegEnvVar)) || !Directory.Exists(Environment.GetEnvironmentVariable(FfmpegEnvVar)))
            Skip = $"{FfmpegEnvVar} is not set to a directory of FFmpeg libraries";
    }
}

internal static class FfmpegSupport
{
    public static void Load() =>
        Assert.True(FfmpegLoader.TryLoad(Environment.GetEnvironmentVariable(RouterAndFfmpegFactAttribute.FfmpegEnvVar)!, out var err), err);

    // Colour bars over a gradient, opaque BGRA: sharp edges and smooth areas, so a codec error shows.
    public static byte[] Pattern(int w, int h, int shift = 0)
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

    public static double MeanAbsError(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0; int n = 0;
        for (var i = 0; i < a.Length; i += 4) // B, G, R; alpha is not coded
        {
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            n += 3;
        }
        return (double)sum / n;
    }
}
