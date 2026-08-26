using System;
using System.IO;

namespace PixelWizard.Core.Protocol
{
    public class ScreenDelta
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] ImageData { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(X);
            writer.Write(Y);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write(ImageData.Length);
            writer.Write(ImageData);
            return ms.ToArray();
        }

        public static ScreenDelta Deserialize(byte[] buffer)
        {
            using var ms = new MemoryStream(buffer);
            using var reader = new BinaryReader(ms);
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            int length = reader.ReadInt32();

            // See NetworkMessage.Deserialize: BinaryReader.ReadBytes silently returns fewer
            // bytes than requested when the stream runs out early instead of throwing, so a
            // forged/corrupt length field would otherwise produce a truncated ImageData with
            // no error. Reject that case explicitly rather than letting it misparse.
            long remaining = ms.Length - ms.Position;
            if (length < 0 || length > remaining)
                throw new InvalidDataException(
                    $"ScreenDelta declared image length {length}, but only {remaining} byte(s) remain in the buffer.");

            return new ScreenDelta
            {
                X = x,
                Y = y,
                Width = width,
                Height = height,
                ImageData = reader.ReadBytes(length)
            };
        }
    }
}
