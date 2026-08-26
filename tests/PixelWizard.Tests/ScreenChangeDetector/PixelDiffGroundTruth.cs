using SkiaSharp;

namespace PixelWizard.Tests.ScreenChangeDetector;

/// <summary>
/// Ground truth for "which pixels actually changed between two fixtures", computed
/// independently of either detector implementation (same per-pixel diff>threshold
/// rule the detectors use, but applied to every pixel, not just the sampled ones).
/// Used to assert on coverage properties instead of brittle exact-rect-lists.
/// </summary>
internal static class PixelDiffGroundTruth
{
    public static bool[,] ComputeChangedMask(string beforePath, string afterPath, int diffThreshold = 10)
    {
        using var before = SKBitmap.Decode(beforePath) ?? throw new InvalidOperationException($"Failed to decode {beforePath}");
        using var after = SKBitmap.Decode(afterPath) ?? throw new InvalidOperationException($"Failed to decode {afterPath}");

        if (before.Width != after.Width || before.Height != after.Height)
            throw new InvalidOperationException("ComputeChangedMask requires equal-sized before/after images.");

        var mask = new bool[after.Width, after.Height];
        for (int y = 0; y < after.Height; y++)
        for (int x = 0; x < after.Width; x++)
        {
            var b = before.GetPixel(x, y);
            var a = after.GetPixel(x, y);
            int diff = Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
            mask[x, y] = diff > diffThreshold;
        }
        return mask;
    }
}
