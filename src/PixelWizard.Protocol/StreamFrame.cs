using System;
using System.IO;

namespace PixelWizard.Protocol
{
    /// <summary>
    /// What a StreamFrame's pixels represent. Distinct streams (e.g. a screen share plus a
    /// camera overlay) share the wire but are told apart by StreamId, not this -- Kind is
    /// metadata a viewer uses to decide how to composite/label a stream, not an identity.
    /// </summary>
    public enum StreamKind : byte
    {
        Screen = 1,
        Camera = 2,
        Overlay = 3
    }

    /// <summary>
    /// A v2 payload carrying one video/image frame from a specific, identified stream. This is
    /// a new MessageType (StreamFrame) rather than a change to ScreenDelta/FullScreen, so v1
    /// golden fixtures for those types stay untouched -- nothing in Phase 1 produces a second
    /// real stream yet (that's Phase 4's WebRTC track wiring), this only makes one
    /// representable and testable now.
    ///
    /// Field-width reasoning:
    /// - StreamId is a byte (max 256 concurrent streams per peer). A screen-share/camera/
    ///   overlay combination realistically needs single digits; 256 is generous headroom, not
    ///   a hard product requirement.
    /// - SequenceNumber is a uint (4 bytes), scoped to one stream's lifetime (a reconnect or
    ///   stream restart resets it to 0 -- it does not persist across StreamId re-use). At a
    ///   generous 60fps that wraps only after ~2.3 years of continuous, uninterrupted capture,
    ///   which no real session approaches; a ushort would wrap in just over an hour at modest
    ///   15fps, which a real support session can plausibly exceed, so the extra 2 bytes over a
    ///   ushort buys real headroom for negligible cost against JPEG-sized payloads. Comparisons
    ///   still use wraparound-safe arithmetic (see SequenceNumbers.Difference) as defense in
    ///   depth even though the wrap case is not expected to be reached in practice.
    /// - CaptureTimestampTicks is a long, using .NET's DateTime.Ticks epoch (0001-01-01 UTC,
    ///   100ns resolution) -- the same convention NetworkMessage.Timestamp already uses, so the
    ///   wire protocol has one timestamp convention, not two. The extra bytes versus a 4-byte
    ///   Unix-seconds/ms alternative are negligible next to frame payload size.
    ///
    /// Region fields (X/Y/Width/Height) are always present, unlike v1's dual-shape convention
    /// where FullScreen carries raw bytes and ScreenDelta wraps a region -- StreamFrame instead
    /// always states its region explicitly (X=0,Y=0,Width/Height=full dimensions for a
    /// full-frame update), so there is one shape to parse, not two.
    /// </summary>
    public class StreamFrameMessage
    {
        public byte StreamId { get; set; }
        public StreamKind Kind { get; set; }
        public uint SequenceNumber { get; set; }
        public long CaptureTimestampTicks { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] ImageData { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(StreamId);
            writer.Write((byte)Kind);
            writer.Write(SequenceNumber);
            writer.Write(CaptureTimestampTicks);
            writer.Write(X);
            writer.Write(Y);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write(ImageData.Length);
            writer.Write(ImageData);
            return ms.ToArray();
        }

        public static StreamFrameMessage Deserialize(byte[] buffer)
        {
            using var ms = new MemoryStream(buffer);
            using var reader = new BinaryReader(ms);
            byte streamId = reader.ReadByte();
            var kind = (StreamKind)reader.ReadByte();
            uint sequenceNumber = reader.ReadUInt32();
            long captureTimestampTicks = reader.ReadInt64();
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            int length = reader.ReadInt32();

            // See NetworkMessage.Deserialize: reject a forged/corrupt length explicitly rather
            // than letting BinaryReader.ReadBytes silently truncate.
            long remaining = ms.Length - ms.Position;
            if (length < 0 || length > remaining)
                throw new InvalidDataException(
                    $"StreamFrame declared image length {length}, but only {remaining} byte(s) remain in the buffer.");

            return new StreamFrameMessage
            {
                StreamId = streamId,
                Kind = kind,
                SequenceNumber = sequenceNumber,
                CaptureTimestampTicks = captureTimestampTicks,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                ImageData = reader.ReadBytes(length)
            };
        }
    }
}
