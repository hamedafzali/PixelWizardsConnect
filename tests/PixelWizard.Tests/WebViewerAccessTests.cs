using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using PixelWizard.Transport.WebSocket;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>
/// The browser viewer streams the host's screen with no consent of its own, so it must only
/// be reachable from the host machine's own viewer page: not from another machine, and not
/// from a foreign website open in the host's browser (WebSockets ignore same-origin).
/// </summary>
public class WebViewerAccessTests
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> TryOpenStreamAsync(string host, int port, string? origin)
    {
        using var ws = new ClientWebSocket();
        if (origin != null) ws.Options.SetRequestHeader("Origin", origin);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await ws.ConnectAsync(new Uri($"ws://{host}:{port}/stream"), cts.Token);
            return ws.State == WebSocketState.Open;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            return false;
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("http://localhost:9001", true)]
    [InlineData("http://LOCALHOST:9001", true)]
    [InlineData("http://127.0.0.1:9001", true)]
    [InlineData("http://localhost:9002", false)]
    [InlineData("https://localhost:9001", false)]
    [InlineData("http://evil.example", false)]
    [InlineData("http://evil.example:9001", false)]
    [InlineData("http://localhost.evil.example:9001", false)]
    [InlineData("null", false)]
    public void IsAllowedOrigin(string? origin, bool allowed) =>
        Assert.Equal(allowed, WebSocketHostServer.IsAllowedOrigin(origin, 9001));

    [Fact]
    public async Task Stream_FromOwnPage_IsAccepted()
    {
        int port = GetFreePort();
        using var server = new WebSocketHostServer(port);
        server.Start();

        Assert.True(await TryOpenStreamAsync("localhost", port, $"http://localhost:{port}"));
    }

    [Fact]
    public async Task Stream_FromForeignWebsite_IsRefused()
    {
        int port = GetFreePort();
        using var server = new WebSocketHostServer(port);
        server.Start();

        Assert.False(await TryOpenStreamAsync("localhost", port, "http://evil.example"));
    }

    [Fact]
    public async Task Stream_FromAnotherMachine_IsRefused()
    {
        // Stand-in for another machine: this machine's own non-loopback address. A runner
        // without one has nothing to check.
        var lanIp = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (lanIp == null) return;

        int port = GetFreePort();
        using var server = new WebSocketHostServer(port);
        server.Start();

        Assert.False(await TryOpenStreamAsync(lanIp.ToString(), port, origin: null));
    }
}
