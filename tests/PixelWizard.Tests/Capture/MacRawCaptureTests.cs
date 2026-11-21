using PixelWizard.Core.Interfaces;
using PixelWizard.MacHost;
using System.Runtime.Versioning;
using Xunit;

namespace PixelWizard.Tests.Capture;

/// <summary>
/// Real CoreGraphics capture of the main display. Skips off macOS, and on macOS when the process
/// has no Screen Recording permission (CI runners typically do not) or no display. The permission
/// check is a side-effect-free preflight, run before constructing the capture, because the
/// MacScreenCapture constructor would otherwise prompt and open System Settings.
/// </summary>
[SupportedOSPlatform("osx")]
public class MacRawCaptureTests
{
    private static void RequireCapturableDisplay()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "CoreGraphics capture only runs on macOS.");
        Skip.IfNot(MacScreenCapture.HasScreenCaptureAccess(),
            "This process has no Screen Recording permission (CGPreflightScreenCaptureAccess is false).");
    }

    [SkippableFact]
    public void CaptureRaw_ReturnsEvenSizedBgraFrameOfTheMainDisplay()
    {
        RequireCapturableDisplay();
        using var cap = new MacScreenCapture();
        var (rw, rh) = cap.Resolution;
        Skip.If(rw <= 1 || rh <= 1, "No display attached.");

        IRawScreenCapture raw = cap;
        using var frame = raw.CaptureRaw();
        Assert.NotNull(frame);
        Assert.Equal(raw.DisplayId, frame!.DisplayId);
        Assert.Equal(rw & ~1, frame.Width);
        Assert.Equal(rh & ~1, frame.Height);
        Assert.Equal(0, frame.Width % 2);
        Assert.Equal(0, frame.Height % 2);
        Assert.True(frame.Stride >= frame.Width * 4);
        Assert.Equal(frame.Stride * frame.Height, frame.Pixels.Length);
        // A real desktop is never uniformly one value; also catches an all-zero (unwritten) buffer.
        var px = frame.Pixels;
        bool varied = false;
        for (int i = 4; i < px.Length && !varied; i += 4 * 97)
            varied = px[i] != px[0] || px[i + 1] != px[1] || px[i + 2] != px[2];
        Assert.True(varied, "Captured frame is uniform.");
    }

    [SkippableFact]
    public void CaptureRaw_RepeatedCaptures_Work()
    {
        RequireCapturableDisplay();
        using var cap = new MacScreenCapture();
        Skip.If(cap.Resolution.Width <= 1, "No display attached.");
        for (int i = 0; i < 3; i++)
        {
            using var f = ((IRawScreenCapture)cap).CaptureRaw();
            Assert.NotNull(f);
        }
    }
}
