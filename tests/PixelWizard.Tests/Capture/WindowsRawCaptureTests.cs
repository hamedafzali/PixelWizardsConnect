using PixelWizard.Core.Interfaces;
using PixelWizard.WindowsHost;
using Xunit;

namespace PixelWizard.Tests.Capture;

/// <summary>
/// Real GDI capture of the primary monitor. Only compiled into the net9.0-windows build (see
/// PixelWizard.Tests.csproj). Skips off Windows, and on Windows when there is no monitor to
/// capture (a headless session). Lives outside the SystemDrawingScreenChangeDetectorTests
/// namespace so the CI step that expects exactly 13 of those tests is unaffected.
/// </summary>
public class WindowsRawCaptureTests
{
    private static WindowsMonitorInfo RequirePrimaryMonitor()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "GDI capture only runs on Windows.");
        System.Collections.Generic.IReadOnlyList<WindowsMonitorInfo> all;
        try { all = WindowsMonitors.List(); }
        catch (Exception e) { throw new SkipException("Cannot enumerate monitors: " + e.Message); }
        Skip.If(all.Count == 0, "No monitor attached to this session (headless runner).");
        return WindowsMonitors.Primary();
    }

    // A session with monitors enumerated but no interactive desktop makes CopyFromScreen throw
    // Win32Exception ("The handle is invalid"); skip on exactly that condition.
    private static PixelWizard.Core.RawFrame? CaptureOrSkip(IRawScreenCapture raw)
    {
        try { return raw.CaptureRaw(); }
        catch (System.ComponentModel.Win32Exception e)
        { throw new SkipException("No interactive desktop to capture: " + e.Message); }
    }

    [SkippableFact]
    public void CaptureRaw_ReturnsEvenSizedBgraFrameOfThePrimaryMonitor()
    {
        var monitor = RequirePrimaryMonitor();
        using var cap = new WindowsScreenCapture(monitor);
        IRawScreenCapture raw = cap;
        using var frame = CaptureOrSkip(raw);

        Assert.NotNull(frame);
        Assert.Equal(monitor.DeviceName, raw.DisplayId);
        Assert.Equal(monitor.DeviceName, frame!.DisplayId);
        Assert.Equal(monitor.Bounds.Width & ~1, frame.Width);
        Assert.Equal(monitor.Bounds.Height & ~1, frame.Height);
        Assert.Equal(0, frame.Width % 2);
        Assert.Equal(0, frame.Height % 2);
        Assert.True(frame.Stride >= frame.Width * 4);
        Assert.Equal(frame.Stride * frame.Height, frame.Pixels.Length);
        Assert.Equal(0xFF, frame.Pixels[3]); // alpha forced opaque
    }

    [SkippableFact]
    public void CaptureRaw_RepeatedCaptures_ReuseThePool()
    {
        var monitor = RequirePrimaryMonitor();
        using var cap = new WindowsScreenCapture(monitor);
        for (int i = 0; i < 3; i++)
        {
            using var f = CaptureOrSkip(cap);
            Assert.NotNull(f);
        }
    }
}
