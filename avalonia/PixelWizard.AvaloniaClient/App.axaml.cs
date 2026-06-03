using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PixelWizard.AvaloniaClient.ViewModels;
using PixelWizard.AvaloniaClient.Views;

namespace PixelWizard.AvaloniaClient;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm     = new MainViewModel();
            var window = new MainWindow { DataContext = vm };

            // Wire consent dialog — runs on UI thread via ShowDialog
            vm.ConsentCallback = async endpoint =>
            {
                var dialog = new ConsentDialog(endpoint);
                return await dialog.ShowDialog<bool>(window);
            };

            vm.ClipboardCallback = async text =>
            {
                var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
                if (clipboard != null) await clipboard.SetTextAsync(text);
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
