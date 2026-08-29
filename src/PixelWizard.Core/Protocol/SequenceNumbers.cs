namespace PixelWizard.Core.Protocol
{
    /// <summary>
    /// Wraparound-safe comparison for StreamFrameMessage.SequenceNumber. A plain subtraction
    /// misreports the gap the instant a stream's counter wraps past uint.MaxValue back to 0;
    /// unchecked 32-bit subtraction gives the correct signed delta across that boundary as long
    /// as the true gap is under ~2^31, which no realistic drop count approaches.
    /// </summary>
    public static class SequenceNumbers
    {
        /// <summary>Signed difference (to - from), correct across a uint wraparound.</summary>
        public static int Difference(uint from, uint to) => unchecked((int)(to - from));

        /// <summary>Number of sequence numbers dropped between an expected-next value and what
        /// actually arrived (0 if none, positive if frames were skipped, negative if the
        /// message arrived out of order/before its expected slot).</summary>
        public static int Gap(uint expectedNext, uint actual) => Difference(expectedNext, actual);
    }
}
