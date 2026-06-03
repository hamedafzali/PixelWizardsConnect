using PixelWizard.Transport;
using Xunit;

namespace PixelWizard.Tests;

public class RouterUrlTests
{
    [Theory]
    [InlineData("localhost", 9000, "http://localhost:9000")]
    [InlineData("router.example.com", 9000, "http://router.example.com:9000")]
    [InlineData("https://router.example.com", 443, "https://router.example.com")]
    [InlineData("https://router.example.com", 9000, "https://router.example.com:9000")]
    [InlineData("http://router.example.com", 80, "http://router.example.com")]
    [InlineData("https://router.example.com:9000", 9000, "https://router.example.com:9000")]
    public void BaseUrl_BuildsExpectedUrl(string host, int port, string expected)
    {
        Assert.Equal(expected, RouterHttpClient.BaseUrl(host, port));
    }
}
