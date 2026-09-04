using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace PixelWizard.WindowsHost
{
    /// <summary>
    /// Monitor bounds/primary/device-name info, resolved via EnumDisplayMonitors +
    /// GetMonitorInfo (user32.dll) instead of System.Windows.Forms.Screen. This is the
    /// T8 replacement that lets PixelWizard.WindowsHost drop UseWindowsForms -- and with
    /// it, the Microsoft.WindowsDesktop.App dependency that made this project's test host
    /// unable to launch at all on non-Windows CI runners.
    /// </summary>
    public readonly record struct WindowsMonitorInfo(Rectangle Bounds, bool IsPrimary, string DeviceName);

    public static class WindowsMonitors
    {
        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private const uint MONITORINFOF_PRIMARY = 0x1;

        /// <summary>All active monitors. Never empty on a machine with a display attached.</summary>
        public static IReadOnlyList<WindowsMonitorInfo> List()
        {
            var monitors = new List<WindowsMonitorInfo>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT rect, IntPtr __) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    monitors.Add(new WindowsMonitorInfo(
                        Bounds: Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                        IsPrimary: (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                        DeviceName: mi.szDevice));
                }
                return true;
            }, IntPtr.Zero);
            return monitors;
        }

        /// <summary>The primary monitor, or the first enumerated one if none is flagged primary.</summary>
        public static WindowsMonitorInfo Primary()
        {
            var all = List();
            foreach (var m in all)
                if (m.IsPrimary) return m;
            return all[0];
        }
    }
}
