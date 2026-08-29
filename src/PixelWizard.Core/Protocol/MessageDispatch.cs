using System;

namespace PixelWizard.Core.Protocol
{
    /// <summary>
    /// What a viewer does with a message received from the host, once Hello/Handshake are
    /// complete. Pure classification of <see cref="MessageType"/> -- it is a property of the
    /// type, not of any connection state, exactly like <see cref="MessageTypeExtensions.GetPlane"/>.
    /// This exists so the dispatch decision in a session/view-model can be unit tested without a
    /// socket or a UI dispatcher: the classification is exhaustively covered, and the side
    /// effects that follow each category are exercised separately (build/run + manual/smoke
    /// verification), not blended into the same untestable switch.
    /// </summary>
    public enum ViewerDispatchAction
    {
        ApplyFullScreen,
        ApplyScreenDelta,
        HostHelloAck,
        HostHelloRejected,
        HandshakeAcknowledged,
        HandshakeRejected,
        LatencyPong,
        Clipboard,
        Chat,
        Ignored
    }

    /// <summary>
    /// What a host does with a message received from a viewer, once Hello/Handshake are
    /// complete. Same rationale as <see cref="ViewerDispatchAction"/>.
    /// </summary>
    public enum HostDispatchAction
    {
        MouseMove,
        MouseClick,
        MouseButtonDown,
        MouseButtonUp,
        KeyPress,
        KeyRelease,
        PingReply,
        QualityChanged,
        Clipboard,
        Chat,
        Ignored
    }

    public static class MessageDispatch
    {
        public static ViewerDispatchAction ClassifyForViewer(MessageType type) => type switch
        {
            MessageType.FullScreen => ViewerDispatchAction.ApplyFullScreen,
            MessageType.ScreenDelta => ViewerDispatchAction.ApplyScreenDelta,
            MessageType.HelloAck => ViewerDispatchAction.HostHelloAck,
            MessageType.HelloRejected => ViewerDispatchAction.HostHelloRejected,
            MessageType.HandshakeOk => ViewerDispatchAction.HandshakeAcknowledged,
            MessageType.HandshakeFailed => ViewerDispatchAction.HandshakeRejected,
            MessageType.Pong => ViewerDispatchAction.LatencyPong,
            MessageType.ClipboardText => ViewerDispatchAction.Clipboard,
            MessageType.ChatMessage => ViewerDispatchAction.Chat,
            _ => ViewerDispatchAction.Ignored
        };

        public static HostDispatchAction ClassifyForHost(MessageType type) => type switch
        {
            MessageType.MouseMove => HostDispatchAction.MouseMove,
            MessageType.MouseClick => HostDispatchAction.MouseClick,
            MessageType.MouseButtonDown => HostDispatchAction.MouseButtonDown,
            MessageType.MouseButtonUp => HostDispatchAction.MouseButtonUp,
            MessageType.KeyPress => HostDispatchAction.KeyPress,
            MessageType.KeyRelease => HostDispatchAction.KeyRelease,
            MessageType.Ping => HostDispatchAction.PingReply,
            MessageType.QualityPreset => HostDispatchAction.QualityChanged,
            MessageType.ClipboardText => HostDispatchAction.Clipboard,
            MessageType.ChatMessage => HostDispatchAction.Chat,
            _ => HostDispatchAction.Ignored
        };
    }
}
