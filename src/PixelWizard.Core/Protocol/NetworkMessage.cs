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
            return new NetworkMessage
            {
                Type = (MessageType)reader.ReadByte(),
                Timestamp = reader.ReadInt64(),
                Data = reader.ReadBytes(reader.ReadInt32())
            };
        }
    }
}
