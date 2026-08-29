using System;
using System.IO;
using System.Linq;
using PixelWizard.Protocol;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>
/// Golden-file round-trip tests for StreamFrameMessage.Serialize/Deserialize, mirroring
/// ScreenDeltaGoldenTests -- StreamFrame is a new v2 message type with its own independent
/// length-prefixed ImageData field, so it needs the same fixture + malformed-length coverage.
/// </summary>
public class StreamFrameGoldenTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "StreamFrame");

    private static byte[] LoadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureDir, name + ".bin"));

    [Fact]
    public void Basic_RoundTrips_AndMatchesGoldenBytes()
    {
        byte[] golden = LoadFixture("Basic");

        var restored = StreamFrameMessage.Deserialize(golden);
        Assert.Equal(3, restored.StreamId);
        Assert.Equal(StreamKind.Screen, restored.Kind);
        Assert.Equal(1000u, restored.SequenceNumber);
        Assert.Equal(638500000000000000L, restored.CaptureTimestampTicks);
        Assert.Equal(10, restored.X);
        Assert.Equal(20, restored.Y);
        Assert.Equal(1920, restored.Width);
        Assert.Equal(1080, restored.Height);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (byte)i), restored.ImageData);

        Assert.Equal(golden, restored.Serialize());
    }

    [Fact]
    public void EmptyImage_RoundTrips_AndMatchesGoldenBytes()
    {
        byte[] golden = LoadFixture("EmptyImage");

        var restored = StreamFrameMessage.Deserialize(golden);
        Assert.Equal(0, restored.StreamId);
        Assert.Equal(StreamKind.Camera, restored.Kind);
        Assert.Equal(0u, restored.SequenceNumber);
        Assert.NotNull(restored.ImageData);
        Assert.Empty(restored.ImageData);
        Assert.Equal(golden, restored.Serialize());
    }

    [Fact]
    public void Deserialize_ImageLengthFieldLiesLonger_ThrowsInsteadOfTruncatingSilently()
    {
        byte[] malformed = BuildFrame(1, (byte)StreamKind.Screen, 1, 0, 0, 0, 0, 0,
            declaredLength: 5000, actualData: new byte[] { 1, 2, 3, 4, 5, 6 });

        Assert.Throws<InvalidDataException>(() => StreamFrameMessage.Deserialize(malformed));
    }

    [Fact]
    public void Deserialize_ImageLengthFieldNegative_Throws()
    {
        byte[] malformed = BuildFrame(1, (byte)StreamKind.Screen, 1, 0, 0, 0, 0, 0,
            declaredLength: -1, actualData: Array.Empty<byte>());

        Assert.ThrowsAny<Exception>(() => StreamFrameMessage.Deserialize(malformed));
    }

    [Fact]
    public void Deserialize_TruncatedFrame_MissingRegionAndData_Throws()
    {
        // StreamId, Kind, SequenceNumber, Timestamp present -- region/length/data all missing.
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)1);
            w.Write((byte)StreamKind.Screen);
            w.Write(1u);
            w.Write(0L);
        }

        Assert.ThrowsAny<Exception>(() => StreamFrameMessage.Deserialize(ms.ToArray()));
    }

    [Fact]
    public void Deserialize_EmptyBuffer_Throws()
    {
        Assert.ThrowsAny<Exception>(() => StreamFrameMessage.Deserialize(Array.Empty<byte>()));
    }

    [Fact]
    public void SequenceGap_DetectsDrops_AndIsSafeAcrossWraparound()
    {
        Assert.Equal(0, SequenceNumbers.Gap(expectedNext: 5, actual: 5));
        Assert.Equal(3, SequenceNumbers.Gap(expectedNext: 5, actual: 8)); // 3 frames dropped
        Assert.Equal(-2, SequenceNumbers.Gap(expectedNext: 8, actual: 6)); // arrived early/out of order

        // uint.MaxValue -> 0 is a normal one-step advance, not a huge apparent gap.
        Assert.Equal(1, SequenceNumbers.Gap(expectedNext: uint.MaxValue, actual: 0));
    }

    private static byte[] BuildFrame(byte streamId, byte kind, uint seq, long timestampTicks,
        int x, int y, int w, int h, int declaredLength, byte[] actualData)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(streamId);
        writer.Write(kind);
        writer.Write(seq);
        writer.Write(timestampTicks);
        writer.Write(x);
        writer.Write(y);
        writer.Write(w);
        writer.Write(h);
        writer.Write(declaredLength);
        writer.Write(actualData);
        return ms.ToArray();
    }
}
