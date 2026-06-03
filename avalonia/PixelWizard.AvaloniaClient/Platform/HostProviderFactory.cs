#if WINDOWS
using PixelWizard.WindowsHost;
#else
using PixelWizard.AvaloniaClient.Platform.Mac;
using PixelWizard.LinuxHost;
#endif

using System;
using System.Collections.Generic;
using PixelWizard.Core.Interfaces;
using PixelWizard.Core.Models;

namespace PixelWizard.AvaloniaClient.Platform;

public static class HostProviderFactory
{
    public static IHostProvider Create()
    {
#if WINDOWS
        return new WindowsHostProvider();
#else
        if (OperatingSystem.IsMacOS())
            return new MacHostProvider();
        if (OperatingSystem.IsLinux())
            return new LinuxHostProvider();
        return new NullHostProvider();
#endif
    }
}

// ── Windows implementation (net9.0-windows only) ──────────────────────────
#if WINDOWS
file sealed class WindowsHostProvider : IHostProvider
{
    public bool IsAvailable => true;

    public IReadOnlyList<MonitorInfo> ListMonitors()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var list    = new List<MonitorInfo>(screens.Length);
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            list.Add(new MonitorInfo(
                Index     : i,
                Name      : string.IsNullOrWhiteSpace(s.DeviceName) ? $"Display {i + 1}" : s.DeviceName,
                Width     : s.Bounds.Width,
                Height    : s.Bounds.Height,
                IsPrimary : s.Primary));
        }
        return list;
    }

    public IScreenCapture CreateCapture(TimeSpan fullRefreshInterval, int monitorIndex = 0)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen  = (monitorIndex >= 0 && monitorIndex < screens.Length)
                      ? screens[monitorIndex]
                      : System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
        return new WindowsScreenCapture(screen: screen, fullRefreshInterval: fullRefreshInterval);
    }

    public IInputInjector CreateInput() =>
        new WindowsInputInjector();
}
#endif

// ── Null / unsupported platform ───────────────────────────────────────────
file sealed class NullHostProvider : IHostProvider
{
    public bool IsAvailable => false;

    public IReadOnlyList<MonitorInfo> ListMonitors() =>
        new List<MonitorInfo> { new MonitorInfo(0, "Display 1", 1920, 1080, true) };

    public IScreenCapture CreateCapture(TimeSpan _, int monitorIndex = 0) =>
        throw new PlatformNotSupportedException("Host mode is not yet supported on this platform.");

    public IInputInjector CreateInput() =>
        throw new PlatformNotSupportedException("Host mode is not yet supported on this platform.");
}
