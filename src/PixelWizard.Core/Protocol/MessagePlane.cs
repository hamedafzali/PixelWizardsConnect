using System;

namespace PixelWizard.Core.Protocol
{
    /// <summary>
    /// Reliable/ordered vs droppable. In Phase 1 both planes still run over the same TCP
    /// connection -- this only models the distinction so Phase 4 can route Media to a WebRTC
    /// track while Control stays reliable, without a wire-format change at that point.
    /// </summary>
    public enum MessagePlane
    {
        Control,
        Media
    }

    /// <summary>
    /// The plane is a property of the message type, not a runtime decision -- this is a pure
    /// classification, not per-connection state.
    /// </summary>
    public static class MessageTypeExtensions
    {
        public static MessagePlane GetPlane(this MessageType type) => type switch
        {
            MessageType.ScreenDelta or MessageType.FullScreen or MessageType.StreamFrame => MessagePlane.Media,
            _ => MessagePlane.Control
        };
    }
}
