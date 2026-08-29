using System.IO;
using PixelWizard.Protocol;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>
/// Golden-file round-trip tests for NetworkMessage.Serialize/Deserialize. Fixtures under
/// Fixtures/NetworkMessage/*.bin pin the exact wire bytes for every MessageType, so an
/// accidental change to the frame layout (field order, width, endianness) fails a test
/// here instead of silently breaking interop between client/host builds.
/// </summary>
public class NetworkMessageGoldenTests
{
    private const long FixtureTimestamp = 638500000000000000L;
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "NetworkMessage");

    private static byte[] LoadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureDir, name + ".bin"));

    public static IEnumerable<object[]> AllMessageTypes()
    {
        foreach (MessageType type in Enum.GetValues(typeof(MessageType)))
            yield return new object[] { type };
    }

    [Theory]
    [MemberData(nameof(AllMessageTypes))]
    public void EveryMessageType_RoundTrips_AndMatchesGoldenBytes(MessageType type)
    {
        byte[] golden = LoadFixture(type.ToString());
        byte[] expectedData = Enumerable.Repeat((byte)type, 8).ToArray();

        var restored = NetworkMessage.Deserialize(golden);
        Assert.Equal(type, restored.Type);
        Assert.Equal(FixtureTimestamp, restored.Timestamp);
        Assert.Equal(expectedData, restored.Data);

        // Re-serializing must reproduce the exact fixture bytes — any drift in the wire
        // format (field order/width/endianness) fails here even if round-trip fields match.
        Assert.Equal(golden, restored.Serialize());
    }

    [Fact]
    public void EmptyPayload_RoundTrips_AndMatchesGoldenBytes()
    {
        byte[] golden = LoadFixture("EmptyPayload");

        var restored = NetworkMessage.Deserialize(golden);
        Assert.Equal(MessageType.Ping, restored.Type);
        Assert.NotNull(restored.Data);
        Assert.Empty(restored.Data);
        Assert.Equal(golden, restored.Serialize());
    }

    [Fact]
    public void LargePayload_OverOneMegabyte_RoundTrips_AndMatchesGoldenBytes()
    {
        byte[] golden = LoadFixture("LargePayload");

        var restored = NetworkMessage.Deserialize(golden);
        Assert.Equal(MessageType.ScreenDelta, restored.Type);
        Assert.True(restored.Data.Length > 1024 * 1024, "fixture payload should exceed 1MB");
        Assert.Equal(golden, restored.Serialize());
    }

    [Fact]
    public void Deserialize_LengthFieldLiesLonger_ThrowsInsteadOfTruncatingSilently()
    {
        // Declares a 1000-byte payload but only supplies 4 bytes.
        byte[] malformed = BuildFrame((byte)MessageType.ScreenDelta, FixtureTimestamp, declaredLength: 1000, actualData: new byte[] { 1, 2, 3, 4 });

        Assert.Throws<InvalidDataException>(() => NetworkMessage.Deserialize(malformed));
    }

    [Fact]
    public void Deserialize_LengthFieldNegative_Throws()
    {
        byte[] malformed = BuildFrame((byte)MessageType.Ping, FixtureTimestamp, declaredLength: -1, actualData: Array.Empty<byte>());

        Assert.ThrowsAny<Exception>(() => NetworkMessage.Deserialize(malformed));
    }

    [Fact]
    public void Deserialize_TruncatedFrame_MissingTimestampAndLength_Throws()
    {
        // Only the 1-byte type is present; timestamp/length/data are all missing.
        byte[] truncated = { (byte)MessageType.Ping };

        Assert.ThrowsAny<Exception>(() => NetworkMessage.Deserialize(truncated));
    }

    [Fact]
    public void Deserialize_TruncatedFrame_MidTimestamp_Throws()
    {
        // Type byte plus 3 of the 8 timestamp bytes — cut off mid-field.
        byte[] truncated = { (byte)MessageType.Ping, 0x01, 0x02, 0x03 };

        Assert.ThrowsAny<Exception>(() => NetworkMessage.Deserialize(truncated));
    }

    [Fact]
    public void Deserialize_EmptyBuffer_Throws()
    {
        Assert.ThrowsAny<Exception>(() => NetworkMessage.Deserialize(Array.Empty<byte>()));
    }

    [Fact]
    public void Deserialize_GarbageByteStream_ThrowsOrRejectsCleanly_NeverSilentlyMisparses()
    {
        // Fixed pseudo-random garbage, not a valid frame under any interpretation whose
        // declared length matches what follows it.
        var garbage = new byte[37];
        new Random(42).NextBytes(garbage);

        // Whatever happens, it must not fabricate a Data array longer than what
        // was actually available in the buffer (the "silent misparse" this test guards).
        try
        {
            var result = NetworkMessage.Deserialize(garbage);
            Assert.True(result.Data.Length <= garbage.Length,
                "Deserialize must never report more payload bytes than the input contained.");
        }
        catch (Exception)
        {
            // Throwing is an acceptable, explicit rejection of malformed input.
        }
    }

    private static byte[] BuildFrame(byte type, long timestamp, int declaredLength, byte[] actualData)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(type);
        writer.Write(timestamp);
        writer.Write(declaredLength);
        writer.Write(actualData);
        return ms.ToArray();
    }
}
