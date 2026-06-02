using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace ScreenAutomationApp
{
    // Message types for network communication
    public enum MessageType : byte
    {
        // Signaling messages
        RegisterHost = 1,
        HostRegistered = 2,
        ConnectToHost = 3,
        HostConnected = 4,
        ConnectionFailed = 5,
        Disconnect = 6,

        // Screen data messages
        ScreenDelta = 10,
        FullScreen = 11,
        ScreenSize = 12,

        // Input messages
        MouseMove = 20,
        MouseClick = 21,
        KeyPress = 22,
        KeyRelease = 23,

        // Quality/health messages
        Ping = 30,
        Pong = 31,
        QualityPreset = 32
    }

    // Base message structure
    [Serializable]
    public class NetworkMessage
    {
        public MessageType Type { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long Timestamp { get; set; } = DateTime.UtcNow.Ticks;

        public byte[] Serialize()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(ms);
                writer.Write((byte)Type);
                writer.Write(Timestamp);
                writer.Write(Data.Length);
                writer.Write(Data);
                return ms.ToArray();
            }
        }

        public static NetworkMessage Deserialize(byte[] buffer)
        {
            using (MemoryStream ms = new MemoryStream(buffer))
            {
                BinaryReader reader = new BinaryReader(ms);
                NetworkMessage msg = new NetworkMessage
                {
                    Type = (MessageType)reader.ReadByte(),
                    Timestamp = reader.ReadInt64(),
                    Data = reader.ReadBytes(reader.ReadInt32())
                };
                return msg;
            }
        }
    }

    // Screen delta for change detection
    [Serializable]
    public class ScreenDelta
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] ImageData { get; set; } = Array.Empty<byte>();

        public byte[] Serialize()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(ms);
                writer.Write(X);
                writer.Write(Y);
                writer.Write(Width);
                writer.Write(Height);
                writer.Write(ImageData.Length);
                writer.Write(ImageData);
                return ms.ToArray();
            }
        }

        public static ScreenDelta Deserialize(byte[] buffer)
        {
            using (MemoryStream ms = new MemoryStream(buffer))
            {
                BinaryReader reader = new BinaryReader(ms);
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

    // Signaling messages
    [Serializable]
    public class RegisterHostMessage
    {
        public string HostId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
    }

    [Serializable]
    public class HostRegisteredMessage
    {
        public string ConnectionCode { get; set; } = string.Empty;
        public string HostId { get; set; } = string.Empty;
    }

    [Serializable]
    public class ConnectToHostMessage
    {
        public string ConnectionCode { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
    }

    [Serializable]
    public class HostConnectedMessage
    {
        public string HostEndpoint { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
    }

    // Input messages
    [Serializable]
    public class MouseMoveMessage
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    [Serializable]
    public class MouseClickMessage
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool LeftButton { get; set; }
        public bool RightButton { get; set; }
    }

    [Serializable]
    public class KeyMessage
    {
        public int VirtualKey { get; set; }
        public bool IsKeyDown { get; set; }
    }
}
