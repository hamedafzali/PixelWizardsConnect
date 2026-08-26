using System.Drawing;
using PixelWizard.WindowsHost;

namespace PixelWizard.Tests.ScreenChangeDetector;

/// <summary>
/// Drives PixelWizard.WindowsHost.ScreenChangeDetector (System.Drawing). Only compiled
/// into the net9.0-windows build of this test project (see the .csproj) — referencing
/// PixelWizard.WindowsHost from the plain net9.0 build fails at restore time (NU1201),
/// and the underlying Bitmap calls throw on any non-Windows OS regardless of TFM. See
/// SystemDrawingScreenChangeDetectorTests for the runtime OS skip.
/// </summary>
public sealed class SystemDrawingDetectorHarness : IDetectorHarness
{
    private readonly PixelWizard.WindowsHost.ScreenChangeDetector _detector = new();

    public int Width { get; private set; }
    public int Height { get; private set; }

    public IReadOnlyList<Rect> Detect(string pngPath, bool forceFullFrame = false)
    {
        byte[] bytes = File.ReadAllBytes(pngPath);
        using var ms = new MemoryStream(bytes);
        using var bitmap = new Bitmap(ms);
        Width = bitmap.Width;
        Height = bitmap.Height;

        var deltas = _detector.DetectChanges(bitmap, forceFullFrame, 80L);
        return deltas.Select(d => new Rect(d.X, d.Y, d.Width, d.Height)).ToList();
    }

    public void Dispose() => _detector.Dispose();
}
