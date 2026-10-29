using Avalonia;
using Avalonia.Fonts.Inter;
using Avalonia.ReactiveUI;
using PixelWizard.AvaloniaClient.Services;

namespace PixelWizard.AvaloniaClient;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        CrashLog.Install();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UsePlatformDetect()
                     .WithInterFont()
                     .UseReactiveUI()
                     .LogToTrace();
}
