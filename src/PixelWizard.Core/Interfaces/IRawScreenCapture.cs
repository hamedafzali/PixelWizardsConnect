using System;

namespace PixelWizard.Core.Interfaces
{
    /// <summary>
    /// Optional capability: grab the whole display as one raw BGRA frame, for a video encoder.
    /// Kept separate from <see cref="IScreenCapture"/> so Linux (JPEG only, Phase 4 D5) and
    /// every existing JPEG-delta caller are untouched; the Windows and macOS captures implement
    /// both interfaces. Callers that want raw frames test for it with <c>capture is IRawScreenCapture</c>.
    /// </summary>
    public interface IRawScreenCapture : IDisposable
    {
        /// <summary>
        /// Identity of the captured display, the same display <see cref="IScreenCapture"/> selects:
        /// the monitor device name on Windows (for example <c>\\.\DISPLAY1</c>), the decimal
        /// CGDirectDisplayID on macOS.
        /// </summary>
        string DisplayId { get; }

        /// <summary>
        /// Captures the display now. Returns null if the OS gave no image (for example no
        /// screen-recording permission). The caller owns the frame and must dispose it
        /// (see <see cref="RawFrame"/>). Width and height are always even.
        /// </summary>
        RawFrame? CaptureRaw();
    }
}
