using System.Runtime.InteropServices;
using System.Windows.Forms;
using PixelWizard.Core.Interfaces;

namespace PixelWizard.WindowsHost
{
    public class WindowsInputInjector : IInputInjector
    {
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint buttons, uint extra);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, uint extra);

        private const uint LEFTDOWN = 0x02, LEFTUP = 0x04;
        private const uint RIGHTDOWN = 0x08, RIGHTUP = 0x10;
        private const uint KEYUP = 0x0002;

        public void MoveMouse(int x, int y)
        {
            Cursor.Position = new System.Drawing.Point(x, y);
        }

        public void Click(int x, int y, bool leftButton)
        {
            Cursor.Position = new System.Drawing.Point(x, y);
            if (leftButton)
            {
                mouse_event(LEFTDOWN, 0, 0, 0, 0);
                mouse_event(LEFTUP, 0, 0, 0, 0);
            }
            else
            {
                mouse_event(RIGHTDOWN, 0, 0, 0, 0);
                mouse_event(RIGHTUP, 0, 0, 0, 0);
            }
        }

        public void ButtonDown(int x, int y, bool leftButton)
        {
            Cursor.Position = new System.Drawing.Point(x, y);
            mouse_event(leftButton ? LEFTDOWN : RIGHTDOWN, 0, 0, 0, 0);
        }

        public void ButtonUp(int x, int y, bool leftButton)
        {
            Cursor.Position = new System.Drawing.Point(x, y);
            mouse_event(leftButton ? LEFTUP : RIGHTUP, 0, 0, 0, 0);
        }

        public void SendKey(int virtualKey, bool isKeyDown)
        {
            if (isKeyDown)
                keybd_event((byte)virtualKey, 0, 0, 0);
            else
                keybd_event((byte)virtualKey, 0, KEYUP, 0);
        }
    }
}
