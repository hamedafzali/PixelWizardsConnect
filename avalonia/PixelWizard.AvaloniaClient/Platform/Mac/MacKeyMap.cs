using System.Collections.Generic;

namespace PixelWizard.AvaloniaClient.Platform.Mac;

/// <summary>
/// Maps Windows Virtual Key codes (sent over the wire) to macOS Carbon/HIToolbox
/// virtual key codes used by CGEventCreateKeyboardEvent.
/// Mac keycodes are positional (ANSI layout), not character codes.
/// </summary>
internal static class MacKeyMap
{
    // Windows VK → macOS keycode
    private static readonly Dictionary<int, ushort> Map = new()
    {
        // ── Letters (Windows VK_A=0x41…VK_Z=0x5A, Mac ANSI layout) ──────────
        { 0x41, 0x00 }, // A
        { 0x42, 0x0B }, // B
        { 0x43, 0x08 }, // C
        { 0x44, 0x02 }, // D
        { 0x45, 0x0E }, // E
        { 0x46, 0x03 }, // F
        { 0x47, 0x05 }, // G
        { 0x48, 0x04 }, // H
        { 0x49, 0x22 }, // I
        { 0x4A, 0x26 }, // J
        { 0x4B, 0x28 }, // K
        { 0x4C, 0x25 }, // L
        { 0x4D, 0x2E }, // M
        { 0x4E, 0x2D }, // N
        { 0x4F, 0x1F }, // O
        { 0x50, 0x23 }, // P
        { 0x51, 0x0C }, // Q
        { 0x52, 0x0F }, // R
        { 0x53, 0x01 }, // S
        { 0x54, 0x11 }, // T
        { 0x55, 0x20 }, // U
        { 0x56, 0x09 }, // V
        { 0x57, 0x0D }, // W
        { 0x58, 0x07 }, // X
        { 0x59, 0x10 }, // Y
        { 0x5A, 0x06 }, // Z

        // ── Digits ────────────────────────────────────────────────────────────
        { 0x30, 0x1D }, // 0
        { 0x31, 0x12 }, // 1
        { 0x32, 0x13 }, // 2
        { 0x33, 0x14 }, // 3
        { 0x34, 0x15 }, // 4
        { 0x35, 0x17 }, // 5
        { 0x36, 0x16 }, // 6
        { 0x37, 0x1A }, // 7
        { 0x38, 0x1C }, // 8
        { 0x39, 0x19 }, // 9

        // ── Control keys ──────────────────────────────────────────────────────
        { 0x08, 0x33 }, // VK_BACK    → Delete
        { 0x09, 0x30 }, // VK_TAB     → Tab
        { 0x0D, 0x24 }, // VK_RETURN  → Return
        { 0x1B, 0x35 }, // VK_ESCAPE  → Escape
        { 0x20, 0x31 }, // VK_SPACE   → Space
        { 0x2E, 0x75 }, // VK_DELETE  → Forward Delete
        { 0x2D, 0x72 }, // VK_INSERT  → Help (no direct Mac equivalent)
        { 0x24, 0x73 }, // VK_HOME    → Home
        { 0x23, 0x77 }, // VK_END     → End
        { 0x21, 0x74 }, // VK_PRIOR   → Page Up
        { 0x22, 0x79 }, // VK_NEXT    → Page Down

        // ── Arrow keys ────────────────────────────────────────────────────────
        { 0x25, 0x7B }, // VK_LEFT
        { 0x26, 0x7E }, // VK_UP
        { 0x27, 0x7C }, // VK_RIGHT
        { 0x28, 0x7D }, // VK_DOWN

        // ── Modifiers ─────────────────────────────────────────────────────────
        { 0xA0, 0x38 }, // VK_LSHIFT  → Shift
        { 0xA1, 0x38 }, // VK_RSHIFT  → Shift (same)
        { 0xA2, 0x3B }, // VK_LCONTROL→ Control
        { 0xA3, 0x3B }, // VK_RCONTROL
        { 0xA4, 0x3A }, // VK_LMENU   → Option
        { 0xA5, 0x3A }, // VK_RMENU
        { 0x5B, 0x37 }, // VK_LWIN    → Command
        { 0x5C, 0x37 }, // VK_RWIN

        // ── Function keys ─────────────────────────────────────────────────────
        { 0x70, 0x7A }, // F1
        { 0x71, 0x78 }, // F2
        { 0x72, 0x63 }, // F3
        { 0x73, 0x76 }, // F4
        { 0x74, 0x60 }, // F5
        { 0x75, 0x61 }, // F6
        { 0x76, 0x62 }, // F7
        { 0x77, 0x64 }, // F8
        { 0x78, 0x65 }, // F9
        { 0x79, 0x6D }, // F10
        { 0x7A, 0x67 }, // F11
        { 0x7B, 0x6F }, // F12

        // ── Punctuation (ANSI) ────────────────────────────────────────────────
        { 0xBA, 0x29 }, // ; :
        { 0xBB, 0x18 }, // = +
        { 0xBC, 0x2B }, // , <
        { 0xBD, 0x1B }, // - _
        { 0xBE, 0x2F }, // . >
        { 0xBF, 0x2C }, // / ?
        { 0xC0, 0x32 }, // ` ~
        { 0xDB, 0x21 }, // [ {
        { 0xDC, 0x2A }, // \ |
        { 0xDD, 0x1E }, // ] }
        { 0xDE, 0x27 }, // ' "

        // ── Numpad ────────────────────────────────────────────────────────────
        { 0x60, 0x52 }, // Numpad 0
        { 0x61, 0x53 }, // Numpad 1
        { 0x62, 0x54 }, // Numpad 2
        { 0x63, 0x55 }, // Numpad 3
        { 0x64, 0x56 }, // Numpad 4
        { 0x65, 0x57 }, // Numpad 5
        { 0x66, 0x58 }, // Numpad 6
        { 0x67, 0x59 }, // Numpad 7
        { 0x68, 0x5B }, // Numpad 8
        { 0x69, 0x5C }, // Numpad 9
        { 0x6A, 0x43 }, // Multiply *
        { 0x6B, 0x45 }, // Add +
        { 0x6D, 0x4E }, // Subtract -
        { 0x6E, 0x41 }, // Decimal .
        { 0x6F, 0x4B }, // Divide /

        // ── Misc ─────────────────────────────────────────────────────────────
        { 0x14, 0x39 }, // VK_CAPITAL  → CapsLock
        { 0x90, 0x47 }, // VK_NUMLOCK  → Clear (Mac numpad)
        { 0x2C, 0x69 }, // VK_SNAPSHOT → F13 (no PrintScreen on Mac)
    };

    /// <summary>Returns the macOS keycode for a Windows VK, or null if not mapped.</summary>
    public static ushort? ToMacKeyCode(int windowsVk) =>
        Map.TryGetValue(windowsVk, out ushort mac) ? mac : null;
}
