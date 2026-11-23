using System;

namespace PixelWizard.Core.Interfaces
{
    /// <summary>
    /// Optional capability of a transport that carries an H.264 video track next to its message channels
    /// (the WebRTC transport; TCP never does). A transport that does not implement it, or whose
    /// <see cref="VideoTrackNegotiated"/> is false, keeps media on the JPEG message path.
    /// </summary>
    public interface IVideoTrackTransport
    {
        /// <summary>True when the SDP exchange agreed an H.264 video track with the peer. Meaningful once connected.</summary>
        bool VideoTrackNegotiated { get; }

        /// <summary>
        /// Sends one Annex-B access unit on the track. <paramref name="durationRtpUnits"/> is the time since the
        /// previous access unit in 90 kHz RTP units. Dropped silently when the track is not negotiated or the
        /// transport is down.
        /// </summary>
        void SendVideoAccessUnit(byte[] annexB, uint durationRtpUnits);

        /// <summary>A complete access unit (Annex-B) arrived; raised on a transport thread, so handlers must not block.</summary>
        event Action<byte[], uint>? VideoAccessUnitReceived;

        /// <summary>Viewer side: asks the sender for a keyframe (RTCP PLI).</summary>
        void RequestKeyframe();

        /// <summary>Host side: the viewer asked for a keyframe (RTCP PLI or FIR).</summary>
        event Action? KeyframeRequested;
    }
}
