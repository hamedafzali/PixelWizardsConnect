using System;
using System.IO;

namespace PixelWizard.Core.Protocol
{
    public class NetworkMessage
    {
        public MessageType Type { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long Timestamp { get; set; } = DateTime.UtcNow.Ticks;

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write((byte)Type);
            writer.Write(Timestamp);
            writer.Write(Data.Length);
            writer.Write(Data);
            return ms.ToArray();
        }

        public static NetworkMessage Deserialize(byte[] buffer)
        {
            using var ms = new MemoryStream(buffer);
            using var reader = new BinaryReader(ms);
            var type = (MessageType)reader.ReadByte();
            long timestamp = reader.ReadInt64();
            int length = reader.ReadInt32();

            // BinaryReader.ReadBytes silently returns fewer bytes than requested when the
            // stream runs out early instead of throwing, so a forged/corrupt length field
            // would otherwise produce a message with truncated data and no error. Reject
            // that case explicitly rather than letting it misparse.
            long remaining = ms.Length - ms.Position;
            if (length < 0 || length > remaining)
                throw new InvalidDataException(
                    $"NetworkMessage declared payload length {length}, but only {remaining} byte(s) remain in the buffer.");

            return new NetworkMessage
            {
                Type = type,
                Timestamp = timestamp,
                Data = reader.ReadBytes(length)
            };
        }
    }
}
