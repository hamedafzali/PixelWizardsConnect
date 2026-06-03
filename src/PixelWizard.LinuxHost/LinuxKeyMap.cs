using System.Collections.Generic;

namespace PixelWizard.LinuxHost;

/// <summary>
/// Maps Windows Virtual Key codes (sent over the wire) to X11 keysyms
/// as defined in X11/keysymdef.h.
/// </summary>
internal static class LinuxKeyMap
{
    private static readonly Dictionary<int, ulong> Map = new()
    {
        // ── Letters (VK_A=0x41…VK_Z=0x5A → XK_a=0x61…XK_z=0x7A) ───────────
        { 0x41, 0x0061 }, { 0x42, 0x0062 }, { 0x43, 0x0063 }, { 0x44, 0x0064 },
        { 0x45, 0x0065 }, { 0x46, 0x0066 }, { 0x47, 0x0067 }, { 0x48, 0x0068 },
        { 0x49, 0x0069 }, { 0x4A, 0x006A }, { 0x4B, 0x006B }, { 0x4C, 0x006C },
        { 0x4D, 0x006D }, { 0x4E, 0x006E }, { 0x4F, 0x006F }, { 0x50, 0x0070 },
        { 0x51, 0x0071 }, { 0x52, 0x0072 }, { 0x53, 0x0073 }, { 0x54, 0x0074 },
        { 0x55, 0x0075 }, { 0x56, 0x0076 }, { 0x57, 0x0077 }, { 0x58, 0x0078 },
        { 0x59, 0x0079 }, { 0x5A, 0x007A },

        // ── Digits ────────────────────────────────────────────────────────────
        { 0x30, 0x0030 }, { 0x31, 0x0031 }, { 0x32, 0x0032 }, { 0x33, 0x0033 },
        { 0x34, 0x0034 }, { 0x35, 0x0035 }, { 0x36, 0x0036 }, { 0x37, 0x0037 },
        { 0x38, 0x0038 }, { 0x39, 0x0039 },

        // ── Control keys ──────────────────────────────────────────────────────
        { 0x08, 0xFF08 }, // VK_BACK    → XK_BackSpace
        { 0x09, 0xFF09 }, // VK_TAB     → XK_Tab
        { 0x0D, 0xFF0D }, // VK_RETURN  → XK_Return
        { 0x1B, 0xFF1B }, // VK_ESCAPE  → XK_Escape
        { 0x20, 0x0020 }, // VK_SPACE   → XK_space
        { 0x2E, 0xFFFF }, // VK_DELETE  → XK_Delete
        { 0x2D, 0xFF63 }, // VK_INSERT  → XK_Insert
        { 0x24, 0xFF50 }, // VK_HOME    → XK_Home
        { 0x23, 0xFF57 }, // VK_END     → XK_End
        { 0x21, 0xFF55 }, // VK_PRIOR   → XK_Page_Up
        { 0x22, 0xFF56 }, // VK_NEXT    → XK_Page_Down

        // ── Arrow keys ────────────────────────────────────────────────────────
        { 0x25, 0xFF51 }, // VK_LEFT
        { 0x26, 0xFF52 }, // VK_UP
        { 0x27, 0xFF53 }, // VK_RIGHT
        { 0x28, 0xFF54 }, // VK_DOWN

        // ── Modifiers ─────────────────────────────────────────────────────────
        { 0xA0, 0xFFE1 }, // VK_LSHIFT   → XK_Shift_L
        { 0xA1, 0xFFE2 }, // VK_RSHIFT   → XK_Shift_R
        { 0xA2, 0xFFE3 }, // VK_LCONTROL → XK_Control_L
        { 0xA3, 0xFFE4 }, // VK_RCONTROL → XK_Control_R
        { 0xA4, 0xFFE9 }, // VK_LMENU    → XK_Alt_L
        { 0xA5, 0xFFEA }, // VK_RMENU    → XK_Alt_R
        { 0x5B, 0xFFEB }, // VK_LWIN     → XK_Super_L
        { 0x5C, 0xFFEC }, // VK_RWIN     → XK_Super_R

        // ── Function keys ─────────────────────────────────────────────────────
        { 0x70, 0xFFBE }, // F1
        { 0x71, 0xFFBF }, // F2
        { 0x72, 0xFFC0 }, // F3
        { 0x73, 0xFFC1 }, // F4
        { 0x74, 0xFFC2 }, // F5
        { 0x75, 0xFFC3 }, // F6
        { 0x76, 0xFFC4 }, // F7
        { 0x77, 0xFFC5 }, // F8
        { 0x78, 0xFFC6 }, // F9
        { 0x79, 0xFFC7 }, // F10
        { 0x7A, 0xFFC8 }, // F11
        { 0x7B, 0xFFC9 }, // F12

        // ── Punctuation (US layout) ───────────────────────────────────────────
        { 0xBA, 0x003B }, // ; :
        { 0xBB, 0x003D }, // = +
        { 0xBC, 0x002C }, // , <
        { 0xBD, 0x002D }, // - _
        { 0xBE, 0x002E }, // . >
        { 0xBF, 0x002F }, // / ?
        { 0xC0, 0x0060 }, // ` ~
        { 0xDB, 0x005B }, // [ {
        { 0xDC, 0x005C }, // \ |
        { 0xDD, 0x005D }, // ] }
        { 0xDE, 0x0027 }, // ' "

        // ── Numpad ────────────────────────────────────────────────────────────
        { 0x60, 0xFFB0 }, // Numpad 0
        { 0x61, 0xFFB1 }, // Numpad 1
        { 0x62, 0xFFB2 }, // Numpad 2
        { 0x63, 0xFFB3 }, // Numpad 3
        { 0x64, 0xFFB4 }, // Numpad 4
        { 0x65, 0xFFB5 }, // Numpad 5
        { 0x66, 0xFFB6 }, // Numpad 6
        { 0x67, 0xFFB7 }, // Numpad 7
        { 0x68, 0xFFB8 }, // Numpad 8
        { 0x69, 0xFFB9 }, // Numpad 9
        { 0x6A, 0xFFAA }, // Multiply
        { 0x6B, 0xFFAB }, // Add
        { 0x6D, 0xFFAD }, // Subtract
        { 0x6E, 0xFFAE }, // Decimal
        { 0x6F, 0xFFAF }, // Divide

        // ── Misc ──────────────────────────────────────────────────────────────
        { 0x14, 0xFFE5 }, // VK_CAPITAL  → XK_Caps_Lock
        { 0x90, 0xFF7F }, // VK_NUMLOCK  → XK_Num_Lock
        { 0x91, 0xFF14 }, // VK_SCROLL   → XK_Scroll_Lock
        { 0x2C, 0xFF61 }, // VK_SNAPSHOT → XK_Print
    };

    public static ulong? ToKeySym(int windowsVk) =>
        Map.TryGetValue(windowsVk, out ulong ks) ? ks : null;
}
