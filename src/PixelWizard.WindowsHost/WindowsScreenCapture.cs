using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using PixelWizard.Core;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;

namespace PixelWizard.WindowsHost
{
    public class WindowsScreenCapture : IScreenCapture, IRawScreenCapture
    {
        private readonly Rectangle _bounds;
        private readonly string _displayId;
        private readonly ScreenChangeDetector _detector = new();
        private DateTime _lastFull = DateTime.MinValue;
        private TimeSpan _fullRefreshInterval;

        public WindowsScreenCapture(WindowsMonitorInfo? monitor = null, TimeSpan? fullRefreshInterval = null)
        {
            var m = monitor ?? WindowsMonitors.Primary();
            _bounds = m.Bounds;
            _displayId = m.DeviceName;
            _fullRefreshInterval = fullRefreshInterval ?? TimeSpan.FromSeconds(10);
        }

        public (int Width, int Height) Resolution => (_bounds.Width, _bounds.Height);

        public List<ScreenDelta> Capture(bool forceFullFrame, long jpegQuality)
        {
            bool force = forceFullFrame || (DateTime.UtcNow - _lastFull) >= _fullRefreshInterval;
            using var bmp = CaptureScreen();
            var deltas = _detector.DetectChanges(bmp, force, jpegQuality);
            if (force) _lastFull = DateTime.UtcNow;
            return deltas;
        }

        private Bitmap CaptureScreen()
        {
            var b = _bounds;
            var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(b.X, b.Y, 0, 0, new Size(b.Width, b.Height), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public string DisplayId => _displayId;

        /// <summary>
        /// Same GDI CopyFromScreen grab as the JPEG path (so, like it, no cursor), but kept as
        /// 32bpp BGRA and cropped to even width/height. GDI leaves alpha at 0, so it is set opaque.
        /// </summary>
        public RawFrame? CaptureRaw()
        {
            var b = _bounds;
            int w = RawFrame.EvenFloor(b.Width), h = RawFrame.EvenFloor(b.Height);
            if (w <= 0 || h <= 0) return null;

            using var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(b.X, b.Y, 0, 0, new Size(b.Width, b.Height), CopyPixelOperation.SourceCopy);

            var frame = RawFrame.Rent(w, h, _displayId);
            var data = bmp.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                unsafe
                {
                    // Format32bppRgb scans are top-down, so Stride is positive.
                    var src = new ReadOnlySpan<byte>((void*)data.Scan0, data.Stride * (b.Height - 1) + b.Width * 4);
                    frame.CopyFrom(src, data.Stride, forceOpaque: true);
                }
            }
            catch
            {
                frame.Dispose();
                throw;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return frame;
        }

        public void Reset() => _detector.Reset();

        public void Dispose() => _detector.Dispose();
    }
}
