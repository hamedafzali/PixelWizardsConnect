using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PixelWizard.AvaloniaClient.Services;

/// <summary>
/// Records an unhandled exception to crash.log in the app-data folder. The client is a WinExe,
/// so without this a startup failure (e.g. a broken XAML build) just makes the window never
/// appear. On Windows it also shows a native message box, which works even when Avalonia
/// itself failed to initialise.
/// </summary>
public static class CrashLog
{
    public static readonly string FilePath = Path.Combine(AppSettings.Dir, "crash.log");

    public static void Install() =>
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject);

    private static void Write(object exception)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.AppendAllText(FilePath, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n{exception}\n\n");
        }
        catch { /* best effort: never mask the original crash */ }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                string type = (exception as Exception)?.GetType().Name ?? "Unknown error";
                MessageBoxW(IntPtr.Zero,
                    $"PixelWizard Connect crashed: {type}\n\nDetails were written to:\n{FilePath}",
                    "PixelWizard Connect", MB_ICONERROR);
            }
            catch { /* best effort */ }
        }
    }

    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
