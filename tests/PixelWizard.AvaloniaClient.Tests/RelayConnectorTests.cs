using PixelWizard.AvaloniaClient.Services;
using PixelWizard.Signaling;
using Xunit;

namespace PixelWizard.AvaloniaClient.Tests;

/// <summary>The real WebRtcRelayConnector against the real router (skipped without PIXELWIZARD_ROUTER_EXE).</summary>
public class RelayConnectorTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [RouterFact]
    public async Task Host_WhoseSocketDropsBeforePairing_ResumesWithTheSameCode_AndPairs()
    {
        using var router = new RouterProcess();
        using var cts = new CancellationTokenSource(Limit);
        var connector = new WebRtcRelayConnector();
        var reg = await connector.RegisterHostAsync("http://127.0.0.1", router.Port, cts.Token);
        string code = reg.Code;
        var waiting = reg.WaitForViewerAsync(cts.Token);

        await ((WebRtcRelayConnector.HostRegistration)reg).DropSocketForTest();
        await Task.Delay(1500); // the first resume attempt waits 1 s
        Assert.False(waiting.IsCompleted);

        var viewerLink = await connector.JoinAsync("http://127.0.0.1", router.Port, code, cts.Token);
        var hostLink = await waiting.WaitAsync(Limit);
        Assert.Equal(code, reg.Code);

        await Task.WhenAll(hostLink.EstablishAsync(cts.Token), viewerLink.EstablishAsync(cts.Token)).WaitAsync(Limit);
        Assert.True(hostLink.Transport.IsConnected);
        Assert.True(viewerLink.Transport.IsConnected);
        hostLink.Transport.Dispose();
        viewerLink.Transport.Dispose();
        await reg.DisposeAsync();
    }

    [RouterFact]
    public async Task Join_WithAnUnknownCode_IsRefusedByTheRouter()
    {
        using var router = new RouterProcess();
        using var cts = new CancellationTokenSource(Limit);
        var ex = await Assert.ThrowsAsync<SignalingErrorException>(() =>
            new WebRtcRelayConnector().JoinAsync("http://127.0.0.1", router.Port, "ZZZZZZ", cts.Token));
        Assert.Equal("code-unknown", ex.Code);
    }
}
