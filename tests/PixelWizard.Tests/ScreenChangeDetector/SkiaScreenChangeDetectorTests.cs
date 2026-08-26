using Xunit;

namespace PixelWizard.Tests.ScreenChangeDetector;

public class SkiaScreenChangeDetectorTests : ScreenChangeDetectorAssertionSuite
{
    protected override IDetectorHarness CreateHarness() => new SkiaDetectorHarness();

    [Fact] public void FirstFrame_NoPriorFrame_ProducesFullFrame() => FirstFrame_NoPriorFrame_ProducesFullFrame_Impl();
    [Fact] public void IdenticalFrames_NoChanges() => IdenticalFrames_NoChanges_Impl();
    [Fact] public void SingleTileChange_ProducesExactRegion() => SingleTileChange_ProducesExactRegion_Impl();
    [Fact] public void FullFrameChange_CoversWholeImage() => FullFrameChange_CoversWholeImage_Impl();
    [Fact] public void ResolutionChange_ProducesFullFrameNoCrash() => ResolutionChange_ProducesFullFrameNoCrash_Impl();

    [Fact] public void ThresholdBelow_NotFlagged() => ThresholdBelow_NotFlagged_Impl();
    [Fact] public void ThresholdAbove_Flagged() => ThresholdAbove_Flagged_Impl();
    [Fact] public void SampleFractionBelow_NotFlagged() => SampleFractionBelow_NotFlagged_Impl();
    [Fact] public void SampleFractionAbove_Flagged() => SampleFractionAbove_Flagged_Impl();
    [Fact] public void SamplingBlindSpot_NotFlagged_DocumentedGap() => SamplingBlindSpot_NotFlagged_DocumentedGap_Impl();
    [Fact] public void BoundaryStraddle_BothTilesRegistered() => BoundaryStraddle_BothTilesRegistered_Impl();
    [Fact] public void AdjacentTiles_MergedIntoOneRegion() => AdjacentTiles_MergedIntoOneRegion_Impl();
    [Fact] public void FarApartTiles_RemainSeparate() => FarApartTiles_RemainSeparate_Impl();
}
