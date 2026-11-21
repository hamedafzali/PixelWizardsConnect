using PixelWizard.Core;
using Xunit;

namespace PixelWizard.Tests;

public class RawFrameTests
{
    [Theory]
    [InlineData(1920, 1920)]
    [InlineData(1921, 1920)]
    [InlineData(1, 0)]
    [InlineData(1079, 1078)]
    public void EvenFloor_RoundsDownToEven(int input, int expected) =>
        Assert.Equal(expected, RawFrame.EvenFloor(input));

    [Fact]
    public void Rent_SetsGeometryAndExactPixelLength()
    {
        using var f = RawFrame.Rent(6, 4, "d1");
        Assert.Equal(6, f.Width);
        Assert.Equal(4, f.Height);
        Assert.Equal(24, f.Stride);
        Assert.Equal("d1", f.DisplayId);
        Assert.Equal(24 * 4, f.Pixels.Length);
    }

    [Theory]
    [InlineData(5, 4)]
    [InlineData(4, 5)]
    [InlineData(0, 4)]
    public void Rent_RejectsOddOrEmptyDimensions(int w, int h) =>
        Assert.Throws<ArgumentException>(() => RawFrame.Rent(w, h, "d"));

    [Fact]
    public void CopyFrom_HonoursSourceStrideAndCropsToEven()
    {
        // 5x3 source (odd), stride padded to 28 bytes; crop to 4x2.
        const int srcW = 5, srcH = 3, srcStride = 28;
        var src = new byte[srcStride * srcH];
        for (int y = 0; y < srcH; y++)
            for (int x = 0; x < srcW; x++)
                for (int c = 0; c < 4; c++)
                    src[y * srcStride + x * 4 + c] = (byte)(y * 40 + x * 4 + c);
        // Padding bytes must never leak into the frame.
        for (int y = 0; y < srcH; y++)
            for (int i = srcW * 4; i < srcStride; i++) src[y * srcStride + i] = 0xEE;

        using var f = RawFrame.Rent(RawFrame.EvenFloor(srcW), RawFrame.EvenFloor(srcH), "d");
        f.CopyFrom(src, srcStride);

        Assert.Equal(16, f.Stride);
        var px = f.Pixels;
        for (int y = 0; y < f.Height; y++)
            for (int x = 0; x < f.Width; x++)
                for (int c = 0; c < 4; c++)
                    Assert.Equal((byte)(y * 40 + x * 4 + c), px[y * f.Stride + x * 4 + c]);
    }

    [Fact]
    public void CopyFrom_ForceOpaque_SetsAlpha()
    {
        var src = new byte[2 * 2 * 4]; // all zero, alpha 0
        using var f = RawFrame.Rent(2, 2, "d");
        f.CopyFrom(src, 8, forceOpaque: true);
        var px = f.Pixels;
        for (int i = 0; i < px.Length; i += 4)
        {
            Assert.Equal(0, px[i]);
            Assert.Equal(0xFF, px[i + 3]);
        }
    }

    [Fact]
    public void Dispose_IsIdempotent_AndPixelsThrowAfter()
    {
        var f = RawFrame.Rent(2, 2, "d");
        f.Dispose();
        f.Dispose();
        Assert.Throws<ObjectDisposedException>(() => f.Pixels.Length);
    }

    [Fact]
    public void Timestamps_AreMonotonic()
    {
        using var a = RawFrame.Rent(2, 2, "d");
        using var b = RawFrame.Rent(2, 2, "d");
        Assert.True(b.Timestamp >= a.Timestamp);
    }
}
