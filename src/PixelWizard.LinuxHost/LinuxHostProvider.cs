using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using PixelWizard.Core.Interfaces;
using PixelWizard.Core.Models;

namespace PixelWizard.LinuxHost;

[SupportedOSPlatform("linux")]
public sealed class LinuxHostProvider : IHostProvider
{
    public bool IsAvailable => OperatingSystem.IsLinux();

    public IReadOnlyList<MonitorInfo> ListMonitors()
    {
        // Linux single-monitor stub: resolution is queried lazily from X11.
        // Return a placeholder; the ViewModel will refresh after capture starts.
        return new List<MonitorInfo>
        {
            new MonitorInfo(0, "Display 1", 1920, 1080, true)
        };
    }

    public IScreenCapture CreateCapture(TimeSpan fullRefreshInterval, int monitorIndex = 0) =>
        new LinuxScreenCapture(fullRefreshInterval);   // monitorIndex ignored on Linux for now

    public IInputInjector CreateInput() =>
        new LinuxInputInjector();
}
