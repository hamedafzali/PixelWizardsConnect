namespace PixelWizard.Tests.ScreenChangeDetector;

public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;
}

/// <summary>
/// Test-only adapter so ScreenChangeDetectorAssertionSuite can drive both the
/// System.Drawing and SkiaSharp detector implementations through one shared API,
/// despite them taking different native bitmap types. Each call feeds a frame into
/// the underlying (stateful) detector and returns the changed regions it reports;
/// the first call on a fresh harness always reports a full-frame region, since the
/// detector has no prior frame yet.
/// </summary>
public interface IDetectorHarness : IDisposable
{
    int Width { get; }
    int Height { get; }

    IReadOnlyList<Rect> Detect(string pngPath, bool forceFullFrame = false);
}
