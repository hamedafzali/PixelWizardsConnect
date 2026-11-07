using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PixelWizard.Signaling;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>
/// A code the router doesn't know must read as a bad code, not as an unreachable router:
/// a mistyped O for 0 sent the user to check a router address that was fine.
/// </summary>
public class RouterConnectErrorTests
{
    private sealed class Reply : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public Reply(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8) });
    }

    private static Task<Core.Interfaces.RouterConnectResult> Resolve(HttpStatusCode status, string body) =>
        new RouterHttpClient(new Reply(status, body)).ResolveEndpointAsync("router", 9000, "ABC123");

    [Fact]
    public async Task UnknownCode_ThrowsCodeNotFound_WithTheRoutersMessage()
    {
        var ex = await Assert.ThrowsAsync<ConnectionCodeNotFoundException>(() => Resolve(HttpStatusCode.NotFound,
            "{\"hostEndpoint\":\"\",\"sessionSecret\":\"\",\"success\":false,\"message\":\"Invalid or expired connection code\"}"));
        Assert.Equal("Invalid or expired connection code", ex.Message);
    }

    [Theory]
    [InlineData("404 page not found")]                // not a router at that address
    [InlineData("{\"success\":true}")]
    [InlineData("[]")]
    public async Task OtherNotFound_StaysAnHttpError(string body)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => Resolve(HttpStatusCode.NotFound, body));
    }

    [Fact]
    public async Task RateLimited_StaysAnHttpError()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => Resolve(HttpStatusCode.TooManyRequests, "Too many requests"));
    }
}
