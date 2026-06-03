using System;
using System.Runtime.Versioning;
using PixelWizard.Core.Interfaces;

namespace PixelWizard.LinuxHost;

[SupportedOSPlatform("linux")]
public sealed class LinuxHostProvider : IHostProvider
{
    public bool IsAvailable => OperatingSystem.IsLinux();

    public IScreenCapture CreateCapture(TimeSpan fullRefreshInterval) =>
        new LinuxScreenCapture(fullRefreshInterval);

    public IInputInjector CreateInput() =>
        new LinuxInputInjector();
}
