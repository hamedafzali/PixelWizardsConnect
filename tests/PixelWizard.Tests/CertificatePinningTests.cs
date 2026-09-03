using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.Tests
{
    /// <summary>
    /// End-to-end tests of TcpTransport's TOFU certificate pinning, using a real TLS
    /// handshake over loopback (server side reuses the self-signed cert TcpTransport
    /// already caches at Environment.SpecialFolder.ApplicationData/PixelWizardConnect/,
    /// so its fingerprint is stable across connections within and across test runs).
    /// </summary>
    public class CertificatePinningTests : IDisposable
    {
        private readonly string _pinStorePath;
        private readonly CertificatePinStore _pinStore;

        public CertificatePinningTests()
        {
            _pinStorePath = Path.Combine(Path.GetTempPath(), $"pixelwizard-pins-{Guid.NewGuid():N}.json");
            _pinStore = new CertificatePinStore(_pinStorePath);
        }

        public void Dispose()
        {
            if (File.Exists(_pinStorePath)) File.Delete(_pinStorePath);
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task<TcpTransport> StartServerAsync(int port)
        {
            var server = new TcpTransport();
            var ready = server.StartServerAsync(port, useTls: true);
            await Task.Delay(50); // give the listener a moment to bind before the client connects
            return server;
        }

        [Fact]
        public async Task UnknownKey_PinsAndConnects()
        {
            int port = GetFreePort();
            using var server = await StartServerAsync(port);
            var client = new TcpTransport(_pinStore);
            string key = $"127.0.0.1:{port}";

            await client.ConnectAsync("127.0.0.1", port);

            Assert.True(client.IsConnected);
            Assert.NotNull(_pinStore.TryGetPin(key));
            client.Disconnect();
        }

        [Fact]
        public async Task KnownKey_MatchingFingerprint_Connects()
        {
            // StartServerAsync's listener accepts exactly one client then stops, so a
            // second connection to the same host:port key needs a fresh server instance
            // rebound to the same port (the same cached self-signed cert is reused, so
            // the fingerprint — and therefore the key's pin match — is unaffected).
            int port = GetFreePort();
            using (var server = await StartServerAsync(port))
            {
                var first = new TcpTransport(_pinStore);
                await first.ConnectAsync("127.0.0.1", port);
                Assert.True(first.IsConnected);
                first.Disconnect();
            }

            using var server2 = await StartServerAsync(port);
            var second = new TcpTransport(_pinStore);
            await second.ConnectAsync("127.0.0.1", port);

            Assert.True(second.IsConnected);
            second.Disconnect();
        }

        [Fact]
        public async Task KnownKey_DifferentFingerprint_RefusedWithDistinctException()
        {
            int port = GetFreePort();
            using var server = await StartServerAsync(port);
            string key = $"127.0.0.1:{port}";

            // Seed a bogus pin so the real cert presented is guaranteed to mismatch.
            _pinStore.Pin(key, new string('f', 64));

            var client = new TcpTransport(_pinStore);
            Exception? captured = null;
            client.Error += ex => captured = ex;

            await client.ConnectAsync("127.0.0.1", port);

            Assert.False(client.IsConnected);
            var mismatch = Assert.IsType<CertificatePinMismatchException>(captured);
            Assert.Equal(key, mismatch.Key);
            Assert.Equal(new string('f', 64), mismatch.ExpectedFingerprint);
        }

        [Fact]
        public async Task Forget_ThenReconnect_RePins()
        {
            int port = GetFreePort();
            string key = $"127.0.0.1:{port}";
            string? pinnedBefore;

            using (var server = await StartServerAsync(port))
            {
                var first = new TcpTransport(_pinStore);
                await first.ConnectAsync("127.0.0.1", port);
                Assert.True(first.IsConnected);
                first.Disconnect();

                pinnedBefore = _pinStore.TryGetPin(key);
                Assert.NotNull(pinnedBefore);
            }

            Assert.True(_pinStore.Forget(key));
            Assert.Null(_pinStore.TryGetPin(key));

            using var server2 = await StartServerAsync(port);
            var second = new TcpTransport(_pinStore);
            await second.ConnectAsync("127.0.0.1", port);

            Assert.True(second.IsConnected);
            Assert.Equal(pinnedBefore, _pinStore.TryGetPin(key));
            second.Disconnect();
        }

        [Fact]
        public async Task Fingerprint_IsStableAcrossReconnects()
        {
            int portA = GetFreePort();
            using var serverA = await StartServerAsync(portA);
            var clientA = new TcpTransport(_pinStore);
            await clientA.ConnectAsync("127.0.0.1", portA);
            Assert.True(clientA.IsConnected);
            var fingerprintA = _pinStore.TryGetPin($"127.0.0.1:{portA}");
            clientA.Disconnect();

            int portB = GetFreePort();
            using var serverB = await StartServerAsync(portB);
            var clientB = new TcpTransport(_pinStore);
            await clientB.ConnectAsync("127.0.0.1", portB);
            Assert.True(clientB.IsConnected);
            var fingerprintB = _pinStore.TryGetPin($"127.0.0.1:{portB}");
            clientB.Disconnect();

            // Both servers reuse the SAME cached self-signed cert (TcpTransport's
            // GetOrCreateSelfSignedCert persists it under ApplicationData), so the
            // leaf-certificate fingerprint must be identical across both connections.
            Assert.Equal(fingerprintA, fingerprintB);
        }

        [Fact]
        public async Task CorruptedStore_RefusesConnectionInsteadOfAcceptingAll()
        {
            File.WriteAllText(_pinStorePath, "not json");
            int port = GetFreePort();
            using var server = await StartServerAsync(port);

            var client = new TcpTransport(_pinStore);
            Exception? captured = null;
            client.Error += ex => captured = ex;

            await client.ConnectAsync("127.0.0.1", port);

            Assert.False(client.IsConnected);
            Assert.IsType<CertificatePinStoreCorruptedException>(captured);
        }
    }
}
