using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using PixelWizard.Core.Protocol;

namespace PixelWizard.AvaloniaClient.Platform.Mac;

/// <summary>
/// Delta detector that works on SKBitmap — no System.Drawing dependency,
/// so it runs on macOS, Linux and Windows.
/// </summary>
internal sealed class SkiaScreenChangeDetector : IDisposable
{
    private SKBitmap? _prev;
    private const int BlockSize = 32;
    private const double ChangeThreshold = 0.10; // 10% of sampled pixels must change

    public List<ScreenDelta> DetectChanges(SKBitmap current, bool forceFullFrame, long jpegQuality)
    {
        var deltas = new List<ScreenDelta>();

        if (_prev == null || forceFullFrame ||
            _prev.Width != current.Width || _prev.Height != current.Height)
        {
            deltas.Add(FullDelta(current, jpegQuality));
            _prev?.Dispose();
            _prev = current.Copy();
            return deltas;
        }

        var changed = FindChangedBlocks(current);
        foreach (var r in MergeBlocks(changed))
        {
            var d = RegionDelta(current, r, jpegQuality);
            if (d != null) deltas.Add(d);
        }

        _prev.Dispose();
        _prev = current.Copy();
        return deltas;
    }

    private List<(int x, int y, int w, int h)> FindChangedBlocks(SKBitmap current)
    {
        var blocks = new List<(int, int, int, int)>();
        int cols = (int)Math.Ceiling((double)current.Width  / BlockSize);
        int rows = (int)Math.Ceiling((double)current.Height / BlockSize);

        for (int by = 0; by < rows; by++)
        for (int bx = 0; bx < cols; bx++)
        {
            int x = bx * BlockSize, y = by * BlockSize;
            int w = Math.Min(BlockSize, current.Width  - x);
            int h = Math.Min(BlockSize, current.Height - y);
            if (BlockChanged(current, x, y, w, h))
                blocks.Add((x, y, w, h));
        }
        return blocks;
    }

    private unsafe bool BlockChanged(SKBitmap current, int x, int y, int w, int h)
    {
        const int step = 4;
        int stride  = current.Width * 4;
        byte* curr  = (byte*)current.GetPixels().ToPointer();
        byte* prev  = (byte*)_prev!.GetPixels().ToPointer();
        int changed = 0, total = 0;

        for (int py = y; py < y + h; py += step)
        for (int px = x; px < x + w; px += step)
        {
            if (px >= current.Width || py >= current.Height) continue;
            int off = py * stride + px * 4;
            int diff = Math.Abs(curr[off]     - prev[off])
                     + Math.Abs(curr[off + 1] - prev[off + 1])
                     + Math.Abs(curr[off + 2] - prev[off + 2]);
            if (diff > 10) changed++;
            total++;
        }
        return total > 0 && (double)changed / total > ChangeThreshold;
    }

    private static List<(int x, int y, int w, int h)> MergeBlocks(List<(int x, int y, int w, int h)> blocks)
    {
        if (blocks.Count == 0) return blocks;
        var merged = new List<(int x, int y, int w, int h)>(blocks);
        bool any;
        do
        {
            any = false;
            for (int i = 0; i < merged.Count && !any; i++)
            for (int j = i + 1; j < merged.Count; j++)
            {
                var a = merged[i]; var b = merged[j];
                if (Adjacent(a, b))
                {
                    merged[i] = Union(a, b);
                    merged.RemoveAt(j);
                    any = true;
                    break;
                }
            }
        } while (any);
        return merged;
    }

    private static bool Adjacent((int x, int y, int w, int h) a, (int x, int y, int w, int h) b)
    {
        int m = BlockSize;
        return a.x - m <= b.x + b.w && a.x + a.w + m >= b.x &&
               a.y - m <= b.y + b.h && a.y + a.h + m >= b.y;
    }

    private static (int x, int y, int w, int h) Union(
        (int x, int y, int w, int h) a, (int x, int y, int w, int h) b)
    {
        int nx = Math.Min(a.x, b.x), ny = Math.Min(a.y, b.y);
        return (nx, ny,
                Math.Max(a.x + a.w, b.x + b.w) - nx,
                Math.Max(a.y + a.h, b.y + b.h) - ny);
    }

    private static ScreenDelta FullDelta(SKBitmap bmp, long quality) =>
        new() { X = 0, Y = 0, Width = bmp.Width, Height = bmp.Height, ImageData = EncodeJpeg(bmp, quality) };

    private static ScreenDelta? RegionDelta(SKBitmap bmp, (int x, int y, int w, int h) r, long quality)
    {
        try
        {
            using var region = new SKBitmap(r.w, r.h);
            using var canvas = new SKCanvas(region);
            canvas.DrawBitmap(bmp, SKRect.Create(r.x, r.y, r.w, r.h),
                                   SKRect.Create(0, 0, r.w, r.h));
            return new ScreenDelta { X = r.x, Y = r.y, Width = r.w, Height = r.h, ImageData = EncodeJpeg(region, quality) };
        }
        catch { return null; }
    }

    private static byte[] EncodeJpeg(SKBitmap bmp, long quality)
    {
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, (int)Math.Clamp(quality, 1, 100));
        return data.ToArray();
    }

    public void Reset() { _prev?.Dispose(); _prev = null; }
    public void Dispose() => _prev?.Dispose();
}
