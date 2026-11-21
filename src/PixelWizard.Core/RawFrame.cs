using System;
using System.Buffers;
using System.Diagnostics;

namespace PixelWizard.Core
{
    /// <summary>
    /// One captured frame of 32-bit BGRA pixels (byte order B,G,R,A in memory), top row first.
    ///
    /// Ownership: the pixel buffer is rented from <see cref="ArrayPool{T}.Shared"/>, so a 30 fps
    /// stream does not allocate an 8 MB array per frame. The frame owns the buffer until
    /// <see cref="Dispose"/>, which returns it; after that <see cref="Pixels"/> throws. The
    /// consumer (the encoder) disposes each frame once it has copied or encoded it. Dispose is
    /// idempotent. A frame is not thread-safe: hand it to one consumer.
    ///
    /// Width and height are even (see <see cref="EvenFloor"/>) because H.264 yuv420p/nv12 needs
    /// even dimensions; the capture crops one pixel row/column off an odd-sized display.
    /// </summary>
    public sealed class RawFrame : IDisposable
    {
        private byte[]? _buffer;

        public int Width { get; }
        public int Height { get; }
        /// <summary>Bytes per row. At least Width * 4; may be larger.</summary>
        public int Stride { get; }
        /// <summary>Identity of the source display, see <see cref="PixelWizard.Core.Interfaces.IRawScreenCapture.DisplayId"/>.</summary>
        public string DisplayId { get; }
        /// <summary>Monotonic capture time (Stopwatch based); only differences are meaningful.</summary>
        public TimeSpan Timestamp { get; }

        private RawFrame(int width, int height, int stride, string displayId, TimeSpan timestamp, byte[] buffer)
        {
            Width = width; Height = height; Stride = stride; DisplayId = displayId; Timestamp = timestamp;
            _buffer = buffer;
        }

        /// <summary>Exactly Stride * Height bytes. Throws ObjectDisposedException after Dispose.</summary>
        public Span<byte> Pixels
        {
            get
            {
                var b = _buffer ?? throw new ObjectDisposedException(nameof(RawFrame));
                return b.AsSpan(0, Stride * Height);
            }
        }

        /// <summary>
        /// Rents a frame of the given even size, tightly packed (Stride = Width * 4). Contents are
        /// undefined until the caller fills <see cref="Pixels"/>.
        /// </summary>
        public static RawFrame Rent(int width, int height, string displayId)
        {
            if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentException($"Raw frame dimensions must be positive and even, got {width}x{height}.");
            int stride = checked(width * 4);
            var buf = ArrayPool<byte>.Shared.Rent(checked(stride * height));
            return new RawFrame(width, height, stride, displayId,
                TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency), buf);
        }

        /// <summary>Rounds down to the nearest even number (odd display sizes lose one pixel row/column).</summary>
        public static int EvenFloor(int value) => value & ~1;

        /// <summary>
        /// Copies a BGRA source with an arbitrary stride into this frame's tightly packed buffer,
        /// cropping to this frame's size from the top-left. The source span must cover (Height - 1) * srcStride + Width * 4 bytes.
        /// With <paramref name="forceOpaque"/> the alpha byte is set to 255 (GDI leaves it 0).
        /// </summary>
        public void CopyFrom(ReadOnlySpan<byte> src, int srcStride, bool forceOpaque = false)
        {
            var dst = Pixels;
            int rowBytes = Width * 4;
            for (int y = 0; y < Height; y++)
            {
                var srcRow = src.Slice(y * srcStride, rowBytes);
                var dstRow = dst.Slice(y * Stride, rowBytes);
                srcRow.CopyTo(dstRow);
                if (forceOpaque)
                    for (int x = 3; x < rowBytes; x += 4) dstRow[x] = 0xFF;
            }
        }

        public void Dispose()
        {
            var b = _buffer;
            _buffer = null;
            if (b != null) ArrayPool<byte>.Shared.Return(b);
        }
    }
}
