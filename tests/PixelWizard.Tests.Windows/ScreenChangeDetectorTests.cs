using System.Drawing;
using System.Drawing.Imaging;
using PixelWizard.Core.Protocol;
using PixelWizard.WindowsHost;
using Xunit;

namespace PixelWizard.Tests.Windows;

/// <summary>
/// T2: Windows counterpart to SkiaScreenChangeDetectorTests in PixelWizard.Tests, run
/// against the same checked-in PNG fixture pairs with the same assertion suite, so the
/// System.Drawing-based detector and the Skia-based ones are held to identical behavior
/// without being consolidated (see STATUS_REPORT.md). Only buildable/runnable on Windows.
/// </summary>
public class ScreenChangeDetectorTests
{
    private const long JpegQuality = 80;
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ScreenChangeDetector");

    private static Bitmap LoadFixture(string name)
    {
        // Bitmap keeps the backing file mapped/locked on Windows unless the source stream
        // is fully copied first; load via a MemoryStream so the file isn't held open.
        byte[] bytes = File.ReadAllBytes(Path.Combine(FixtureDir, name + ".png"));
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }

    private static List<ScreenDelta> RunSecondFrame(
        string beforeName, string afterName, bool forceFullFrameOnSecond = false)
    {
        using var before = LoadFixture(beforeName);
        using var after = LoadFixture(afterName);
        using var detector = new ScreenChangeDetector();

        detector.DetectChanges(before, forceFullFrame: false, JpegQuality); // seed _previousFrame
        return detector.DetectChanges(after, forceFullFrameOnSecond, JpegQuality);
    }

    [Fact]
    public void IdenticalFrames_ProducesNoDeltas()
    {
        var deltas = RunSecondFrame("Identical_Before", "Identical_After");
        Assert.Empty(deltas);
    }

    [Fact]
    public void SingleTileChange_ProducesExactlyOneRegion_MatchingTheChangedBlock()
    {
        var deltas = RunSecondFrame("SingleTileChange_Before", "SingleTileChange_After");

        var delta = Assert.Single(deltas);
        Assert.Equal(32, delta.X);
        Assert.Equal(32, delta.Y);
        Assert.Equal(32, delta.Width);
        Assert.Equal(32, delta.Height);
    }

    [Fact]
    public void FullFrameChange_ProducesRegionCoveringTheWholeImage()
    {
        var deltas = RunSecondFrame("FullFrameChange_Before", "FullFrameChange_After");

        Assert.NotEmpty(deltas);
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

    [Fact]
    public void ResolutionChange_ProducesSingleFullFrameDelta_AtNewDimensions()
    {
        var deltas = RunSecondFrame("ResolutionChange_Before", "ResolutionChange_After");

        var delta = Assert.Single(deltas);
        Assert.Equal(0, delta.X);
        Assert.Equal(0, delta.Y);
        Assert.Equal(256, delta.Width);
        Assert.Equal(256, delta.Height);
    }

    [Fact]
    public void GradientWithSubThresholdNoise_ProducesNoDeltas()
    {
        var deltas = RunSecondFrame("GradientNoise_Before", "GradientNoise_After");
        Assert.Empty(deltas);
    }
}
