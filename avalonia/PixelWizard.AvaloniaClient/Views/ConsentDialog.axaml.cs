using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PixelWizard.AvaloniaClient.Views;

public partial class ConsentDialog : Window
{
    public ConsentDialog(string endpoint = "")
    {
        InitializeComponent();
        TimeText.Text     = DateTime.Now.ToString("HH:mm:ss");
        EndpointText.Text = string.IsNullOrEmpty(endpoint) ? "unknown" : endpoint;
    }

    private void OnAllow(object? sender, RoutedEventArgs e) => Close(true);
    private void OnDeny(object? sender, RoutedEventArgs e)  => Close(false);
}
