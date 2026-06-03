using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;
using PixelWizard.Core.Interfaces;
using PixelWizard.Core.Models;
using PixelWizard.Core.Protocol;

namespace PixelWizard.AvaloniaClient.Platform.Mac;

[SupportedOSPlatform("osx")]
public sealed class MacScreenCapture : IScreenCapture
{
    private readonly SkiaScreenChangeDetector _detector = new();
    private readonly TimeSpan _fullRefreshInterval;
    private DateTime _lastFull = DateTime.MinValue;

    public MacScreenCapture(TimeSpan? fullRefreshInterval = null)
    {
        _fullRefreshInterval = fullRefreshInterval ?? TimeSpan.FromSeconds(10);

        // Request Screen Recording permission (macOS 10.15+).
        if (!CGPreflightScreenCaptureAccess())
        {
            CGRequestScreenCaptureAccess();
            // Also open the panel so the user knows where to grant permission.
            Process.Start(new ProcessStartInfo("open",
                "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")
            { UseShellExecute = false });
        }
    }

    public (int Width, int Height) Resolution
    {
        get
        {
            var id     = CGMainDisplayID();
            var bounds = CGDisplayBounds(id);
            return ((int)bounds.Size.Width, (int)bounds.Size.Height);
        }
    }

    public List<ScreenDelta> Capture(bool forceFullFrame, long jpegQuality)
    {
        bool force = forceFullFrame || (DateTime.UtcNow - _lastFull) >= _fullRefreshInterval;

        var displayId = CGMainDisplayID();
        var cgImage   = CGDisplayCreateImage(displayId);
        if (cgImage == IntPtr.Zero) return new List<ScreenDelta>();

        try
        {
            using var bitmap = CGImageToSKBitmap(cgImage);
            if (bitmap == null) return new List<ScreenDelta>();

            var deltas = _detector.DetectChanges(bitmap, force, jpegQuality);
            if (force) _lastFull = DateTime.UtcNow;
            return deltas;
        }
        finally
        {
            CGImageRelease(cgImage);
        }
    }

    private static unsafe SKBitmap? CGImageToSKBitmap(IntPtr cgImage)
    {
        int width  = (int)CGImageGetWidth(cgImage);
        int height = (int)CGImageGetHeight(cgImage);
        int bpr    = (int)CGImageGetBytesPerRow(cgImage);

        var provider = CGImageGetDataProvider(cgImage);
        var cfData   = CGDataProviderCopyData(provider);
        if (cfData == IntPtr.Zero) return null;

        try
        {
            byte* src = (byte*)CFDataGetBytePtr(cfData);

            // macOS CGDisplayCreateImage returns kCGBitmapByteOrder32Host | kCGImageAlphaPremultipliedFirst
            // which on little-endian Intel/Apple Silicon is BGRA in memory.
            var info   = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);
            byte* dst  = (byte*)bitmap.GetPixels();

            for (int row = 0; row < height; row++)
                Buffer.MemoryCopy(src + row * bpr, dst + row * width * 4, width * 4, width * 4);

            return bitmap;
        }
        finally
        {
            CFRelease(cfData);
        }
    }

    public void Reset()   => _detector.Reset();
    public void Dispose() => _detector.Dispose();

    // ── CoreGraphics ─────────────────────────────────────────────────────────

    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CG)] static extern uint    CGMainDisplayID();
    [DllImport(CG)] static extern CGRect  CGDisplayBounds(uint displayId);
    [DllImport(CG)] static extern IntPtr  CGDisplayCreateImage(uint displayId);
    [DllImport(CG)] static extern void    CGImageRelease(IntPtr image);
    [DllImport(CG)] static extern nuint   CGImageGetWidth(IntPtr image);
    [DllImport(CG)] static extern nuint   CGImageGetHeight(IntPtr image);
    [DllImport(CG)] static extern nuint   CGImageGetBytesPerRow(IntPtr image);
    [DllImport(CG)] static extern IntPtr  CGImageGetDataProvider(IntPtr image);
    [DllImport(CG)] static extern IntPtr  CGDataProviderCopyData(IntPtr provider);
    [DllImport(CG)] static extern bool    CGPreflightScreenCaptureAccess();
    [DllImport(CG)] static extern bool    CGRequestScreenCaptureAccess();

    [DllImport(CF)] static extern nint    CFDataGetLength(IntPtr cfData);
    [DllImport(CF)] static extern IntPtr  CFDataGetBytePtr(IntPtr cfData);
    [DllImport(CF)] static extern void    CFRelease(IntPtr cf);
}

// Minimal CGRect / CGPoint / CGSize structs that match CoreGraphics ABI on 64-bit macOS
[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint { public double X; public double Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize  { public double Width; public double Height; }

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect  { public CGPoint Origin; public CGSize Size; }
