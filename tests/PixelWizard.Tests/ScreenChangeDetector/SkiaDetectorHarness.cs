using System.Reflection;
using PixelWizard.Core.Protocol;
using SkiaSharp;

namespace PixelWizard.Tests.ScreenChangeDetector;

/// <summary>
/// Drives PixelWizard.LinuxHost.SkiaScreenChangeDetector, which is `internal sealed`.
/// Rather than adding InternalsVisibleTo to a production project (out of scope for this
/// task), this loads the already-referenced LinuxHost assembly and invokes the detector
/// via reflection — its constructor and DetectChanges/Dispose members are all public,
/// only the containing type is internal.
/// </summary>
public sealed class SkiaDetectorHarness : IDetectorHarness
{
    private static readonly Assembly LinuxHostAssembly = Assembly.Load("PixelWizard.LinuxHost");
    private static readonly Type DetectorType =
        LinuxHostAssembly.GetType("PixelWizard.LinuxHost.SkiaScreenChangeDetector", throwOnError: true)!;
    private static readonly MethodInfo DetectMethod =
        DetectorType.GetMethod("DetectChanges", BindingFlags.Public | BindingFlags.Instance)!;
    private static readonly MethodInfo DisposeMethod =
        DetectorType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance)!;

    private readonly object _detector = Activator.CreateInstance(DetectorType)!;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public IReadOnlyList<Rect> Detect(string pngPath, bool forceFullFrame = false)
    {
        using var bitmap = SKBitmap.Decode(pngPath) ?? throw new InvalidOperationException($"Failed to decode {pngPath}");
        Width = bitmap.Width;
        Height = bitmap.Height;

        var deltas = (List<ScreenDelta>)DetectMethod.Invoke(_detector, new object[] { bitmap, forceFullFrame, 80L })!;
        return deltas.Select(d => new Rect(d.X, d.Y, d.Width, d.Height)).ToList();
    }

    public void Dispose() => DisposeMethod.Invoke(_detector, null);
}
