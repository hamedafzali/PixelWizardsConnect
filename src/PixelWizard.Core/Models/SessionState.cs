namespace PixelWizard.Core.Models
{
    public enum SessionRole { None, Host, Viewer }

    public enum SessionStatus { Idle, Connecting, Connected, Disconnected }

    public class SessionState
    {
        public SessionRole Role { get; set; } = SessionRole.None;
        public SessionStatus Status { get; set; } = SessionStatus.Idle;
        public string? ConnectionCode { get; set; }
        public string? RemoteEndpoint { get; set; }
        public StreamingSettings Streaming { get; set; } = StreamingSettings.Balanced;

        public bool IsHost => Role == SessionRole.Host;
        public bool IsViewer => Role == SessionRole.Viewer;
        public bool IsActive => Status == SessionStatus.Connected;
    }
}
