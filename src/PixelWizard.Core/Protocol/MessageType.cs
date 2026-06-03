namespace PixelWizard.Core.Protocol
{
    public enum MessageType : byte
    {
        // Signaling
        RegisterHost = 1,
        HostRegistered = 2,
        ConnectToHost = 3,
        HostConnected = 4,
        ConnectionFailed = 5,
        Disconnect = 6,

        // Screen
        ScreenDelta = 10,
        FullScreen = 11,
        ScreenSize = 12,

        // Input
        MouseMove = 20,
        MouseClick = 21,
        KeyPress = 22,
        KeyRelease = 23,

        // Health
        Ping = 30,
        Pong = 31,
        QualityPreset = 32
    }
}
