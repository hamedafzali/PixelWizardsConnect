using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ScreenAutomationApp
{
    public class ScreenChangeDetector
    {
        private Bitmap? _previousFrame;
        private readonly int _blockSize = 32; // Size of blocks to check for changes
        private readonly int _threshold = 10; // Pixel difference threshold

        public List<ScreenDelta> DetectChanges(Bitmap currentFrame, bool forceFullFrame = false, long jpegQuality = 75)
        {
            var deltas = new List<ScreenDelta>();

            if (_previousFrame == null || forceFullFrame)
            {
                deltas.Add(CreateFullDelta(currentFrame, jpegQuality));
                _previousFrame?.Dispose();
                _previousFrame = new Bitmap(currentFrame);
                return deltas;
            }

            // Check if dimensions changed
            if (_previousFrame.Width != currentFrame.Width || _previousFrame.Height != currentFrame.Height)
            {
                deltas.Add(CreateFullDelta(currentFrame, jpegQuality));
                _previousFrame.Dispose();
                _previousFrame = new Bitmap(currentFrame);
                return deltas;
            }

            // Detect changed blocks
            var changedBlocks = new List<Rectangle>();
            
            int blocksX = (int)Math.Ceiling((double)currentFrame.Width / _blockSize);
            int blocksY = (int)Math.Ceiling((double)currentFrame.Height / _blockSize);

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int x = bx * _blockSize;
                    int y = by * _blockSize;
                    int width = Math.Min(_blockSize, currentFrame.Width - x);
                    int height = Math.Min(_blockSize, currentFrame.Height - y);

                    if (HasBlockChanged(x, y, width, height, currentFrame))
                    {
                        changedBlocks.Add(new Rectangle(x, y, width, height));
                    }
                }
            }

            // Merge adjacent blocks to reduce number of deltas
            var mergedRegions = MergeAdjacentBlocks(changedBlocks);

            // Create deltas for each region
            foreach (var region in mergedRegions)
            {
                var delta = CreateDelta(currentFrame, region, jpegQuality);
                if (delta != null)
                {
                    deltas.Add(delta);
                }
            }

            // Update previous frame
            _previousFrame.Dispose();
            _previousFrame = new Bitmap(currentFrame);

            return deltas;
        }

        private bool HasBlockChanged(int x, int y, int width, int height, Bitmap currentFrame)
        {
            // Sample pixels from the block (not every pixel for performance)
            int sampleStep = 4;
            int changedPixels = 0;
            int totalSamples = 0;

            for (int py = y; py < y + height; py += sampleStep)
            {
                for (int px = x; px < x + width; px += sampleStep)
                {
                    if (px >= currentFrame.Width || py >= currentFrame.Height)
                        continue;

                    Color currentPixel = currentFrame.GetPixel(px, py);
                    Color previousPixel = _previousFrame!.GetPixel(px, py);

                    int diff = Math.Abs(currentPixel.R - previousPixel.R) +
                               Math.Abs(currentPixel.G - previousPixel.G) +
                               Math.Abs(currentPixel.B - previousPixel.B);

                    if (diff > _threshold)
                    {
                        changedPixels++;
                    }
                    totalSamples++;
                }
            }

            // If more than 10% of sampled pixels changed, consider block changed
            return totalSamples > 0 && (double)changedPixels / totalSamples > 0.1;
        }

        private List<Rectangle> MergeAdjacentBlocks(List<Rectangle> blocks)
        {
            if (blocks.Count == 0)
                return blocks;

            var merged = new List<Rectangle>(blocks);
            bool mergedAny;

            do
            {
                mergedAny = false;
                for (int i = 0; i < merged.Count && !mergedAny; i++)
                {
                    for (int j = i + 1; j < merged.Count; j++)
                    {
                        if (AreAdjacentOrOverlapping(merged[i], merged[j]))
                        {
                            merged[i] = UnionRectangles(merged[i], merged[j]);
                            merged.RemoveAt(j);
                            mergedAny = true;
                            break;
                        }
                    }
                }
            } while (mergedAny);

            return merged;
        }

        private bool AreAdjacentOrOverlapping(Rectangle r1, Rectangle r2)
        {
            // Check if rectangles are touching or overlapping
            int margin = _blockSize; // Allow small gaps
            return r1.Left - margin <= r2.Right &&
                   r1.Right + margin >= r2.Left &&
                   r1.Top - margin <= r2.Bottom &&
                   r1.Bottom + margin >= r2.Top;
        }

        private Rectangle UnionRectangles(Rectangle r1, Rectangle r2)
        {
            int x = Math.Min(r1.X, r2.X);
            int y = Math.Min(r1.Y, r2.Y);
            int width = Math.Max(r1.Right, r2.Right) - x;
            int height = Math.Max(r1.Bottom, r2.Bottom) - y;
            return new Rectangle(x, y, width, height);
        }

        private ScreenDelta CreateFullDelta(Bitmap frame, long jpegQuality)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                SaveJpeg(frame, ms, jpegQuality);
                return new ScreenDelta
                {
                    X = 0,
                    Y = 0,
                    Width = frame.Width,
                    Height = frame.Height,
                    ImageData = ms.ToArray()
                };
            }
        }

        private ScreenDelta? CreateDelta(Bitmap frame, Rectangle region, long jpegQuality)
        {
            try
            {
                using (Bitmap regionBitmap = frame.Clone(region, frame.PixelFormat))
                using (MemoryStream ms = new MemoryStream())
                {
                    SaveJpeg(regionBitmap, ms, jpegQuality);
                    return new ScreenDelta
                    {
                        X = region.X,
                        Y = region.Y,
                        Width = region.Width,
                        Height = region.Height,
                        ImageData = ms.ToArray()
                    };
                }
            }
            catch
            {
                return null;
            }
        }

        private static void SaveJpeg(Bitmap bitmap, Stream stream, long jpegQuality)
        {
            ImageCodecInfo? jpegCodec = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

            if (jpegCodec == null)
            {
                bitmap.Save(stream, ImageFormat.Jpeg);
                return;
            }

            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(jpegQuality, 1, 100));
            bitmap.Save(stream, jpegCodec, parameters);
        }

        public void Reset()
        {
            _previousFrame?.Dispose();
            _previousFrame = null;
        }

        public void Dispose()
        {
            _previousFrame?.Dispose();
        }
    }
}
