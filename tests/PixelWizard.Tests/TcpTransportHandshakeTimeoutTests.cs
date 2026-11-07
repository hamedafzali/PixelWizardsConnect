using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.Tests
{
    /// <summary>
    /// A TLS viewer against a host that accepts but never answers the ClientHello (a host
    /// listening in plaintext does exactly this) used to hang in ConnectAsync forever.
    /// </summary>
    public class TcpTransportHandshakeTimeoutTests : IDisposable
    {
        private readonly string _pinStorePath =
            Path.Combine(Path.GetTempPath(), $"pixelwizard-pins-{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            if (File.Exists(_pinStorePath)) File.Delete(_pinStorePath);
        }

        [Fact]
        public async Task SilentHost_ConnectTimesOut_WithErrorThenDisconnected()
        {
            var silent = new TcpListener(IPAddress.Loopback, 0);
            silent.Start();
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var accepting = silent.AcceptTcpClientAsync();
            try
            {
                var client = new TcpTransport(new CertificatePinStore(_pinStorePath))
                {
                    HandshakeTimeout = TimeSpan.FromMilliseconds(300)
                };
                Exception? error = null;
                bool disconnectedAfterError = false;
                client.Error += ex => error = ex;
                client.Disconnected += () => disconnectedAfterError = error != null;

                var sw = Stopwatch.StartNew();
                var connecting = client.ConnectAsync("127.0.0.1", port, useTls: true);
                var finished = await Task.WhenAny(connecting, Task.Delay(TimeSpan.FromSeconds(10)));

                Assert.Same(connecting, finished);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
                Assert.IsType<TlsHandshakeTimeoutException>(error);
                Assert.True(disconnectedAfterError);
                Assert.False(client.IsConnected);
            }
            finally
            {
                silent.Stop();
                try { (await accepting).Dispose(); } catch { }
            }
        }
    }
}
