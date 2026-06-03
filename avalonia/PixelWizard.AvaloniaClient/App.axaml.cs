using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using PixelWizard.AvaloniaClient.ViewModels;
using PixelWizard.AvaloniaClient.Views;

namespace PixelWizard.AvaloniaClient;

public class App : Application
{
    private ViewingBadgeWindow? _badgeWindow;
    private MainWindow?         _mainWindow;
    private MainViewModel?      _vm;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _vm     = new MainViewModel();
            _mainWindow = new MainWindow { DataContext = _vm };

            // ── Feature 1 / Clipboard read ────────────────────────────────────
            _vm.ClipboardCallback = async text =>
            {
                var clipboard = TopLevel.GetTopLevel(_mainWindow)?.Clipboard;
                if (clipboard != null) await clipboard.SetTextAsync(text);
            };

            _vm.GetClipboardCallback = async () =>
            {
                var clipboard = TopLevel.GetTopLevel(_mainWindow)?.Clipboard;
                return clipboard == null ? null : await clipboard.GetTextAsync();
            };

            // ── Consent dialog ────────────────────────────────────────────────
            _vm.ConsentCallback = async endpoint =>
            {
                var tcs    = new TaskCompletionSource<bool>();
                var dialog = new ConsentDialog(endpoint);
                dialog.Closed += (_, _) => tcs.TrySetResult(dialog.Result == true);
                dialog.Show();
                return await tcs.Task;
            };

            // ── Feature 7: Tray icon ──────────────────────────────────────────
            var trayIcon = SetupTrayIcon(_mainWindow, _vm);

            // Update tray tooltip when TrayTooltip property changes.
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.TrayTooltip) && trayIcon != null)
                    trayIcon.ToolTipText = _vm.TrayTooltip;
            };

            // Feature 7: hide to tray on close while hosting instead of exiting.
            _mainWindow.Closing += (_, e) =>
            {
                if (_vm.IsHostRunning)
                {
                    e.Cancel = true;
                    _mainWindow.Hide();
                }
            };

            // ── Feature 8: Viewing badge callbacks ────────────────────────────
            _vm.ShowViewingBadgeCallback = () =>
            {
                if (_badgeWindow != null) return;
                _badgeWindow = new ViewingBadgeWindow();

                // Position at top-right of primary screen.
                var screen = _mainWindow.Screens?.Primary;
                if (screen != null)
                {
                    int x = screen.WorkingArea.X + screen.WorkingArea.Width  - 230;
                    int y = screen.WorkingArea.Y + 10;
                    _badgeWindow.Position = new PixelPoint(x, y);
                }

                _badgeWindow.Show();
            };

            _vm.HideViewingBadgeCallback = () =>
            {
                _badgeWindow?.Close();
                _badgeWindow = null;
            };

            desktop.MainWindow = _mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ── Tray icon click handlers (wired from AXAML NativeMenuItem) ────────────

    public void OnTrayShow(object? sender, EventArgs e)
    {
        _mainWindow?.Show();
        _mainWindow?.Activate();
    }

    public void OnTrayQuit(object? sender, EventArgs e)
    {
        _vm?.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
            life.Shutdown();
    }

    // ── Feature 7: Build tray icon programmatically ───────────────────────────

    private static TrayIcon? SetupTrayIcon(MainWindow window, MainViewModel vm)
    {
        try
        {
            var trayIcons = TrayIcon.GetIcons(Current!);
            if (trayIcons == null || trayIcons.Count == 0) return null;

            var trayIcon = trayIcons[0];
            trayIcon.Icon = MakeTrayIcon();

            return trayIcon;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Generates a 16×16 blue-square PNG WindowIcon using SkiaSharp.</summary>
    private static WindowIcon? MakeTrayIcon()
    {
        try
        {
            using var bmp = new SKBitmap(16, 16);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(37, 99, 235), IsAntialias = true };
            canvas.DrawRoundRect(1, 1, 14, 14, 3, 3, paint);
            using var stream = new MemoryStream();
            bmp.Encode(stream, SKEncodedImageFormat.Png, 100);
            stream.Position = 0;
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }
}
