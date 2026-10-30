using Avalonia;
using Avalonia.Headless;
using PixelWizard.AvaloniaClient;

[assembly: AvaloniaTestApplication(typeof(PixelWizard.AvaloniaClient.Tests.TestAppBuilder))]

namespace PixelWizard.AvaloniaClient.Tests;

/// <summary>
/// Boots the real <see cref="App"/> (its XAML and styles) on the headless platform. With no
/// desktop lifetime, App creates no MainWindow, tray icon or dialogs. Skia draws, as in the
/// app: the headless drawing stub can't load the Inter font a shown window needs, and with
/// Skia a captured frame has real pixels.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
