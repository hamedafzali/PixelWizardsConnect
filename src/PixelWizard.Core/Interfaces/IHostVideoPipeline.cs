using System;

namespace PixelWizard.Core.Interfaces
{
    /// <summary>
    /// The host's capture-encode-send chain for the H.264 track. Created and started only by
    /// <c>HostSession.Approve()</c> (PHASE4-PLAN D4): before the host has approved a viewer no capture runs,
    /// no encoder exists and nothing is written to the track.
    /// </summary>
    public interface IHostVideoPipeline : IDisposable
    {
        /// <summary>Opens the capture and encoder and starts sending. False, with the reason, when it cannot (the session then stays on JPEG).</summary>
        bool TryStart(out string reason);

        /// <summary>True from a successful <see cref="TryStart"/> until it stops, whether by <see cref="Stop"/> or on its own after an error.</summary>
        bool IsRunning { get; }

        /// <summary>Stops sending and releases the encoder. Idempotent.</summary>
        void Stop();
    }
}
