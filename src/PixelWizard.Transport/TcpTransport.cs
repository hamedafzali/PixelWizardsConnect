using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using PixelWizard.Core.Interfaces;
using PixelWizard.Core.Protocol;

namespace PixelWizard.Transport
{
    public class TcpTransport : ISessionTransport
    {
        private TcpClient? _tcpClient;
        private Stream? _stream;
        private CancellationTokenSource? _cts;
        private bool _isConnected;
        private readonly CertificatePinStore _pinStore;

        internal static readonly CertificatePinStore SharedPinStore = new();

        public TcpTransport() : this(SharedPinStore) { }

        internal TcpTransport(CertificatePinStore pinStore)
        {
            _pinStore = pinStore;
        }

        /// <summary>
        /// Forgets a previously pinned certificate fingerprint for host:port, so the next
        /// connection re-pins instead of refusing. Needed because legitimate hosts reinstall
        /// and regenerate certificates — without this the product bricks itself against them.
        /// </summary>
        public static bool ForgetPin(string host, int port) => SharedPinStore.Forget($"{host}:{port}");

        public event Action<NetworkMessage>? MessageReceived;
        public event Action? Connected;
        public event Action? Disconnected;
        public event Action<Exception>? Error;
        public event Action<int>? BytesReceived;
        public event Action<int>? BytesSent;

        /// <summary>
        /// A frame with valid framing and length arrived with a <see cref="MessageType"/> byte
        /// this build doesn't recognize (e.g. a newer peer). The payload is discarded and the
        /// session continues — this is the mechanism that lets peers upgrade independently
        /// instead of every version needing to match exactly.
        /// </summary>
        public event Action<byte>? UnknownMessageTypeSkipped;

        public bool IsConnected => _isConnected && _tcpClient?.Connected == true;

        public async Task ConnectAsync(string host, int port, bool useTls = true)
        {
            try
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(host, port);
                Stream stream = _tcpClient.GetStream();

                if (useTls)
                {
                    string pinKey = $"{host}:{port}";
                    Exception? pinningFailure = null;

                    var ssl = new SslStream(stream, false,
                        (sender, cert, chain, errors) => ValidatePin(pinKey, cert, out pinningFailure));

                    try
                    {
                        await ssl.AuthenticateAsClientAsync(host);
                    }
                    catch when (pinningFailure != null)
                    {
                        throw pinningFailure;
                    }

                    stream = ssl;
                }

                Activate(stream);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
                Disconnect();
            }
        }

        public async Task StartServerAsync(int port, bool useTls = true)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _tcpClient = await listener.AcceptTcpClientAsync();
                listener.Stop();

                Stream stream = _tcpClient.GetStream();

                if (useTls)
                {
                    var cert = GetOrCreateSelfSignedCert();
                    var ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsServerAsync(cert, false, false);
                    stream = ssl;
                }

                Activate(stream);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
                Disconnect();
            }
        }

        public async Task SendMessageAsync(NetworkMessage message)
        {
            if (!IsConnected || _stream == null)
                return;

            try
            {
                byte[] data = message.Serialize();
                byte[] len = BitConverter.GetBytes(data.Length);
                await _stream.WriteAsync(len, 0, 4);
                await _stream.WriteAsync(data, 0, data.Length);
                await _stream.FlushAsync();
                BytesSent?.Invoke(4 + data.Length);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
                Disconnect();
            }
        }

        public void Disconnect()
        {
            _isConnected = false;
            _cts?.Cancel();
            _stream?.Close();
            _tcpClient?.Close();
            Disconnected?.Invoke();
        }

        public void Dispose()
        {
            Disconnect();
            _cts?.Dispose();
            _tcpClient?.Dispose();
            _stream?.Dispose();
        }

        private void Activate(Stream stream)
        {
            _stream = stream;
            _isConnected = true;
            _cts = new CancellationTokenSource();
            Connected?.Invoke();
            Task.Run(() => ReceiveLoop(_cts.Token));
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            byte[] lenBuf = new byte[4];
            while (!token.IsCancellationRequested && IsConnected)
            {
                try
                {
                    if (!await ReadExact(lenBuf, 4, token)) break;
                    int len = BitConverter.ToInt32(lenBuf, 0);
                    if (len <= 0)
                    {
                        Error?.Invoke(new FramingException(len,
                            $"Outer frame length was {len}; stream sync is lost, disconnecting."));
                        break;
                    }

                    byte[] msgBuf = new byte[len];
                    if (!await ReadExact(msgBuf, len, token)) break;

                    BytesReceived?.Invoke(4 + len);
                    var message = NetworkMessage.Deserialize(msgBuf);

                    if (!Enum.IsDefined(typeof(MessageType), message.Type))
                    {
                        UnknownMessageTypeSkipped?.Invoke((byte)message.Type);
                        continue;
                    }

                    MessageReceived?.Invoke(message);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Error?.Invoke(ex); break; }
            }
            Disconnect();
        }

        private async Task<bool> ReadExact(byte[] buf, int length, CancellationToken token)
        {
            if (_stream == null) return false;
            int total = 0;
            while (total < length)
            {
                int read = await _stream.ReadAsync(buf, total, length - total, token);
                if (read == 0) return false;
                total += read;
            }
            return true;
        }

        // Trust-on-first-use: pin the leaf cert's SHA-256 fingerprint on first connection to a
        // given host:port, then require an exact match on every later connection. Chain/name
        // errors from the (self-signed) server cert are deliberately ignored — the fingerprint
        // comparison against the pin store is the only trust decision made here.
        private bool ValidatePin(string key, X509Certificate? certificate, out Exception? failure)
        {
            failure = null;

            if (certificate == null)
            {
                failure = new CertificateMissingException(key, $"No certificate was presented by '{key}'.");
                return false;
            }

            string fingerprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

            string? pinned;
            try
            {
                pinned = _pinStore.TryGetPin(key);
            }
            catch (CertificatePinStoreCorruptedException ex)
            {
                failure = ex;
                return false;
            }

            if (pinned == null)
            {
                _pinStore.Pin(key, fingerprint);
                return true;
            }

            if (!string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                failure = new CertificatePinMismatchException(key, pinned, fingerprint,
                    $"Certificate fingerprint for '{key}' does not match the pinned value. " +
                    "This may indicate a MITM attack, or the host legitimately reinstalled/regenerated " +
                    $"its certificate — use {nameof(TcpTransport)}.{nameof(ForgetPin)} to re-trust it.");
                return false;
            }

            return true;
        }

        private static readonly string CertPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PixelWizardConnect", "transport.pfx");

        private static readonly string CertPassword = "pixelwizard-transport";

        private static X509Certificate2 GetOrCreateSelfSignedCert()
        {
            if (File.Exists(CertPath))
            {
                try { return X509CertificateLoader.LoadPkcs12FromFile(CertPath, CertPassword, X509KeyStorageFlags.Exportable); }
                catch { /* regenerate */ }
            }

            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=PixelWizardTransport", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            Directory.CreateDirectory(Path.GetDirectoryName(CertPath)!);
            File.WriteAllBytes(CertPath, cert.Export(X509ContentType.Pfx, CertPassword));
            return X509CertificateLoader.LoadPkcs12FromFile(CertPath, CertPassword, X509KeyStorageFlags.Exportable);
        }
    }
}
