using PixelWizard.Protocol;

namespace PixelWizard.Core.Interfaces
{
    public interface IInputInjector
    {
        void MoveMouse(int x, int y);
        void Click(int x, int y, bool leftButton);
        void ButtonDown(int x, int y, bool leftButton);
        void ButtonUp(int x, int y, bool leftButton);
        void SendKey(int virtualKey, bool isKeyDown);

        /// <summary>Scrolls at (x, y). Deltas are in 120ths of a notch; positive is up and right.</summary>
        void Scroll(int x, int y, int deltaX, int deltaY);
    }
}
