using System;
using System.IO;
using System.Threading.Tasks;
using PixelWizard.Transport;
using Xunit;

namespace PixelWizard.Tests
{
    public class CertificatePinStoreTests : IDisposable
    {
        private readonly string _path;

        public CertificatePinStoreTests()
        {
            _path = Path.Combine(Path.GetTempPath(), $"pixelwizard-pins-{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }

        [Fact]
        public void MissingFile_IsTreatedAsEmptyPinSet()
        {
            var store = new CertificatePinStore(_path);

            Assert.Null(store.TryGetPin("host:1"));
        }

        [Fact]
        public void PinThenGet_RoundTripsAcrossInstances()
        {
            var fingerprint = new string('a', 64);
            new CertificatePinStore(_path).Pin("host:1", fingerprint);

            var fromNewInstance = new CertificatePinStore(_path).TryGetPin("host:1");

            Assert.Equal(fingerprint, fromNewInstance);
        }

        [Fact]
        public void EmptyFile_FailsClosed()
        {
            File.WriteAllText(_path, "");
            var store = new CertificatePinStore(_path);

            Assert.Throws<CertificatePinStoreCorruptedException>(() => store.TryGetPin("host:1"));
        }

        [Fact]
        public void MalformedJson_FailsClosed()
        {
            File.WriteAllText(_path, "{ this is not json");
            var store = new CertificatePinStore(_path);

            Assert.Throws<CertificatePinStoreCorruptedException>(() => store.TryGetPin("host:1"));
        }

        [Fact]
        public void TruncatedFile_FailsClosed()
        {
            var fingerprint = new string('b', 64);
            var goodPath = _path;
            new CertificatePinStore(goodPath).Pin("host:1", fingerprint);

            var full = File.ReadAllText(goodPath);
            File.WriteAllText(goodPath, full.Substring(0, full.Length / 2));

            var store = new CertificatePinStore(goodPath);
            Assert.Throws<CertificatePinStoreCorruptedException>(() => store.TryGetPin("host:1"));
        }

        [Fact]
        public void MalformedFingerprint_FailsClosed()
        {
            File.WriteAllText(_path, "{\"version\":1,\"pins\":{\"host:1\":\"not-a-fingerprint\"}}");
            var store = new CertificatePinStore(_path);

            Assert.Throws<CertificatePinStoreCorruptedException>(() => store.TryGetPin("host:1"));
        }

        [Fact]
        public void Forget_RemovesPin_SoNextLookupIsUnknown()
        {
            var store = new CertificatePinStore(_path);
            store.Pin("host:1", new string('c', 64));

            var removed = store.Forget("host:1");

            Assert.True(removed);
            Assert.Null(store.TryGetPin("host:1"));
        }

        [Fact]
        public void Forget_UnknownKey_ReturnsFalseAndDoesNotThrow()
        {
            var store = new CertificatePinStore(_path);

            Assert.False(store.Forget("nope:1"));
        }

        [Fact]
        public async Task ConcurrentPinsToDifferentKeys_DoNotCorruptTheStore()
        {
            var store = new CertificatePinStore(_path);
            var tasks = new Task[50];

            for (int i = 0; i < tasks.Length; i++)
            {
                int captured = i;
                tasks[captured] = Task.Run(() => store.Pin($"host{captured}:1", captured.ToString("x2").PadRight(64, '0')));
            }

            await Task.WhenAll(tasks);

            var verify = new CertificatePinStore(_path);
            for (int i = 0; i < tasks.Length; i++)
            {
                var expected = i.ToString("x2").PadRight(64, '0');
                Assert.Equal(expected, verify.TryGetPin($"host{i}:1"));
            }
        }
    }
}
