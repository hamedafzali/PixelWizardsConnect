using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;

namespace PixelWizard.WindowsHost
{
    public class WindowsScreenCapture : IScreenCapture
    {
        private readonly Rectangle _bounds;
        private readonly ScreenChangeDetector _detector = new();
        private DateTime _lastFull = DateTime.MinValue;
        private TimeSpan _fullRefreshInterval;

        public WindowsScreenCapture(WindowsMonitorInfo? monitor = null, TimeSpan? fullRefreshInterval = null)
        {
            _bounds = (monitor ?? WindowsMonitors.Primary()).Bounds;
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

        public void Reset() => _detector.Reset();

        public void Dispose() => _detector.Dispose();
    }
}
