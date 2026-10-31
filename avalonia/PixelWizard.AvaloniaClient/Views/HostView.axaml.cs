using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace PixelWizard.AvaloniaClient.Views;

public partial class HostView : UserControl
{
    // Back leaves the host panel for mode selection, which the shell owns.
    public static readonly StyledProperty<ICommand?> BackCommandProperty =
        AvaloniaProperty.Register<HostView, ICommand?>(nameof(BackCommand));

    public ICommand? BackCommand
    {
        get => GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    public HostView()
    {
        InitializeComponent();
    }
}
