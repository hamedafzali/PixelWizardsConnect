using System;

namespace PixelWizard.Core.Interfaces
{
    public interface IHostProvider
    {
        bool IsAvailable { get; }
        IScreenCapture CreateCapture(TimeSpan fullRefreshInterval);
        IInputInjector CreateInput();
    }
}
