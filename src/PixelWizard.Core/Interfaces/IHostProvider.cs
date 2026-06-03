using System;
using System.Collections.Generic;
using PixelWizard.Core.Models;

namespace PixelWizard.Core.Interfaces
{
    public interface IHostProvider
    {
        bool IsAvailable { get; }

        /// <summary>Returns all monitors available on this machine.</summary>
        IReadOnlyList<MonitorInfo> ListMonitors();

        /// <summary>Creates a capture for the given monitor index (0 = primary).</summary>
        IScreenCapture CreateCapture(TimeSpan fullRefreshInterval, int monitorIndex = 0);

        IInputInjector CreateInput();
    }
}
