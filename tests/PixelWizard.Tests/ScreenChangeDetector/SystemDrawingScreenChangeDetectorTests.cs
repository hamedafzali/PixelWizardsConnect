using Xunit;

namespace PixelWizard.Tests.ScreenChangeDetector;

/// <summary>
/// Windows counterpart of SkiaScreenChangeDetectorTests, run against the identical
/// assertion suite bodies. Only compiled into the net9.0-windows build of this project
/// (see PixelWizard.Tests.csproj). Each case is [SkippableFact]-gated so it skips
/// loudly (not silently, not by simply being absent) when not actually on Windows —
/// this suite has never executed for real on this (macOS) machine; it is exercised by
/// T4's windows-latest CI job.
/// </summary>
public class SystemDrawingScreenChangeDetectorTests : ScreenChangeDetectorAssertionSuite
{
    protected override IDetectorHarness CreateHarness() => new SystemDrawingDetectorHarness();

    private static void SkipIfNotWindows() => Skip.IfNot(OperatingSystem.IsWindows(),
        "System.Drawing.Common only runs on Windows under .NET 9 (confirmed: throws " +
        "TypeInitializationException on macOS). Unexecuted here — verified by T4's " +
        "windows-latest CI job, not on this machine.");

    [SkippableFact] public void FirstFrame_NoPriorFrame_ProducesFullFrame() { SkipIfNotWindows(); FirstFrame_NoPriorFrame_ProducesFullFrame_Impl(); }
    [SkippableFact] public void IdenticalFrames_NoChanges() { SkipIfNotWindows(); IdenticalFrames_NoChanges_Impl(); }
    [SkippableFact] public void SingleTileChange_ProducesExactRegion() { SkipIfNotWindows(); SingleTileChange_ProducesExactRegion_Impl(); }
    [SkippableFact] public void FullFrameChange_CoversWholeImage() { SkipIfNotWindows(); FullFrameChange_CoversWholeImage_Impl(); }
    [SkippableFact] public void ResolutionChange_ProducesFullFrameNoCrash() { SkipIfNotWindows(); ResolutionChange_ProducesFullFrameNoCrash_Impl(); }

    [SkippableFact] public void ThresholdBelow_NotFlagged() { SkipIfNotWindows(); ThresholdBelow_NotFlagged_Impl(); }
    [SkippableFact] public void ThresholdAbove_Flagged() { SkipIfNotWindows(); ThresholdAbove_Flagged_Impl(); }
    [SkippableFact] public void SampleFractionBelow_NotFlagged() { SkipIfNotWindows(); SampleFractionBelow_NotFlagged_Impl(); }
    [SkippableFact] public void SampleFractionAbove_Flagged() { SkipIfNotWindows(); SampleFractionAbove_Flagged_Impl(); }
    [SkippableFact] public void SamplingBlindSpot_NotFlagged_DocumentedGap() { SkipIfNotWindows(); SamplingBlindSpot_NotFlagged_DocumentedGap_Impl(); }
    [SkippableFact] public void BoundaryStraddle_BothTilesRegistered() { SkipIfNotWindows(); BoundaryStraddle_BothTilesRegistered_Impl(); }
    [SkippableFact] public void AdjacentTiles_MergedIntoOneRegion() { SkipIfNotWindows(); AdjacentTiles_MergedIntoOneRegion_Impl(); }
    [SkippableFact] public void FarApartTiles_RemainSeparate() { SkipIfNotWindows(); FarApartTiles_RemainSeparate_Impl(); }
}
