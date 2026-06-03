using System;
using PixelWizard.Core.Interfaces;

namespace PixelWizard.AvaloniaClient.Platform;

/// <summary>
/// Abstracts platform-specific screen capture and input injection so the
/// shared ViewModel doesn't reference Windows-only types directly.
/// </summary>
public interface IHostProvider
{
    bool IsAvailable { get; }
    IScreenCapture CreateCapture(TimeSpan fullRefreshInterval);
    IInputInjector CreateInput();
}
