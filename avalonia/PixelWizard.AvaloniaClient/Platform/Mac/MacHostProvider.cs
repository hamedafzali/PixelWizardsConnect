using System;
using System.Runtime.Versioning;
using PixelWizard.Core.Interfaces;

namespace PixelWizard.AvaloniaClient.Platform.Mac;

[SupportedOSPlatform("osx")]
internal sealed class MacHostProvider : IHostProvider
{
    public bool IsAvailable => OperatingSystem.IsMacOS();

    public IScreenCapture CreateCapture(TimeSpan fullRefreshInterval) =>
        new MacScreenCapture(fullRefreshInterval);

    public IInputInjector CreateInput() =>
        new MacInputInjector();
}
