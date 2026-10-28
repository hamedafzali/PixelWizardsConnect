using PixelWizard.Session;
using Xunit;

namespace PixelWizard.Tests.Session;

public class DirectAddressTests
{
    [Theory]
    [InlineData("192.168.1.10", "192.168.1.10", 8888)]
    [InlineData("  192.168.1.10  ", "192.168.1.10", 8888)]
    [InlineData("192.168.1.10:9999", "192.168.1.10", 9999)]
    [InlineData("my-pc.local:7000", "my-pc.local", 7000)]
    [InlineData("[::1]:9999", "::1", 9999)]
    [InlineData("[fe80::1]", "fe80::1", 8888)]
    [InlineData("fe80::1", "fe80::1", 8888)]
    public void Valid(string input, string host, int port)
    {
        Assert.True(DirectAddress.TryParse(input, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(":8888")]
    [InlineData("192.168.1.10:")]
    [InlineData("192.168.1.10:abc")]
    [InlineData("192.168.1.10:0")]
    [InlineData("192.168.1.10:65536")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    public void Invalid(string? input)
    {
        Assert.False(DirectAddress.TryParse(input, out _, out _));
    }
}
