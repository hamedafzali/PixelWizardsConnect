using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PixelWizard.Core.Interfaces;

namespace PixelWizard.LinuxHost;

/// <summary>
/// Injects mouse and keyboard input on Linux via XTest. Requires libX11 and libXtst.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxInputInjector : IInputInjector
{
    private readonly IntPtr _display;
    private readonly int    _screen;

    public LinuxInputInjector()
    {
        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException(
                "Cannot open X11 display for input injection. Ensure DISPLAY is set.");
        _screen = XDefaultScreen(_display);
    }

    public void MoveMouse(int x, int y)
    {
        XTestFakeMotionEvent(_display, _screen, x, y, 0);
        XFlush(_display);
    }

    public void Click(int x, int y, bool leftButton)
    {
        MoveMouse(x, y);
        uint button = leftButton ? 1u : 3u;
        XTestFakeButtonEvent(_display, button, true,  0);
        XTestFakeButtonEvent(_display, button, false, 0);
        XFlush(_display);
    }

    public void ButtonDown(int x, int y, bool leftButton)
    {
        MoveMouse(x, y);
        XTestFakeButtonEvent(_display, leftButton ? 1u : 3u, true,  0);
        XFlush(_display);
    }

    public void ButtonUp(int x, int y, bool leftButton)
    {
        MoveMouse(x, y);
        XTestFakeButtonEvent(_display, leftButton ? 1u : 3u, false, 0);
        XFlush(_display);
    }

    public void SendKey(int windowsVk, bool isKeyDown)
    {
        ulong? keysym = LinuxKeyMap.ToKeySym(windowsVk);
        if (keysym == null) return;
        uint keycode = XKeysymToKeycode(_display, keysym.Value);
        if (keycode == 0) return;
        XTestFakeKeyEvent(_display, keycode, isKeyDown, 0);
        XFlush(_display);
    }

    ~LinuxInputInjector()
    {
        if (_display != IntPtr.Zero)
            XCloseDisplay(_display);
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    private const string Xlib = "libX11";
    private const string Xtst = "libXtst";

    [DllImport(Xlib)] static extern IntPtr XOpenDisplay(string? display);
    [DllImport(Xlib)] static extern void   XCloseDisplay(IntPtr display);
    [DllImport(Xlib)] static extern int    XDefaultScreen(IntPtr display);
    [DllImport(Xlib)] static extern void   XFlush(IntPtr display);
    [DllImport(Xlib)] static extern uint   XKeysymToKeycode(IntPtr display, ulong keysym);

    [DllImport(Xtst)] static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, ulong delay);
    [DllImport(Xtst)] static extern int XTestFakeButtonEvent(IntPtr display, uint button, bool isPress, ulong delay);
    [DllImport(Xtst)] static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, bool isPress, ulong delay);
}
