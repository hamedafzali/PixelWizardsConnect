using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace PixelWizard.AvaloniaClient.Platform;

/// <summary>
/// macOS takes the Dock icon from the .app bundle and ignores Window.Icon, so a run that is
/// not bundled (./run.sh, dotnet run) shows a generic one. This sets it from the app's own PNG.
/// </summary>
public static class MacDockIcon
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>Call on the UI thread after launch has finished: launch resets the icon. Best effort: a failure keeps the default icon.</summary>
    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            using var asset = AssetLoader.Open(new Uri("avares://PixelWizard.AvaloniaClient/Assets/app-icon-512.png"));
            using var ms = new System.IO.MemoryStream();
            asset.CopyTo(ms);
            var bytes = ms.ToArray();

            var data = IntPtr.Zero;
            unsafe
            {
                fixed (byte* p = bytes)
                    data = Send(Class("NSData"), Sel("dataWithBytes:length:"), (IntPtr)p, (nuint)bytes.Length);
            }
            var image = Send(Send(Class("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
            if (image == IntPtr.Zero) return;
            var app = Send(Class("NSApplication"), Sel("sharedApplication"));
            Send(app, Sel("setApplicationIconImage:"), image);
            Send(image, Sel("release"));
        }
        catch (Exception) { /* the default Dock icon is harmless */ }
    }

    private static IntPtr Class(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1, nuint arg2);
}
