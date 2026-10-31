using System.Reactive;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PixelWizard.AvaloniaClient.ViewModels;

namespace PixelWizard.AvaloniaClient.Views;

public partial class ViewerConnectView : UserControl
{
    // Back leaves the viewer for mode selection, which the shell owns.
    public static readonly StyledProperty<ICommand?> BackCommandProperty =
        AvaloniaProperty.Register<ViewerConnectView, ICommand?>(nameof(BackCommand));

    public ICommand? BackCommand
    {
        get => GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    public ViewerConnectView()
    {
        InitializeComponent();
    }

    private ViewerViewModel? VM => DataContext as ViewerViewModel;

    // ── Enter key handlers for text inputs ───────────────────────────────────

    private void OnCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) { VM?.ConnectViaCodeCommand.Execute(Unit.Default); e.Handled = true; }
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) { VM?.ConnectDirectCommand.Execute(Unit.Default); e.Handled = true; }
    }
}
