using PixelWizard.Core.Protocol;
using Xunit;

namespace PixelWizard.Tests;

public class ProtocolTests
{
    [Fact]
    public void NetworkMessage_RoundTrips_TypeDataAndTimestamp()
    {
        var data = new byte[64];
        new Random(1234).NextBytes(data);
        var original = new NetworkMessage
        {
            Type = MessageType.ScreenDelta,
            Data = data,
            Timestamp = 1234567890123L
        };

        var restored = NetworkMessage.Deserialize(original.Serialize());

        Assert.Equal(MessageType.ScreenDelta, restored.Type);
        Assert.Equal(original.Timestamp, restored.Timestamp);
        Assert.Equal(data, restored.Data);
    }

    [Fact]
    public void NetworkMessage_EmptyData_RoundTripsToEmptyNotNull()
    {
        var original = new NetworkMessage
        {
            Type = MessageType.Ping,
            Data = Array.Empty<byte>()
        };

        var restored = NetworkMessage.Deserialize(original.Serialize());

        Assert.NotNull(restored.Data);
        Assert.Empty(restored.Data);
        Assert.Equal(MessageType.Ping, restored.Type);
    }

    [Fact]
    public void ScreenDelta_RoundTrips_AllFields()
    {
        var image = new byte[] { 1, 2, 3, 4, 5, 250, 251, 252 };
        var original = new ScreenDelta
        {
            X = 17,
            Y = 42,
            Width = 800,
            Height = 600,
            ImageData = image
        };

        var restored = ScreenDelta.Deserialize(original.Serialize());

        Assert.Equal(17, restored.X);
        Assert.Equal(42, restored.Y);
        Assert.Equal(800, restored.Width);
        Assert.Equal(600, restored.Height);
        Assert.Equal(image, restored.ImageData);
    }

    [Fact]
    public void MouseMoveMessage_RoundTrips_AllFields()
    {
        var original = new MouseMoveMessage { X = -3, Y = 1920 };

        var restored = MouseMoveMessage.Deserialize(original.Serialize());

        Assert.Equal(-3, restored.X);
        Assert.Equal(1920, restored.Y);
    }

    [Theory]
    [InlineData(10, 20, true, false)]
    [InlineData(0, 0, false, true)]
    [InlineData(5, 5, true, true)]
    [InlineData(7, 9, false, false)]
    public void MouseClickMessage_RoundTrips_AllFields(int x, int y, bool left, bool right)
    {
        var original = new MouseClickMessage
        {
            X = x,
            Y = y,
            LeftButton = left,
            RightButton = right
        };

        var restored = MouseClickMessage.Deserialize(original.Serialize());

        Assert.Equal(x, restored.X);
        Assert.Equal(y, restored.Y);
        Assert.Equal(left, restored.LeftButton);
        Assert.Equal(right, restored.RightButton);
    }

    [Theory]
    [InlineData(65, true)]
    [InlineData(13, false)]
    public void KeyMessage_RoundTrips_AllFields(int virtualKey, bool isKeyDown)
    {
        var original = new KeyMessage { VirtualKey = virtualKey, IsKeyDown = isKeyDown };

        var restored = KeyMessage.Deserialize(original.Serialize());

        Assert.Equal(virtualKey, restored.VirtualKey);
        Assert.Equal(isKeyDown, restored.IsKeyDown);
    }
}
