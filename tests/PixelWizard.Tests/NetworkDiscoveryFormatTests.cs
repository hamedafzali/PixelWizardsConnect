using PixelWizard.Transport;
using Xunit;

namespace PixelWizard.Tests;

public class NetworkDiscoveryFormatTests
{
    [Fact]
    public void Parse_ValidAnnouncement_ReturnsRemoteIpAndPort()
    {
        var result = NetworkDiscovery.Parse("PIXELWIZARD|MyPC|8888", "192.168.1.5");

        Assert.NotNull(result);
        Assert.Equal("192.168.1.5", result!.Value.ip);
        Assert.Equal("8888", result.Value.port);
    }

    [Fact]
    public void Parse_GarbageMessage_ReturnsNull()
    {
        Assert.Null(NetworkDiscovery.Parse("garbage", "1.2.3.4"));
    }

    [Fact]
    public void Parse_MissingPortField_ReturnsNull()
    {
        Assert.Null(NetworkDiscovery.Parse("PIXELWIZARD|onlytwo", "1.2.3.4"));
    }
}
