using System;

namespace PixelWizard.Transport.Tcp
{
    /// <summary>
    /// The certificate presented by a previously-pinned host does not match the fingerprint
    /// recorded at first connection. Distinct from a generic transport failure on purpose:
    /// this means "possible MITM, or the host's cert legitimately changed" — the opposite of
    /// "host is offline" — and callers must be able to tell the two apart.
    /// </summary>
    public sealed class CertificatePinMismatchException : Exception
    {
        public string Key { get; }
        public string ExpectedFingerprint { get; }
        public string ActualFingerprint { get; }

        public CertificatePinMismatchException(string key, string expectedFingerprint, string actualFingerprint, string message)
            : base(message)
        {
            Key = key;
            ExpectedFingerprint = expectedFingerprint;
            ActualFingerprint = actualFingerprint;
        }
    }

    /// <summary>
    /// No certificate was presented during the TLS handshake, so there is nothing to pin
    /// against or compare. Kept distinct from CertificatePinMismatchException because there
    /// is no "expected vs actual" fingerprint pair to report here.
    /// </summary>
    public sealed class CertificateMissingException : Exception
    {
        public string Key { get; }

        public CertificateMissingException(string key, string message) : base(message)
        {
            Key = key;
        }
    }

    /// <summary>
    /// The pin store exists but could not be read as valid data (empty, malformed JSON,
    /// truncated, or an unrecognised schema/fingerprint format). Connections must refuse
    /// rather than silently treating this as "no pins recorded" — that would let a
    /// corrupted-on-disk store quietly reopen trust for a host that was already pinned.
    /// </summary>
    public sealed class CertificatePinStoreCorruptedException : Exception
    {
        public CertificatePinStoreCorruptedException(string message) : base(message) { }
        public CertificatePinStoreCorruptedException(string message, Exception inner) : base(message, inner) { }
    }
}
