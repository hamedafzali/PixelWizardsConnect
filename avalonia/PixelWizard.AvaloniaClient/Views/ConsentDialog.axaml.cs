using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PixelWizard.AvaloniaClient.Views;

public partial class ConsentDialog : Window
{
    public bool? Result { get; private set; }

    public ConsentDialog(string endpoint = "")
    {
        InitializeComponent();
        TimeText.Text     = DateTime.Now.ToString("HH:mm:ss");
        EndpointText.Text = string.IsNullOrEmpty(endpoint) ? "unknown" : endpoint;
    }

    private void OnAllow(object? sender, RoutedEventArgs e) { Result = true;  Close(); }
    private void OnDeny(object? sender, RoutedEventArgs e)  { Result = false; Close(); }
}
