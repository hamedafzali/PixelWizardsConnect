using System;

namespace PixelWizard.Transport.Tcp
{
    /// <summary>
    /// The TCP connection was made but the host did not complete the TLS handshake in time.
    /// The usual cause is a host listening in plaintext, which never answers the ClientHello.
    /// Kept distinct from a socket timeout because the fix is different: the address is
    /// right, the TLS setting on one side is not.
    /// </summary>
    public sealed class TlsHandshakeTimeoutException : TimeoutException
    {
        public TlsHandshakeTimeoutException(string message) : base(message) { }
    }
}
