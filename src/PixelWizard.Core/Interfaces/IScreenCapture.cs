using System;
using System.Collections.Generic;
using PixelWizard.Protocol;

namespace PixelWizard.Core.Interfaces
{
    public interface IScreenCapture : IDisposable
    {
        List<ScreenDelta> Capture(bool forceFullFrame, long jpegQuality);
        void Reset();
        (int Width, int Height) Resolution { get; }
    }
}
