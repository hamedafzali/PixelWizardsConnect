using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PixelWizard.AvaloniaClient.Views;

/// <summary>
/// Shown when a previously pinned host presents a different TLS certificate. Like
/// <see cref="ConsentDialog"/>, only an explicit click trusts: Escape, Enter (the default
/// button) and closing the window all refuse, leaving the recorded pin in place.
/// </summary>
public partial class PinMismatchDialog : Window
{
    public bool? Result { get; private set; }

    public PinMismatchDialog() : this("", "", "") { }

    public PinMismatchDialog(string key, string expectedFingerprint, string actualFingerprint)
    {
        InitializeComponent();
        KeyText.Text      = key;
        ExpectedText.Text = Group(expectedFingerprint);
        ActualText.Text   = Group(actualFingerprint);

        KeyDown += (_, e) => { if (e.Key == Key.Escape) OnRefuse(null, null!); };
        Opened  += (_, _) => RefuseButton.Focus();
    }

    // "AB12CD34…" -> "AB:12:CD:34…", the usual way fingerprints are compared by eye.
    private static string Group(string hex)
    {
        var sb = new StringBuilder(hex.Length * 3 / 2);
        for (int i = 0; i < hex.Length; i += 2)
        {
            if (i > 0) sb.Append(':');
            sb.Append(hex, i, System.Math.Min(2, hex.Length - i));
        }
        return sb.ToString();
    }

    private void OnTrust(object? sender, RoutedEventArgs e)  { Result = true;  Close(); }
    private void OnRefuse(object? sender, RoutedEventArgs e) { Result = false; Close(); }
}
