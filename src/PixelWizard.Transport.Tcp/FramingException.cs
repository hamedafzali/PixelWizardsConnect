using System;

namespace PixelWizard.Transport.Tcp
{
    /// <summary>
    /// The outer 4-byte length prefix was zero, negative, or otherwise unusable. This means
    /// stream sync is lost — there is no valid frame boundary to recover from — so it is
    /// unrecoverable and the connection must close. Kept distinct from an unrecognized
    /// <c>MessageType</c> byte (which is a defined, recoverable case: the frame is well-formed
    /// and gets skipped) so callers never have to guess which failure mode they are seeing.
    /// </summary>
    public sealed class FramingException : Exception
    {
        public int DeclaredLength { get; }

        public FramingException(int declaredLength, string message) : base(message)
        {
            DeclaredLength = declaredLength;
        }
    }
}
