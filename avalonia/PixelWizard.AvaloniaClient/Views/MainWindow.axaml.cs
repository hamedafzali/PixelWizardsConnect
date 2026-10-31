using System;
using System.ComponentModel;
using Avalonia.Controls;
using PixelWizard.AvaloniaClient.ViewModels;

namespace PixelWizard.AvaloniaClient.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as MainViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
            SyncScreenWindow();
        };
    }

    // ── Remote screen in its own window ──────────────────────────────────────

    /// <summary>The popped-out remote screen, while it is open.</summary>
    public RemoteScreenWindow? ScreenWindow { get; private set; }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowScreenWindow)) SyncScreenWindow();
    }

    // Opened on pop out, closed when the session ends or the screen is brought back.
    private void SyncScreenWindow()
    {
        bool show = _vm?.ShowScreenWindow == true;
        if (show && ScreenWindow == null)
        {
            ScreenWindow = new RemoteScreenWindow { DataContext = _vm };
            ScreenWindow.Closed += OnScreenWindowClosedByUser;
            ScreenWindow.Show();
        }
        else if (!show && ScreenWindow != null)
        {
            CloseScreenWindow();
        }
    }

    private void CloseScreenWindow()
    {
        var window = ScreenWindow!;
        ScreenWindow = null;
        window.Closed -= OnScreenWindowClosedByUser;
        window.Close();
    }

    // Closing the window brings the screen back here; the session carries on.
    private void OnScreenWindowClosedByUser(object? sender, EventArgs e)
    {
        ScreenWindow = null;
        if (_vm != null) _vm.ScreenInOwnWindow = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        // An open screen window would otherwise keep the app running.
        if (ScreenWindow != null) CloseScreenWindow();
        (DataContext as MainViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
