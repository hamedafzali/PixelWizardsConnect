using Avalonia;
using Avalonia.Fonts.Inter;
using Avalonia.ReactiveUI;

namespace PixelWizard.AvaloniaClient;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UsePlatformDetect()
                     .WithInterFont()
                     .UseReactiveUI()
                     .LogToTrace();
}
