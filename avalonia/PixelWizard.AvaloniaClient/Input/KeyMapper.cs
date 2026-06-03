using System.Collections.Generic;
using Avalonia.Input;

namespace PixelWizard.AvaloniaClient.Input;

/// <summary>
/// Maps Avalonia Key values to Windows Virtual Key codes so keyboard
/// input captured on Mac/Linux can be forwarded to a Windows host.
/// </summary>
public static class KeyMapper
{
    // Avalonia Key enum values are NOT the same as Windows VK codes.
    // This table covers the keys used in real remote-support sessions.
    private static readonly Dictionary<Key, int> _map = new()
    {
        // Control
        { Key.Back,        0x08 }, // VK_BACK
        { Key.Tab,         0x09 }, // VK_TAB
        { Key.Return,      0x0D }, // VK_RETURN
        { Key.Escape,      0x1B }, // VK_ESCAPE
        { Key.Space,       0x20 }, // VK_SPACE
        { Key.Delete,      0x2E }, // VK_DELETE
        { Key.Insert,      0x2D }, // VK_INSERT
        { Key.Home,        0x24 }, // VK_HOME
        { Key.End,         0x23 }, // VK_END
        { Key.PageUp,      0x21 }, // VK_PRIOR
        { Key.PageDown,    0x22 }, // VK_NEXT

        // Arrows
        { Key.Left,        0x25 },
        { Key.Up,          0x26 },
        { Key.Right,       0x27 },
        { Key.Down,        0x28 },

        // Modifiers
        { Key.LeftShift,   0xA0 }, { Key.RightShift,   0xA1 },
        { Key.LeftCtrl,    0xA2 }, { Key.RightCtrl,    0xA3 },
        { Key.LeftAlt,     0xA4 }, { Key.RightAlt,     0xA5 },
        { Key.LWin,        0x5B }, { Key.RWin,         0x5C },

        // Letters A–Z  (VK = 0x41–0x5A)
        { Key.A, 0x41 }, { Key.B, 0x42 }, { Key.C, 0x43 }, { Key.D, 0x44 },
        { Key.E, 0x45 }, { Key.F, 0x46 }, { Key.G, 0x47 }, { Key.H, 0x48 },
        { Key.I, 0x49 }, { Key.J, 0x4A }, { Key.K, 0x4B }, { Key.L, 0x4C },
        { Key.M, 0x4D }, { Key.N, 0x4E }, { Key.O, 0x4F }, { Key.P, 0x50 },
        { Key.Q, 0x51 }, { Key.R, 0x52 }, { Key.S, 0x53 }, { Key.T, 0x54 },
        { Key.U, 0x55 }, { Key.V, 0x56 }, { Key.W, 0x57 }, { Key.X, 0x58 },
        { Key.Y, 0x59 }, { Key.Z, 0x5A },

        // Digits 0–9  (VK = 0x30–0x39)
        { Key.D0, 0x30 }, { Key.D1, 0x31 }, { Key.D2, 0x32 }, { Key.D3, 0x33 },
        { Key.D4, 0x34 }, { Key.D5, 0x35 }, { Key.D6, 0x36 }, { Key.D7, 0x37 },
        { Key.D8, 0x38 }, { Key.D9, 0x39 },

        // Numpad
        { Key.NumPad0, 0x60 }, { Key.NumPad1, 0x61 }, { Key.NumPad2, 0x62 },
        { Key.NumPad3, 0x63 }, { Key.NumPad4, 0x64 }, { Key.NumPad5, 0x65 },
        { Key.NumPad6, 0x66 }, { Key.NumPad7, 0x67 }, { Key.NumPad8, 0x68 },
        { Key.NumPad9, 0x69 },
        { Key.Multiply, 0x6A }, { Key.Add,      0x6B },
        { Key.Subtract, 0x6D }, { Key.Divide,   0x6F },
        { Key.Decimal,  0x6E },

        // Function keys
        { Key.F1,  0x70 }, { Key.F2,  0x71 }, { Key.F3,  0x72 }, { Key.F4,  0x73 },
        { Key.F5,  0x74 }, { Key.F6,  0x75 }, { Key.F7,  0x76 }, { Key.F8,  0x77 },
        { Key.F9,  0x78 }, { Key.F10, 0x79 }, { Key.F11, 0x7A }, { Key.F12, 0x7B },

        // Punctuation
        { Key.OemSemicolon,  0xBA }, // ;:
        { Key.OemPlus,       0xBB }, // =+
        { Key.OemComma,      0xBC }, // ,<
        { Key.OemMinus,      0xBD }, // -_
        { Key.OemPeriod,     0xBE }, // .>
        { Key.OemQuestion,   0xBF }, // /?
        { Key.OemTilde,      0xC0 }, // `~
        { Key.OemOpenBrackets, 0xDB }, // [{
        { Key.OemPipe,       0xDC }, // \|
        { Key.OemCloseBrackets, 0xDD }, // ]}
        { Key.OemQuotes,     0xDE }, // '"
        { Key.PrintScreen,   0x2C },
        { Key.Scroll,        0x91 },
        { Key.Pause,         0x13 },
        { Key.CapsLock,      0x14 },
        { Key.NumLock,       0x90 },
    };

    /// <summary>Returns the Windows VK code, or 0 if not mapped.</summary>
    public static int ToVirtualKey(Key key) =>
        _map.TryGetValue(key, out int vk) ? vk : 0;
}
