using System;
using System.Text;
using System.Threading.Tasks;
using PixelWizard.Protocol;
using PixelWizard.Session;
using Xunit;

namespace PixelWizard.Tests.Session;

/// <summary>
/// T9.4: the typed senders on ViewerSession/HostSession must put exactly the bytes on the
/// wire that MainViewModel's inline NetworkMessage construction did -- each test decodes
/// what reached the transport with the same Deserialize the receiving side uses.
/// </summary>
public class SessionSendTests
{
    private static readonly HelloMessage TestHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };

    private static (ViewerSession, FakeSessionTransport) Viewer()
    {
        var fake = new FakeSessionTransport();
        return (new ViewerSession(() => fake, TestHello), fake);
    }

    private static (HostSession, FakeSessionTransport) Host()
    {
        var fake = new FakeSessionTransport();
        return (new HostSession(fake, TestHello), fake);
    }

    [Fact]
    public async Task MouseMove()
    {
        var (s, t) = Viewer();
        await s.SendMouseMoveAsync(12, 34);
        Assert.Equal(MessageType.MouseMove, t.LastSentMessage!.Type);
        var m = MouseMoveMessage.Deserialize(t.LastSentMessage.Data);
        Assert.Equal((12, 34), (m.X, m.Y));
    }

    public static TheoryData<string, MessageType> ClickKinds => new()
    {
        { "click", MessageType.MouseClick },
        { "down", MessageType.MouseButtonDown },
        { "up", MessageType.MouseButtonUp },
    };

    [Theory]
    [MemberData(nameof(ClickKinds))]
    public async Task MouseButtons_LeftAndRight(string kind, MessageType expected)
    {
        foreach (bool left in new[] { true, false })
        {
            var (s, t) = Viewer();
            await (kind switch
            {
                "click" => s.SendMouseClickAsync(5, 6, left),
                "down" => s.SendMouseDownAsync(5, 6, left),
                _ => s.SendMouseUpAsync(5, 6, left),
            });
            Assert.Equal(expected, t.LastSentMessage!.Type);
            var m = MouseClickMessage.Deserialize(t.LastSentMessage.Data);
            Assert.Equal((5, 6, left, !left), (m.X, m.Y, m.LeftButton, m.RightButton));
        }
    }

    [Theory]
    [InlineData(true, MessageType.KeyPress)]
    [InlineData(false, MessageType.KeyRelease)]
    public async Task Key(bool isDown, MessageType expected)
    {
        var (s, t) = Viewer();
        await s.SendKeyAsync(0x41, isDown);
        Assert.Equal(expected, t.LastSentMessage!.Type);
        var k = KeyMessage.Deserialize(t.LastSentMessage.Data);
        Assert.Equal((0x41, isDown), (k.VirtualKey, k.IsKeyDown));
    }

    [Fact]
    public async Task QualityPreset()
    {
        var (s, t) = Viewer();
        await s.SendQualityPresetAsync(3);
        Assert.Equal(MessageType.QualityPreset, t.LastSentMessage!.Type);
        Assert.Equal(3, BitConverter.ToInt32(t.LastSentMessage.Data, 0));
    }

    [Fact]
    public async Task Ping_CarriesCurrentUtcTicks()
    {
        var (s, t) = Viewer();
        long before = DateTime.UtcNow.Ticks;
        await s.SendPingAsync();
        long after = DateTime.UtcNow.Ticks;
        Assert.Equal(MessageType.Ping, t.LastSentMessage!.Type);
        long ticks = BitConverter.ToInt64(t.LastSentMessage.Data, 0);
        Assert.InRange(ticks, before, after);
    }

    [Fact]
    public async Task Ping_EchoedAsPong_ProducesLatency()
    {
        var (s, t) = Viewer();
        int? latency = null;
        s.LatencyMeasured += ms => latency = ms;
        await s.SendPingAsync();
        t.RaiseMessageReceived(new NetworkMessage { Type = MessageType.Pong, Data = t.LastSentMessage!.Data });
        Assert.NotNull(latency);
        Assert.InRange(latency!.Value, 0, 5_000);
    }

    [Fact]
    public async Task Viewer_ClipboardAndChat_Utf8()
    {
        var (s, t) = Viewer();
        await s.SendClipboardAsync("clip é");
        Assert.Equal(MessageType.ClipboardText, t.LastSentMessage!.Type);
        Assert.Equal("clip é", Encoding.UTF8.GetString(t.LastSentMessage.Data));
        await s.SendChatAsync("hi ✓");
        Assert.Equal(MessageType.ChatMessage, t.LastSentMessage!.Type);
        Assert.Equal("hi ✓", Encoding.UTF8.GetString(t.LastSentMessage.Data));
    }

    [Fact]
    public async Task Host_ClipboardAndChat_Utf8()
    {
        var (s, t) = Host();
        await s.SendClipboardAsync("clip é");
        Assert.Equal(MessageType.ClipboardText, t.LastSentMessage!.Type);
        Assert.Equal("clip é", Encoding.UTF8.GetString(t.LastSentMessage.Data));
        await s.SendChatAsync("hi ✓");
        Assert.Equal(MessageType.ChatMessage, t.LastSentMessage!.Type);
        Assert.Equal("hi ✓", Encoding.UTF8.GetString(t.LastSentMessage.Data));
    }

    [Fact]
    public async Task Host_FullFrame_SendsRawImageBytes()
    {
        var (s, t) = Host();
        var delta = new ScreenDelta { X = 0, Y = 0, Width = 2, Height = 2, ImageData = new byte[] { 1, 2, 3 } };
        await s.SendFrameAsync(delta, full: true);
        Assert.Equal(MessageType.FullScreen, t.LastSentMessage!.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, t.LastSentMessage.Data);
    }

    [Fact]
    public async Task Host_Delta_SendsSerializedDelta()
    {
        var (s, t) = Host();
        var delta = new ScreenDelta { X = 7, Y = 8, Width = 2, Height = 3, ImageData = new byte[] { 9, 9 } };
        await s.SendFrameAsync(delta, full: false);
        Assert.Equal(MessageType.ScreenDelta, t.LastSentMessage!.Type);
        var back = ScreenDelta.Deserialize(t.LastSentMessage.Data);
        Assert.Equal((7, 8, 2, 3), (back.X, back.Y, back.Width, back.Height));
        Assert.Equal(new byte[] { 9, 9 }, back.ImageData);
    }
}
