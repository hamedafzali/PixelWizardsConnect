using PixelWizard.Core.Protocol;

namespace PixelWizard.Core.Interfaces
{
    public interface IInputInjector
    {
        void MoveMouse(int x, int y);
        void Click(int x, int y, bool leftButton);
        void ButtonDown(int x, int y, bool leftButton);
        void ButtonUp(int x, int y, bool leftButton);
        void SendKey(int virtualKey, bool isKeyDown);
    }
}
