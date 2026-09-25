using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.Tests.Session;

/// <summary>
/// T12: the pin-mismatch dialog in MainViewModel depends on this ordering over real TLS --
/// the mismatch reaches ViewerSession.Error intact, while the session is not connected,
/// before Disconnected -- and on forgetting the pin being enough for the next connect to
/// succeed. The dialog itself is UI and verified by hand.
/// </summary>
public class PinMismatchSessionTests : IDisposable
{
    private static readonly HelloMessage OurHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };

    private readonly string _pinPath = Path.Combine(Path.GetTempPath(), $"pixelwizard-pins-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_pinPath)) File.Delete(_pinPath);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<T> WaitAsync<T>(TaskCompletionSource<T> tcs, string what)
    {
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != tcs.Task)
            throw new TimeoutException($"Timed out waiting for: {what}");
        return await tcs.Task;
    }

    [Fact]
    public async Task Mismatch_RaisedOnErrorWhileNotConnected_BeforeDisconnected_ThenForgetAllowsReconnect()
    {
        int port = GetFreePort();
        string key = $"127.0.0.1:{port}";
        var pinStore = new CertificatePinStore(_pinPath);
        const string stalePin = "0000000000000000000000000000000000000000000000000000000000000000";
        pinStore.Pin(key, stalePin);

        var listener = new HostListener(() => new TcpTransport(), OurHello, port, useTls: () => true);
        var hostVerified = new TaskCompletionSource<bool>();
        listener.SessionCreated += h => h.HandshakeVerified += () => hostVerified.TrySetResult(true);
        _ = listener.StartAsync();
        await Task.Delay(50); // give the listener a moment to bind before connecting

        try
        {
            // First attempt: the stale pin must refuse the connection.
            var events = new List<string>();
            // Snapshot at the first drop: disposing the viewer raises Disconnected again
            // (TcpTransport raises it on every Disconnect call), which is not what's under test.
            var dropped = new TaskCompletionSource<string[]>();
            CertificatePinMismatchException? seen = null;
            bool connectedDuringError = true;
            using (var viewer = new ViewerSession(() => new TcpTransport(pinStore), OurHello))
            {
                viewer.Error += ex =>
                {
                    events.Add("Error");
                    seen = ex as CertificatePinMismatchException;
                    connectedDuringError = viewer.IsConnected;
                };
                viewer.Connected += () => events.Add("Connected");
                viewer.Disconnected += () => { events.Add("Disconnected"); dropped.TrySetResult(events.ToArray()); };

                await viewer.ConnectAsync("127.0.0.1", port, useTls: true);
                Assert.Equal(new[] { "Error", "Disconnected" },
                             await WaitAsync(dropped, "viewer to drop after the pin mismatch"));
            }

            Assert.NotNull(seen);
            Assert.False(connectedDuringError);
            Assert.Equal(key, seen!.Key);
            Assert.Equal(stalePin, seen.ExpectedFingerprint);
            Assert.NotEqual(stalePin, seen.ActualFingerprint, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(stalePin, pinStore.TryGetPin(key)); // refusing changed nothing

            // The refused attempt used up the listener's one accept; listen again for the retry
            // (MainViewModel does this from HostSession.Disconnected). Completes on accept.
            _ = listener.RelistenAsync();
            await Task.Delay(50);

            // Trust path: forget the pin, reconnect, handshake completes and the new cert is pinned.
            Assert.True(pinStore.Forget(key));
            using var retry = new ViewerSession(() => new TcpTransport(pinStore), OurHello);
            await retry.ConnectAsync("127.0.0.1", port, useTls: true);
            Assert.True(await WaitAsync(hostVerified, "host to verify the handshake after the pin was forgotten"));
            Assert.Equal(seen.ActualFingerprint, pinStore.TryGetPin(key), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }
}
