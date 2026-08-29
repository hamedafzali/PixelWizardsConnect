using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;

namespace PixelWizard.LinuxHost;

/// <summary>
/// Screen capture for Linux via Xlib. Requires libX11 and an active DISPLAY.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxScreenCapture : IScreenCapture
{
    private readonly SkiaScreenChangeDetector _detector = new();
    private readonly TimeSpan _fullRefreshInterval;
    private DateTime _lastFull = DateTime.MinValue;
    private readonly IntPtr _display;
    private readonly IntPtr _rootWindow;
    private readonly int    _screen;

    public LinuxScreenCapture(TimeSpan? fullRefreshInterval = null)
    {
        _fullRefreshInterval = fullRefreshInterval ?? TimeSpan.FromSeconds(10);
        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException(
                "Cannot open X11 display. Ensure DISPLAY is set (e.g. DISPLAY=:0).");
        _screen     = XDefaultScreen(_display);
        _rootWindow = XRootWindow(_display, _screen);
    }

    public (int Width, int Height) Resolution
    {
        get
        {
            XGetGeometry(_display, _rootWindow,
                out _, out _, out _, out uint w, out uint h, out _, out _);
            return ((int)w, (int)h);
        }
    }

    public unsafe List<ScreenDelta> Capture(bool forceFullFrame, long jpegQuality)
    {
        bool force = forceFullFrame || (DateTime.UtcNow - _lastFull) >= _fullRefreshInterval;
        var (width, height) = Resolution;

        var image = XGetImage(_display, _rootWindow, 0, 0, (uint)width, (uint)height, AllPlanes, ZPixmap);
        if (image == IntPtr.Zero) return [];

        try
        {
            using var bitmap = XImageToSKBitmap(image, width, height);
            if (bitmap == null) return [];

            var deltas = _detector.DetectChanges(bitmap, force, jpegQuality);
            if (force) _lastFull = DateTime.UtcNow;
            return deltas;
        }
        finally
        {
            XDestroyImage(image);
        }
    }

    private static unsafe SKBitmap? XImageToSKBitmap(IntPtr image, int width, int height)
    {
        // XImage 64-bit field layout (little-endian Linux):
        //   offset  0: int width
        //   offset  4: int height
        //   offset  8: int xoffset
        //   offset 12: int format
        //   offset 16: char* data
        //   offset 24: int byte_order
        //   offset 28: int bitmap_unit
        //   offset 32: int bitmap_bit_order
        //   offset 36: int bitmap_pad
        //   offset 40: int depth
        //   offset 44: int bytes_per_line
        //   offset 48: int bits_per_pixel
        IntPtr data      = Marshal.ReadIntPtr(image, 16);
        int bytesPerLine = Marshal.ReadInt32(image,  44);
        int bitsPerPixel = Marshal.ReadInt32(image,  48);

        if (data == IntPtr.Zero) return null;

        var info   = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        byte* dst  = (byte*)bitmap.GetPixels();
        byte* src  = (byte*)data;

        if (bitsPerPixel == 32)
        {
            for (int row = 0; row < height; row++)
            {
                byte* srcRow = src + row * bytesPerLine;
                byte* dstRow = dst + row * width * 4;
                Buffer.MemoryCopy(srcRow, dstRow, width * 4, width * 4);
                // X11 leaves the alpha byte as 0 in BGRX — force it to 0xFF.
                for (int col = 0; col < width; col++)
                    dstRow[col * 4 + 3] = 0xFF;
            }
        }
        else
        {
            // 24-bit BGR packed: expand to BGRA
            for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
            {
                int si = row * bytesPerLine + col * 3;
                int di = (row * width + col) * 4;
                dst[di + 0] = src[si + 0];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = 0xFF;
            }
        }

        return bitmap;
    }

    public void Reset()   => _detector.Reset();

    public void Dispose()
    {
        _detector.Dispose();
        if (_display != IntPtr.Zero)
            XCloseDisplay(_display);
    }

    // ── Xlib P/Invoke ────────────────────────────────────────────────────────

    private const string Xlib      = "libX11";
    private const int    ZPixmap   = 2;
    private const ulong  AllPlanes = ulong.MaxValue;

    [DllImport(Xlib)] static extern IntPtr XOpenDisplay(string? display);
    [DllImport(Xlib)] static extern void   XCloseDisplay(IntPtr display);
    [DllImport(Xlib)] static extern int    XDefaultScreen(IntPtr display);
    [DllImport(Xlib)] static extern IntPtr XRootWindow(IntPtr display, int screen);
    [DllImport(Xlib)] static extern int    XGetGeometry(
        IntPtr display, IntPtr drawable,
        out IntPtr root, out int x, out int y,
        out uint width, out uint height, out uint borderWidth, out uint depth);
    [DllImport(Xlib)] static extern IntPtr XGetImage(
        IntPtr display, IntPtr drawable,
        int x, int y, uint width, uint height, ulong planeMask, int format);
    [DllImport(Xlib)] static extern void XDestroyImage(IntPtr image);
}
