using Avalonia;
using Avalonia.Headless;
using PixelWizard.AvaloniaClient;

[assembly: AvaloniaTestApplication(typeof(PixelWizard.AvaloniaClient.Tests.TestAppBuilder))]

namespace PixelWizard.AvaloniaClient.Tests;

/// <summary>
/// Boots the real <see cref="App"/> (its XAML and styles) on the headless platform. With no
/// desktop lifetime, App creates no MainWindow, tray icon or dialogs.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
