using System.IO;

namespace PixelWizard.Protocol
{
    public class MouseMoveMessage
    {
        public int X { get; set; }
        public int Y { get; set; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(X); w.Write(Y);
            return ms.ToArray();
        }

        public static MouseMoveMessage Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new MouseMoveMessage { X = r.ReadInt32(), Y = r.ReadInt32() };
        }
    }

    public class MouseClickMessage
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool LeftButton { get; set; }
        public bool RightButton { get; set; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(X); w.Write(Y); w.Write(LeftButton); w.Write(RightButton);
            return ms.ToArray();
        }

        public static MouseClickMessage Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new MouseClickMessage
            {
                X = r.ReadInt32(),
                Y = r.ReadInt32(),
                LeftButton = r.ReadBoolean(),
                RightButton = r.ReadBoolean()
            };
        }
    }

    public class KeyMessage
    {
        public int VirtualKey { get; set; }
        public bool IsKeyDown { get; set; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(VirtualKey); w.Write(IsKeyDown);
            return ms.ToArray();
        }

        public static KeyMessage Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            return new KeyMessage { VirtualKey = r.ReadInt32(), IsKeyDown = r.ReadBoolean() };
        }
    }
}
