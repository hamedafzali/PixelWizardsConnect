using System.Collections.Generic;
using SkiaSharp;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>
/// T2: shared assertion suite run against both near-duplicate SkiaScreenChangeDetector
/// implementations (Mac and Linux) using the same checked-in PNG fixture pairs. The two
/// classes are intentionally NOT consolidated yet (see STATUS_REPORT.md) — this suite
/// exists so that consolidation can happen safely later, once both are provably behaving
/// the same way. If one drifts from the other, the corresponding [Theory] case here fails
/// only for that implementation.
///
/// PixelWizard.WindowsHost.ScreenChangeDetector (System.Drawing) is NOT covered here: it
/// targets net9.0-windows, which this net9.0 test project cannot reference (NU1201), and
/// System.Drawing.Common does not run on non-Windows under .NET 9 regardless of TFM. See
/// PixelWizard.Tests.Windows for the equivalent suite against that implementation — it can
/// only build and run on Windows (verified there by CI, not here).
/// </summary>
public class SkiaScreenChangeDetectorTests
{
    private const long JpegQuality = 80;
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ScreenChangeDetector");

    private static SKBitmap LoadFixture(string name) =>
        SKBitmap.Decode(Path.Combine(FixtureDir, name + ".png"))
        ?? throw new InvalidOperationException($"Failed to decode fixture '{name}.png'");

    public static IEnumerable<object[]> Implementations()
    {
        yield return new object[] { "Mac" };
        yield return new object[] { "Linux" };
    }

    private static List<PixelWizard.Core.Protocol.ScreenDelta> RunSecondFrame(
        string implementation, string beforeName, string afterName, bool forceFullFrameOnSecond = false)
    {
        using var before = LoadFixture(beforeName);
        using var after = LoadFixture(afterName);

        switch (implementation)
        {
            case "Mac":
            {
                using var detector = new PixelWizard.AvaloniaClient.Platform.Mac.SkiaScreenChangeDetector();
                detector.DetectChanges(before, forceFullFrame: false, JpegQuality); // seed _prev
                return detector.DetectChanges(after, forceFullFrameOnSecond, JpegQuality);
            }
            case "Linux":
            {
                using var detector = new PixelWizard.LinuxHost.SkiaScreenChangeDetector();
                detector.DetectChanges(before, forceFullFrame: false, JpegQuality); // seed _prev
                return detector.DetectChanges(after, forceFullFrameOnSecond, JpegQuality);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(implementation));
        }
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void IdenticalFrames_ProducesNoDeltas(string implementation)
    {
        var deltas = RunSecondFrame(implementation, "Identical_Before", "Identical_After");
        Assert.Empty(deltas);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void SingleTileChange_ProducesExactlyOneRegion_MatchingTheChangedBlock(string implementation)
    {
        var deltas = RunSecondFrame(implementation, "SingleTileChange_Before", "SingleTileChange_After");

        var delta = Assert.Single(deltas);
        Assert.Equal(32, delta.X);
        Assert.Equal(32, delta.Y);
        Assert.Equal(32, delta.Width);
        Assert.Equal(32, delta.Height);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void FullFrameChange_ProducesRegionCoveringTheWholeImage(string implementation)
    {
        var deltas = RunSecondFrame(implementation, "FullFrameChange_Before", "FullFrameChange_After");

        Assert.NotEmpty(deltas);
        // Every block differs, so after merging, the union of all reported regions must
        // cover the entire 128x128 frame — whether that lands as one rect or several
        // adjacent ones is an implementation detail of the merge heuristic, not the
        // contract under test.
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var d in deltas)
        {
            minX = Math.Min(minX, d.X);
            minY = Math.Min(minY, d.Y);
            maxX = Math.Max(maxX, d.X + d.Width);
            maxY = Math.Max(maxY, d.Y + d.Height);
        }
        Assert.Equal(0, minX);
        Assert.Equal(0, minY);
        Assert.Equal(128, maxX);
        Assert.Equal(128, maxY);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ResolutionChange_ProducesSingleFullFrameDelta_AtNewDimensions(string implementation)
    {
        var deltas = RunSecondFrame(implementation, "ResolutionChange_Before", "ResolutionChange_After");

        var delta = Assert.Single(deltas);
        Assert.Equal(0, delta.X);
        Assert.Equal(0, delta.Y);
        Assert.Equal(256, delta.Width);
        Assert.Equal(256, delta.Height);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void GradientWithSubThresholdNoise_ProducesNoDeltas(string implementation)
    {
        // "After" differs from "before" by at most +/-3 per touched channel (see fixture
        // generation) — below the detector's per-pixel diff>10 threshold — so it must not
        // be mistaken for a real change.
        var deltas = RunSecondFrame(implementation, "GradientNoise_Before", "GradientNoise_After");
        Assert.Empty(deltas);
    }
}
