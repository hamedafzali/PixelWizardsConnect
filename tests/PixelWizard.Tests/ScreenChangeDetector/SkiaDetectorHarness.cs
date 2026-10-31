using PixelWizard.Protocol;
using SkiaSharp;

namespace PixelWizard.Tests.ScreenChangeDetector;

/// <summary>
/// Drives PixelWizard.LinuxHost.SkiaScreenChangeDetector. A direct reference, so moving
/// or renaming the detector is a build error here rather than a lookup failing at run time.
/// </summary>
public sealed class SkiaDetectorHarness : IDetectorHarness
{
    private readonly PixelWizard.LinuxHost.SkiaScreenChangeDetector _detector = new();

    public int Width { get; private set; }
    public int Height { get; private set; }

    public IReadOnlyList<Rect> Detect(string pngPath, bool forceFullFrame = false)
    {
        using var bitmap = SKBitmap.Decode(pngPath) ?? throw new InvalidOperationException($"Failed to decode {pngPath}");
        Width = bitmap.Width;
        Height = bitmap.Height;

        var deltas = _detector.DetectChanges(bitmap, forceFullFrame, 80L);
        return deltas.Select(d => new Rect(d.X, d.Y, d.Width, d.Height)).ToList();
    }

    public void Dispose() => _detector.Dispose();
}
