using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PixelWizard.Core.Interfaces;

namespace PixelWizard.AvaloniaClient.Platform.Mac;

/// <summary>
/// Injects mouse and keyboard input on macOS via CoreGraphics (Quartz) events.
/// Requires Accessibility permission: System Settings → Privacy → Accessibility.
/// </summary>
[SupportedOSPlatform("osx")]
public sealed class MacInputInjector : IInputInjector
{
    public MacInputInjector()
    {
        if (!AXIsProcessTrusted())
        {
            // Open the Accessibility panel in System Settings so the user can grant permission.
            Process.Start(new ProcessStartInfo("open",
                "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
            {
                UseShellExecute = false
            });
        }
    }

    public void MoveMouse(int x, int y)
    {
        var pt  = new CGPoint { X = x, Y = y };
        var evt = CGEventCreateMouseEvent(IntPtr.Zero, CGEventType.MouseMoved, pt, 0);
        if (evt == IntPtr.Zero) return;
        CGEventPost(CGEventTapLocation.HID, evt);
        CFRelease(evt);
    }

    public void Click(int x, int y, bool leftButton)
    {
        var pt     = new CGPoint { X = x, Y = y };
        var button = leftButton ? 0 : 1;
        var down   = leftButton ? CGEventType.LeftMouseDown  : CGEventType.RightMouseDown;
        var up     = leftButton ? CGEventType.LeftMouseUp    : CGEventType.RightMouseUp;

        var evtDown = CGEventCreateMouseEvent(IntPtr.Zero, down, pt, button);
        var evtUp   = CGEventCreateMouseEvent(IntPtr.Zero, up,   pt, button);

        if (evtDown != IntPtr.Zero) { CGEventPost(CGEventTapLocation.HID, evtDown); CFRelease(evtDown); }
        if (evtUp   != IntPtr.Zero) { CGEventPost(CGEventTapLocation.HID, evtUp);   CFRelease(evtUp);   }
    }

    public void ButtonDown(int x, int y, bool leftButton)
    {
        var pt  = new CGPoint { X = x, Y = y };
        var evt = CGEventCreateMouseEvent(IntPtr.Zero,
            leftButton ? CGEventType.LeftMouseDown : CGEventType.RightMouseDown, pt,
            leftButton ? 0 : 1);
        if (evt == IntPtr.Zero) return;
        CGEventPost(CGEventTapLocation.HID, evt);
        CFRelease(evt);
    }

    public void ButtonUp(int x, int y, bool leftButton)
    {
        var pt  = new CGPoint { X = x, Y = y };
        var evt = CGEventCreateMouseEvent(IntPtr.Zero,
            leftButton ? CGEventType.LeftMouseUp : CGEventType.RightMouseUp, pt,
            leftButton ? 0 : 1);
        if (evt == IntPtr.Zero) return;
        CGEventPost(CGEventTapLocation.HID, evt);
        CFRelease(evt);
    }

    public void SendKey(int windowsVk, bool isKeyDown)
    {
        ushort? mac = MacKeyMap.ToMacKeyCode(windowsVk);
        if (mac == null) return;

        var evt = CGEventCreateKeyboardEvent(IntPtr.Zero, mac.Value, isKeyDown);
        if (evt == IntPtr.Zero) return;
        CGEventPost(CGEventTapLocation.HID, evt);
        CFRelease(evt);
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AX = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    [DllImport(CG)] static extern IntPtr CGEventCreateMouseEvent(IntPtr src, CGEventType type, CGPoint pt, int btn);
    [DllImport(CG)] static extern IntPtr CGEventCreateKeyboardEvent(IntPtr src, ushort keyCode, bool keyDown);
    [DllImport(CG)] static extern void   CGEventPost(CGEventTapLocation tap, IntPtr evt);
    [DllImport(CF)] static extern void   CFRelease(IntPtr cf);
    [DllImport(AX)] static extern bool   AXIsProcessTrusted();

    private enum CGEventType     : uint { MouseMoved = 5, LeftMouseDown = 1, LeftMouseUp = 2, RightMouseDown = 3, RightMouseUp = 4 }
    private enum CGEventTapLocation : uint { HID = 0 }
}
