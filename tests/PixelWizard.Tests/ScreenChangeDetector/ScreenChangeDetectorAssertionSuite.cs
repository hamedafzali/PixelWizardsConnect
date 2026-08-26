using Xunit;

namespace PixelWizard.Tests.ScreenChangeDetector;

/// <summary>
/// One assertion suite, written once, run against both ScreenChangeDetector
/// (System.Drawing) and SkiaScreenChangeDetector (SkiaSharp) via IDetectorHarness —
/// see SkiaScreenChangeDetectorTests and SystemDrawingScreenChangeDetectorTests for
/// the thin per-implementation [Fact]/[SkippableFact] wrappers around these bodies.
///
/// Per T2 hard rules: this suite does not consolidate or "fix" either detector. If the
/// two disagree, that is a finding for docs/BACKLOG.md, not something to reconcile here.
/// </summary>
public abstract class ScreenChangeDetectorAssertionSuite
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ScreenChangeDetector");

    protected abstract IDetectorHarness CreateHarness();

    private static string Fixture(string name) => Path.Combine(FixtureDir, name + ".png");

    // ---- Structural cases ----

    protected void FirstFrame_NoPriorFrame_ProducesFullFrame_Impl()
    {
        using var harness = CreateHarness();
        var regions = harness.Detect(Fixture("Identical_Before"));

        var region = Assert.Single(regions);
        Assert.Equal(new Rect(0, 0, harness.Width, harness.Height), region);
    }

    protected void IdenticalFrames_NoChanges_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("Identical_Before"));
        var regions = harness.Detect(Fixture("Identical_After"));

        Assert.Empty(regions);
    }

    protected void SingleTileChange_ProducesExactRegion_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("SingleTileChange_Before"));
        var regions = harness.Detect(Fixture("SingleTileChange_After"));

        var region = Assert.Single(regions);
        Assert.Equal(new Rect(32, 32, 32, 32), region);
    }

    protected void FullFrameChange_CoversWholeImage_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("FullFrameChange_Before"));
        var regions = harness.Detect(Fixture("FullFrameChange_After"));

        var mask = PixelDiffGroundTruth.ComputeChangedMask(
            Fixture("FullFrameChange_Before"), Fixture("FullFrameChange_After"));
        AssertAllChangedPixelsCovered(regions, mask);
        AssertNoRegionEntirelyUnchanged(regions, mask);
        Assert.InRange(regions.Count, 1, 8); // sanity bound; exact merge shape is not the contract
    }

    protected void ResolutionChange_ProducesFullFrameNoCrash_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("ResolutionChange_Before"));
        var regions = harness.Detect(Fixture("ResolutionChange_After"));

        var region = Assert.Single(regions);
        Assert.Equal(new Rect(0, 0, harness.Width, harness.Height), region);
    }

    // ---- Algorithm-boundary cases ----

    protected void ThresholdBelow_NotFlagged_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("ThresholdBelow_Before"));
        var regions = harness.Detect(Fixture("ThresholdBelow_After"));

        Assert.Empty(regions); // per-pixel diff == 10, condition is strictly > 10
    }

    protected void ThresholdAbove_Flagged_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("ThresholdAbove_Before"));
        var regions = harness.Detect(Fixture("ThresholdAbove_After"));

        var region = Assert.Single(regions);
        Assert.Equal(new Rect(32, 32, 32, 32), region);
    }

    protected void SampleFractionBelow_NotFlagged_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("SampleFractionBelow_Before"));
        var regions = harness.Detect(Fixture("SampleFractionBelow_After"));

        // 6/64 sampled pixels changed (9.375%) — below the block's 10% threshold.
        // Real per-pixel differences exist (ground truth would show them) but the
        // block-level decision correctly suppresses them; this is expected, not a gap.
        Assert.Empty(regions);
    }

    protected void SampleFractionAbove_Flagged_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("SampleFractionAbove_Before"));
        var regions = harness.Detect(Fixture("SampleFractionAbove_After"));

        // 7/64 sampled pixels changed (10.9375%) — above the block's 10% threshold.
        var region = Assert.Single(regions);
        Assert.Equal(new Rect(32, 32, 32, 32), region);
    }

    protected void SamplingBlindSpot_NotFlagged_DocumentedGap_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("SamplingBlindSpot_Before"));
        var regions = harness.Detect(Fixture("SamplingBlindSpot_After"));

        // A single pixel at local offset (1,1) within the tile was changed — a real,
        // visible difference — but it lands off the every-4th-pixel sampling grid, so
        // the detector never observes it. This is a genuine algorithmic blind spot;
        // this test documents the current (unflagged) behavior, it does not fix it.
        Assert.Empty(regions);
    }

    protected void BoundaryStraddle_BothTilesRegistered_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("BoundaryStraddle_Before"));
        var regions = harness.Detect(Fixture("BoundaryStraddle_After"));

        var mask = PixelDiffGroundTruth.ComputeChangedMask(
            Fixture("BoundaryStraddle_Before"), Fixture("BoundaryStraddle_After"));
        AssertAllChangedPixelsCovered(regions, mask);
        AssertNoRegionEntirelyUnchanged(regions, mask);
        // The two straddled tiles are edge-adjacent, so MergeBlocks may fold them into
        // one region or leave two — either is a valid outcome of the merge heuristic;
        // what matters is that both sides of the boundary were actually detected.
        Assert.InRange(regions.Count, 1, 2);
    }

    protected void AdjacentTiles_MergedIntoOneRegion_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("AdjacentTiles_Before"));
        var regions = harness.Detect(Fixture("AdjacentTiles_After"));

        var mask = PixelDiffGroundTruth.ComputeChangedMask(
            Fixture("AdjacentTiles_Before"), Fixture("AdjacentTiles_After"));
        var region = Assert.Single(regions); // proves the two tiles were merged
        AssertAllChangedPixelsCovered(regions, mask);
        AssertNoRegionEntirelyUnchanged(regions, mask);
    }

    protected void FarApartTiles_RemainSeparate_Impl()
    {
        using var harness = CreateHarness();
        harness.Detect(Fixture("FarApartTiles_Before"));
        var regions = harness.Detect(Fixture("FarApartTiles_After"));

        Assert.Equal(2, regions.Count);
        Assert.Contains(regions, r => r.Contains(33, 33));   // block (1,1)
        Assert.Contains(regions, r => r.Contains(161, 161)); // block (5,5)
    }

    // ---- Shared coverage-style assertion helpers ----

    private static void AssertAllChangedPixelsCovered(IReadOnlyList<Rect> regions, bool[,] mask)
    {
        int width = mask.GetLength(0), height = mask.GetLength(1);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (!mask[x, y]) continue;
            Assert.True(regions.Any(r => r.Contains(x, y)),
                $"Changed pixel ({x},{y}) is not covered by any reported region.");
        }
    }

    private static void AssertNoRegionEntirelyUnchanged(IReadOnlyList<Rect> regions, bool[,] mask)
    {
        int width = mask.GetLength(0), height = mask.GetLength(1);
        foreach (var region in regions)
        {
            bool anyChanged = false;
            for (int y = region.Y; y < Math.Min(region.Bottom, height) && !anyChanged; y++)
            for (int x = region.X; x < Math.Min(region.Right, width) && !anyChanged; x++)
                if (mask[x, y]) anyChanged = true;

            Assert.True(anyChanged, $"Region {region} contains no actually-changed pixel.");
        }
    }
}
