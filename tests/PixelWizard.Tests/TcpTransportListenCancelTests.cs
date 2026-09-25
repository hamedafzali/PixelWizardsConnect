using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>
/// Disconnect()/Dispose() must cancel a StartServerAsync still waiting to accept. Before
/// this fix the TcpListener was a local nobody could stop: a stopped host kept accepting,
/// and a later viewer reached the consent dialog on a host the user had already stopped.
/// </summary>
public class TcpTransportListenCancelTests
{
    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task AssertCompletesAsync(Task t, string what)
    {
        if (await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(5))) != t)
            throw new TimeoutException($"Timed out waiting for: {what}");
        await t;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectOrDispose_WhileAccepting_StopsListening_NoConnectedNoError(bool dispose)
    {
        int port = GetFreePort();
        var t = new TcpTransport();
        bool connected = false;
        Exception? error = null;
        t.Connected += () => connected = true;
        t.Error += ex => error = ex;

        var listening = t.StartServerAsync(port, useTls: false);
        await Task.Delay(50);
        if (dispose) t.Dispose(); else t.Disconnect();

        await AssertCompletesAsync(listening, "StartServerAsync to return after the stop");
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port));
        Assert.False(connected);
        Assert.Null(error);
        Assert.False(t.IsConnected);
    }

    [Fact]
    public async Task PortIsReusable_AfterCancellingAPendingAccept()
    {
        int port = GetFreePort();
        var first = new TcpTransport();
        var listening = first.StartServerAsync(port, useTls: false);
        await Task.Delay(50);
        first.Disconnect();
        await AssertCompletesAsync(listening, "first listen to stop");

        using var second = new TcpTransport();
        var connected = new TaskCompletionSource<bool>();
        second.Connected += () => connected.TrySetResult(true);
        _ = second.StartServerAsync(port, useTls: false);
        await Task.Delay(50);
        using var client = new TcpTransport();
        await client.ConnectAsync("127.0.0.1", port, useTls: false);

        await AssertCompletesAsync(connected.Task, "second listener to accept");
    }
}
