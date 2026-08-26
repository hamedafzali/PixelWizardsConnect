#!/usr/bin/env python3
"""
Generates the PNG fixture pairs used by ScreenChangeDetectorAssertionSuite
(tests/PixelWizard.Tests/ScreenChangeDetector). Deterministic, no external
inputs — re-running this script must reproduce byte-identical output.

Canvas is 224x224 (7x7 blocks of the detector's 32px block size) unless a
case specifically needs a different size (ResolutionChange).

Requires Pillow: pip install pillow
"""
import os
from PIL import Image

OUT_DIR = os.path.join(os.path.dirname(__file__), "ScreenChangeDetector")
SIZE = 224
BLOCK = 32


def base_pixel(x, y):
    return ((x * 3) % 256, (y * 5) % 256, ((x + y) * 2) % 256, 255)


def base_canvas(size=SIZE):
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            px[x, y] = base_pixel(x, y)
    return img


def solid_canvas(color=(100, 100, 100, 255), size=SIZE):
    return Image.new("RGBA", (size, size), color)


def invert_region(img, x0, y0, x1, y1):
    px = img.load()
    for y in range(y0, y1):
        for x in range(x0, x1):
            r, g, b, a = px[x, y]
            px[x, y] = (255 - r, 255 - g, 255 - b, a)


def shift_channel(img, x0, y0, x1, y1, channel, delta):
    px = img.load()
    for y in range(y0, y1):
        for x in range(x0, x1):
            c = list(px[x, y])
            c[channel] = max(0, min(255, c[channel] + delta))
            px[x, y] = tuple(c)


def set_pixel(img, x, y, color):
    img.putpixel((x, y), color)


def save(img, name):
    img.save(os.path.join(OUT_DIR, f"{name}.png"))


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    # --- Structural cases ---

    identical = base_canvas()
    save(identical, "Identical_Before")
    save(identical.copy(), "Identical_After")

    single_before = base_canvas()
    single_after = single_before.copy()
    invert_region(single_after, 32, 32, 64, 64)  # block (1,1)
    save(single_before, "SingleTileChange_Before")
    save(single_after, "SingleTileChange_After")

    full_before = base_canvas()
    full_after = full_before.copy()
    invert_region(full_after, 0, 0, SIZE, SIZE)
    save(full_before, "FullFrameChange_Before")
    save(full_after, "FullFrameChange_After")

    save(base_canvas(SIZE), "ResolutionChange_Before")
    save(base_canvas(SIZE * 2), "ResolutionChange_After")

    # FirstFrame reuses Identical_Before.png directly (no separate fixture
    # needed: the case is "no prior frame at all", tested against a lone
    # frame with nothing to compare against).

    # --- Algorithm-boundary cases ---
    # Sampling grid: block-local offsets 0,4,8,...,28 (8 per axis, 64
    # samples/block). Per-pixel diff = summed abs diff across R,G,B.
    # Detector flags a pixel when diff > 10, and a block when the flagged
    # fraction of *sampled* pixels is > 0.10 (> 6.4 of 64, i.e. >= 7).

    # Below/above the per-pixel diff threshold (10), applied uniformly
    # across an entire tile so the block-level 10% threshold is moot.
    thr_below_before = solid_canvas()
    thr_below_after = thr_below_before.copy()
    shift_channel(thr_below_after, 32, 32, 64, 64, 0, 10)  # diff == 10, not > 10
    save(thr_below_before, "ThresholdBelow_Before")
    save(thr_below_after, "ThresholdBelow_After")

    thr_above_before = solid_canvas()
    thr_above_after = thr_above_before.copy()
    shift_channel(thr_above_after, 32, 32, 64, 64, 0, 11)  # diff == 11, > 10
    save(thr_above_before, "ThresholdAbove_Before")
    save(thr_above_after, "ThresholdAbove_After")

    # Below/above the 10%-of-sampled-pixels-in-a-tile threshold (6/64 vs
    # 7/64), touching only pixels that land on the sampled grid
    # (block-local offsets that are multiples of 4).
    TILE_X, TILE_Y = 32, 32
    CONTRAST = (255, 255, 255, 255)

    frac_below_before = solid_canvas()
    frac_below_after = frac_below_before.copy()
    for i in range(6):  # local (0,0),(4,0),...,(20,0) -> 6/64 = 9.375%
        set_pixel(frac_below_after, TILE_X + i * 4, TILE_Y, CONTRAST)
    save(frac_below_before, "SampleFractionBelow_Before")
    save(frac_below_after, "SampleFractionBelow_After")

    frac_above_before = solid_canvas()
    frac_above_after = frac_above_before.copy()
    for i in range(7):  # local (0,0),(4,0),...,(24,0) -> 7/64 = 10.9375%
        set_pixel(frac_above_after, TILE_X + i * 4, TILE_Y, CONTRAST)
    save(frac_above_before, "SampleFractionAbove_Before")
    save(frac_above_after, "SampleFractionAbove_After")

    # A single pixel changed at a non-sampled offset (local (1,1) — not a
    # multiple of 4 in either axis). The detector's every-4th-pixel
    # sampling never observes it: this documents that real blind spot,
    # it does not work around it.
    blind_before = solid_canvas()
    blind_after = blind_before.copy()
    set_pixel(blind_after, TILE_X + 1, TILE_Y + 1, CONTRAST)
    save(blind_before, "SamplingBlindSpot_Before")
    save(blind_after, "SamplingBlindSpot_After")

    # A change straddling the boundary between two horizontally adjacent
    # tiles (block (0,0) and block (1,0)): an 8px-wide vertical strip
    # (columns 28..35) covering the first tile row's full height, wide
    # enough to cross each tile's own 10% threshold independently.
    straddle_before = base_canvas()
    straddle_after = straddle_before.copy()
    invert_region(straddle_after, 28, 0, 36, 32)
    save(straddle_before, "BoundaryStraddle_Before")
    save(straddle_after, "BoundaryStraddle_After")

    # Two whole tiles changed that are horizontally adjacent (share an
    # edge) -> expected to merge into a single region.
    adjacent_before = base_canvas()
    adjacent_after = adjacent_before.copy()
    invert_region(adjacent_after, 32, 0, 64, 32)   # block (1,0)
    invert_region(adjacent_after, 64, 0, 96, 32)   # block (2,0)
    save(adjacent_before, "AdjacentTiles_Before")
    save(adjacent_after, "AdjacentTiles_After")

    # Two whole tiles changed that are far apart (gap >> the merge margin
    # of one block size) -> expected to remain separate regions.
    far_before = base_canvas()
    far_after = far_before.copy()
    invert_region(far_after, 32, 32, 64, 64)     # block (1,1)
    invert_region(far_after, 160, 160, 192, 192)  # block (5,5)
    save(far_before, "FarApartTiles_Before")
    save(far_after, "FarApartTiles_After")

    print(f"Wrote fixtures to {OUT_DIR}")


if __name__ == "__main__":
    main()
