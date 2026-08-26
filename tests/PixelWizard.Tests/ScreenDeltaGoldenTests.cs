using System.IO;
using PixelWizard.Core.Protocol;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>
/// Golden-file round-trip tests for ScreenDelta.Serialize/Deserialize, mirroring
/// NetworkMessageGoldenTests. ScreenDelta has its own independent length-prefixed
/// ImageData field (see T1b audit), so it needs the same fixture + malformed-length
/// coverage rather than inheriting NetworkMessage's.
/// </summary>
public class ScreenDeltaGoldenTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ScreenDelta");

    private static byte[] LoadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureDir, name + ".bin"));

    [Fact]
    public void Basic_RoundTrips_AndMatchesGoldenBytes()
    {
        byte[] golden = LoadFixture("Basic");

        var restored = ScreenDelta.Deserialize(golden);
        Assert.Equal(17, restored.X);
        Assert.Equal(42, restored.Y);
        Assert.Equal(800, restored.Width);
        Assert.Equal(600, restored.Height);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (byte)i), restored.ImageData);

        // Re-serializing must reproduce the exact fixture bytes.
        Assert.Equal(golden, restored.Serialize());
    }

    [Fact]
    public void EmptyImage_RoundTrips_AndMatchesGoldenBytes()
    {
        byte[] golden = LoadFixture("EmptyImage");

        var restored = ScreenDelta.Deserialize(golden);
        Assert.Equal(0, restored.X);
        Assert.Equal(0, restored.Y);
        Assert.Equal(0, restored.Width);
        Assert.Equal(0, restored.Height);
        Assert.NotNull(restored.ImageData);
        Assert.Empty(restored.ImageData);
        Assert.Equal(golden, restored.Serialize());
    }

    [Fact]
    public void Deserialize_ImageLengthFieldLiesLonger_ThrowsInsteadOfTruncatingSilently()
    {
        // Declares a 5000-byte image but only supplies 6 bytes.
        byte[] malformed = BuildFrame(1, 2, 3, 4, declaredLength: 5000, actualData: new byte[] { 1, 2, 3, 4, 5, 6 });

        Assert.Throws<InvalidDataException>(() => ScreenDelta.Deserialize(malformed));
    }

    [Fact]
    public void Deserialize_ImageLengthFieldNegative_Throws()
    {
        byte[] malformed = BuildFrame(0, 0, 0, 0, declaredLength: -1, actualData: Array.Empty<byte>());

        Assert.ThrowsAny<Exception>(() => ScreenDelta.Deserialize(malformed));
    }

    [Fact]
    public void Deserialize_TruncatedFrame_MissingImageLengthAndData_Throws()
    {
        // Only X, Y, Width — Height, length, and data are all missing.
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(1); w.Write(2); w.Write(3);
        }

        Assert.ThrowsAny<Exception>(() => ScreenDelta.Deserialize(ms.ToArray()));
    }

    [Fact]
    public void Deserialize_EmptyBuffer_Throws()
    {
        Assert.ThrowsAny<Exception>(() => ScreenDelta.Deserialize(Array.Empty<byte>()));
    }

    private static byte[] BuildFrame(int x, int y, int w, int h, int declaredLength, byte[] actualData)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(x);
        writer.Write(y);
        writer.Write(w);
        writer.Write(h);
        writer.Write(declaredLength);
        writer.Write(actualData);
        return ms.ToArray();
    }
}
