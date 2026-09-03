using System;
using System.Threading.Tasks;
using PixelWizard.Core.Interfaces;
using PixelWizard.Core.Models;
using PixelWizard.Protocol;
using Timer = System.Timers.Timer;

namespace PixelWizard.Media
{
    /// <summary>
    /// Drives an <see cref="IScreenCapture"/> on a timer at the cadence
    /// <see cref="StreamingSettings"/> specifies, and hands each captured delta to a caller-
    /// supplied async callback. Extracted from MainViewModel's StartCaptureTimer/
    /// CaptureTickAsync (Phase 2 T5) with the same behavior — no transport or UI knowledge:
    /// the caller decides what "connected" means and what to do with a delta (send over
    /// TcpTransport, broadcast to the WebSocket viewer, or both).
    ///
    /// Two settings reads are deliberately different in cadence, matching the original
    /// exactly: the timer's *interval* (FPS) is fixed for the lifetime of one <see cref="Start"/>
    /// call and is not recalculated if the settings change later, but JPEG quality is re-read
    /// from <c>settingsProvider</c> on every tick — so a mid-session quality change updates
    /// image quality immediately without needing to restart the frame-rate timer. This class
    /// does not decide when to restart the timer; callers that used to rely on the timer
    /// surviving a relisten/reconnect (it always did — it's gated only by <c>canSend</c>) get
    /// that same behavior for free, since <c>canSend</c> is evaluated fresh on every tick.
    /// </summary>
    public sealed class CaptureLoop : IDisposable
    {
        private readonly IScreenCapture _capture;
        private readonly Func<StreamingSettings> _settingsProvider;
        private readonly Func<bool> _canSend;
        private Timer? _timer;
        private bool _isSendingFrame;

        /// <summary>
        /// Invoked once per captured delta, awaited before the loop moves on to the next delta
        /// in the same tick — preserving the original code's sequential (not fire-and-forget)
        /// send order. The bool argument is true when the delta is a full-frame capture.
        /// </summary>
        public Func<ScreenDelta, bool, Task>? DeltaCapturedAsync { get; set; }

        /// <summary>Invoked when a capture/send tick throws. The original code's bare catch
        /// swallowed everything and set a status message; this exposes the exception instead
        /// so callers (who own the UI/status surface) decide what to show.</summary>
        public event Action<Exception>? CaptureError;

        public CaptureLoop(IScreenCapture capture, Func<StreamingSettings> settingsProvider, Func<bool> canSend)
        {
            _capture          = capture;
            _settingsProvider = settingsProvider;
            _canSend           = canSend;
        }

        public void Start()
        {
            var settings = _settingsProvider();
            _timer?.Stop();
            _timer?.Dispose();
            _timer = new Timer(settings.FrameInterval.TotalMilliseconds) { AutoReset = true };
            _timer.Elapsed += async (_, _) => await TickAsync();
            _timer.Start();
        }

        public void Stop()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
        }

        private async Task TickAsync()
        {
            if (!_canSend() || _isSendingFrame) return;
            _isSendingFrame = true;
            try
            {
                var settings = _settingsProvider();
                var deltas   = _capture.Capture(false, settings.JpegQuality);
                foreach (var delta in deltas)
                {
                    bool full = delta.X == 0 && delta.Y == 0 &&
                                delta.Width  == _capture.Resolution.Width &&
                                delta.Height == _capture.Resolution.Height;

                    if (DeltaCapturedAsync != null)
                        await DeltaCapturedAsync(delta, full);
                }
            }
            catch (Exception ex) { CaptureError?.Invoke(ex); }
            finally { _isSendingFrame = false; }
        }

        /// <summary>Stops the timer and disposes the underlying capture. Matches the original
        /// StopHost ordering (timer stopped before capture disposed).</summary>
        public void Dispose()
        {
            Stop();
            _capture.Dispose();
        }
    }
}
