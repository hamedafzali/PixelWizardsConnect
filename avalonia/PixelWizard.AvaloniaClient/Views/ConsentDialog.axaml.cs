using System;
using Avalonia.Controls;
using Avalonia.Input;
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

        // Escape key dismisses as Deny
        KeyDown += (_, e) => { if (e.Key == Key.Escape) OnDeny(null, null!); };
    }

    private void OnAllow(object? sender, RoutedEventArgs e) { Result = true;  Close(); }
    private void OnDeny(object? sender, RoutedEventArgs e)  { Result = false; Close(); }
}
