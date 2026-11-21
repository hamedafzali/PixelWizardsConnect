namespace PixelWizard.Media.H264;

/// <summary>One H.264 access unit in Annex-B form (start codes), with in-band SPS/PPS on keyframes.</summary>
public sealed record EncodedAccessUnit(byte[] AnnexB, bool IsKeyframe, long Pts);

/// <summary>A decoded picture as tightly owned BGRA bytes (top row first).</summary>
public sealed record DecodedFrame(byte[] Bgra, int Width, int Height, int Stride);
