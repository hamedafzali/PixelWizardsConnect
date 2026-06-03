#if WINDOWS
using PixelWizard.WindowsHost;
#else
using PixelWizard.AvaloniaClient.Platform.Mac;
using PixelWizard.LinuxHost;
#endif

using System;
using PixelWizard.Core.Interfaces;

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

    public IScreenCapture CreateCapture(TimeSpan fullRefreshInterval) =>
        new WindowsScreenCapture(fullRefreshInterval: fullRefreshInterval);

    public IInputInjector CreateInput() =>
        new WindowsInputInjector();
}
#endif

// ── Null / unsupported platform ───────────────────────────────────────────
file sealed class NullHostProvider : IHostProvider
{
    public bool IsAvailable => false;

    public IScreenCapture CreateCapture(TimeSpan _) =>
        throw new PlatformNotSupportedException("Host mode is not yet supported on this platform.");

    public IInputInjector CreateInput() =>
        throw new PlatformNotSupportedException("Host mode is not yet supported on this platform.");
}
