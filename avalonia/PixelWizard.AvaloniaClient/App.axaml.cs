using System.Threading.Tasks;
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

            // Show the consent dialog as an independent topmost window so the main
            // app window is NOT pulled to the foreground when a viewer connects.
            vm.ConsentCallback = async endpoint =>
            {
                var tcs    = new TaskCompletionSource<bool>();
                var dialog = new ConsentDialog(endpoint);
                dialog.Closed += (_, _) => tcs.TrySetResult(dialog.Result == true);
                dialog.Show();
                return await tcs.Task;
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
