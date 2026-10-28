using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.Tests
{
    /// <summary>
    /// The host's self-signed certificate is created on first TLS listen. Listeners that start
    /// together (parallel tests on a fresh CI runner, or a relisten racing the first) must all
    /// end up with the one certificate the file holds -- otherwise a host presents a key no
    /// later listen will, and on Windows the losing writer fails outright.
    /// </summary>
    public class TransportCertificateTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pixelwizard-cert-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        [Fact]
        public async Task ConcurrentFirstUse_AllCallersGetTheCertificateTheFileHolds()
        {
            string path = Path.Combine(_dir, "transport.pfx");
            const int callers = 8;
            using var gate = new Barrier(callers);

            var results = await Task.WhenAll(Enumerable.Range(0, callers).Select(_ => Task.Run(() =>
            {
                gate.SignalAndWait();
                using var cert = TcpTransport.GetOrCreateSelfSignedCert(path);
                return cert.Thumbprint;
            })));

            using var onDisk = TcpTransport.GetOrCreateSelfSignedCert(path);
            Assert.All(results, t => Assert.Equal(onDisk.Thumbprint, t));
        }
    }
}
