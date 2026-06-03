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
            return new ScreenDelta
            {
                X = reader.ReadInt32(),
                Y = reader.ReadInt32(),
                Width = reader.ReadInt32(),
                Height = reader.ReadInt32(),
                ImageData = reader.ReadBytes(reader.ReadInt32())
            };
        }
    }
}
