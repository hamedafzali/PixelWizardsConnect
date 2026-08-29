using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using PixelWizard.Protocol;

namespace PixelWizard.WindowsHost
{
    public class ScreenChangeDetector : IDisposable
    {
        private Bitmap? _previousFrame;
        private readonly int _blockSize = 32;
        private readonly int _threshold = 10;

        public List<ScreenDelta> DetectChanges(Bitmap current, bool forceFullFrame, long jpegQuality)
        {
            var deltas = new List<ScreenDelta>();

            if (_previousFrame == null || forceFullFrame ||
                _previousFrame.Width != current.Width || _previousFrame.Height != current.Height)
            {
                deltas.Add(CreateFullDelta(current, jpegQuality));
                _previousFrame?.Dispose();
                _previousFrame = new Bitmap(current);
                return deltas;
            }

            var changed = new List<Rectangle>();
            int blocksX = (int)Math.Ceiling((double)current.Width / _blockSize);
            int blocksY = (int)Math.Ceiling((double)current.Height / _blockSize);

            for (int by = 0; by < blocksY; by++)
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int x = bx * _blockSize, y = by * _blockSize;
                    int w = Math.Min(_blockSize, current.Width - x);
                    int h = Math.Min(_blockSize, current.Height - y);
                    if (HasChanged(x, y, w, h, current))
                        changed.Add(new Rectangle(x, y, w, h));
                }

            foreach (var region in MergeBlocks(changed))
            {
                var d = CreateRegionDelta(current, region, jpegQuality);
                if (d != null) deltas.Add(d);
            }

            _previousFrame.Dispose();
            _previousFrame = new Bitmap(current);
            return deltas;
        }

        private unsafe bool HasChanged(int x, int y, int w, int h, Bitmap current)
        {
            var rect = new Rectangle(x, y, w, h);
            var bmpDataCurr = current.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bmpDataPrev = _previousFrame!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            const int step = 4;
            int changed = 0, total = 0;
            try
            {
                for (int row = 0; row < h; row += step)
                for (int col = 0; col < w; col += step)
                {
                    byte* pc = (byte*)bmpDataCurr.Scan0 + row * bmpDataCurr.Stride + col * 4;
                    byte* pp = (byte*)bmpDataPrev.Scan0 + row * bmpDataPrev.Stride + col * 4;
                    int diff = Math.Abs(pc[2] - pp[2])   // R
                             + Math.Abs(pc[1] - pp[1])   // G
                             + Math.Abs(pc[0] - pp[0]);  // B
                    if (diff > _threshold) changed++;
                    total++;
                }
            }
            finally
            {
                current.UnlockBits(bmpDataCurr);
                _previousFrame!.UnlockBits(bmpDataPrev);
            }
            return total > 0 && (double)changed / total > 0.10;
        }

        private List<Rectangle> MergeBlocks(List<Rectangle> blocks)
        {
            if (blocks.Count == 0) return blocks;
            var merged = new List<Rectangle>(blocks);
            bool any;
            do
            {
                any = false;
                for (int i = 0; i < merged.Count && !any; i++)
                    for (int j = i + 1; j < merged.Count; j++)
                        if (Adjacent(merged[i], merged[j]))
                        {
                            merged[i] = Union(merged[i], merged[j]);
                            merged.RemoveAt(j);
                            any = true;
                            break;
                        }
            } while (any);
            return merged;
        }

        private bool Adjacent(Rectangle a, Rectangle b)
        {
            int m = _blockSize;
            return a.Left - m <= b.Right && a.Right + m >= b.Left &&
                   a.Top - m <= b.Bottom && a.Bottom + m >= b.Top;
        }

        private static Rectangle Union(Rectangle a, Rectangle b)
        {
            int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new Rectangle(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
        }

        private static ScreenDelta CreateFullDelta(Bitmap frame, long quality)
        {
            using var ms = new MemoryStream();
            SaveJpeg(frame, ms, quality);
            return new ScreenDelta { X = 0, Y = 0, Width = frame.Width, Height = frame.Height, ImageData = ms.ToArray() };
        }

        private static ScreenDelta? CreateRegionDelta(Bitmap frame, Rectangle region, long quality)
        {
            try
            {
                using var bmp = frame.Clone(region, frame.PixelFormat);
                using var ms = new MemoryStream();
                SaveJpeg(bmp, ms, quality);
                return new ScreenDelta { X = region.X, Y = region.Y, Width = region.Width, Height = region.Height, ImageData = ms.ToArray() };
            }
            catch { return null; }
        }

        private static void SaveJpeg(Bitmap bmp, Stream stream, long quality)
        {
            var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (codec == null) { bmp.Save(stream, ImageFormat.Jpeg); return; }
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 1, 100));
            bmp.Save(stream, codec, p);
        }

        public void Reset()
        {
            _previousFrame?.Dispose();
            _previousFrame = null;
        }

        public void Dispose() => _previousFrame?.Dispose();
    }
}
