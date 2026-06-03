using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using PixelWizard.Core.Interfaces;
using PixelWizard.Core.Models;

namespace PixelWizard.AvaloniaClient.Platform.Mac;

[SupportedOSPlatform("osx")]
internal sealed class MacHostProvider : IHostProvider
{
    public bool IsAvailable => OperatingSystem.IsMacOS();

    public IReadOnlyList<MonitorInfo> ListMonitors()
    {
        // Query count first
        MacScreenCapture.CGGetActiveDisplayList(0, null, out uint count);
        if (count == 0) count = 1;

        var ids = new uint[count];
        MacScreenCapture.CGGetActiveDisplayList(count, ids, out uint actual);

        var result = new List<MonitorInfo>((int)actual);
        for (int i = 0; i < (int)actual; i++)
        {
            uint id      = ids[i];
            var  bounds  = MacScreenCapture.CGDisplayBounds(id);
            bool primary = MacScreenCapture.CGDisplayIsMain(id);
            result.Add(new MonitorInfo(
                Index     : i,
                Name      : primary ? $"Display {i + 1} (Primary)" : $"Display {i + 1}",
                Width     : (int)bounds.Size.Width,
                Height    : (int)bounds.Size.Height,
                IsPrimary : primary));
        }
        return result;
    }

    public IScreenCapture CreateCapture(TimeSpan fullRefreshInterval, int monitorIndex = 0)
    {
        uint displayId = GetDisplayId(monitorIndex);
        return new MacScreenCapture(fullRefreshInterval, displayId);
    }

    public IInputInjector CreateInput() =>
        new MacInputInjector();

    private static uint GetDisplayId(int monitorIndex)
    {
        MacScreenCapture.CGGetActiveDisplayList(0, null, out uint count);
        if (count == 0 || monitorIndex < 0) return MacScreenCapture.CGMainDisplayID();

        var ids = new uint[count];
        MacScreenCapture.CGGetActiveDisplayList(count, ids, out uint actual);

        if (monitorIndex >= (int)actual) return ids[0];
        return ids[monitorIndex];
    }
}
