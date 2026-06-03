using System;
using System.Reactive;
using Avalonia.Controls;
using Avalonia.Input;
using PixelWizard.AvaloniaClient.Input;
using PixelWizard.AvaloniaClient.ViewModels;

namespace PixelWizard.AvaloniaClient.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    // ── Keyboard ─────────────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = VM;
        if (vm?.KeyboardActive != true) return;
        int vk = KeyMapper.ToVirtualKey(e.Key);
        if (vk == 0) return;
        e.Handled = true;
        vm.SendKey(vk, isDown: true);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var vm = VM;
        if (vm?.IsConnected != true) return;
        int vk = KeyMapper.ToVirtualKey(e.Key);
        if (vk == 0) return;
        e.Handled = true;
        vm.SendKey(vk, isDown: false);
    }

    // ── Enter key handlers for text inputs ───────────────────────────────────

    private void OnCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) { VM?.ConnectViaCodeCommand.Execute(Unit.Default); e.Handled = true; }
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) { VM?.ConnectDirectCommand.Execute(Unit.Default); e.Handled = true; }
    }

    // ── Mouse ─────────────────────────────────────────────────────────────────

    private void OnScreenPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var vm = VM;
        if (vm?.IsConnected != true || vm.RemoteScreen == null) return;
        var (rx, ry) = ToRemote(sender as Image, e.GetPosition((Image?)sender));
        bool left = e.GetCurrentPoint((Image?)sender).Properties.IsLeftButtonPressed;
        vm.SendMouseDown(rx, ry, leftButton: left);
        e.Handled = true;
    }

    private void OnScreenPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var vm = VM;
        if (vm?.IsConnected != true || vm.RemoteScreen == null) return;
        var (rx, ry) = ToRemote(sender as Image, e.GetPosition((Image?)sender));
        bool left = e.InitialPressMouseButton == MouseButton.Left;
        vm.SendMouseUp(rx, ry, leftButton: left);
    }

    private void OnScreenPointerMoved(object? sender, PointerEventArgs e)
    {
        var vm = VM;
        if (vm?.IsConnected != true || vm.RemoteScreen == null) return;
        var (rx, ry) = ToRemote(sender as Image, e.GetPosition((Image?)sender));
        vm.SendMouseMove(rx, ry);
    }

    private void OnScreenPointerEntered(object? sender, PointerEventArgs e)
    {
        if (VM is { } vm) vm.KeyboardActive = true;
        Focus();
    }

    private void OnScreenPointerExited(object? sender, PointerEventArgs e)
    {
        if (VM is { } vm) vm.KeyboardActive = false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (int x, int y) ToRemote(Image? img, Avalonia.Point pos)
    {
        if (img?.Source == null || img.Bounds.Width <= 0 || img.Bounds.Height <= 0)
            return (0, 0);

        var src = img.Source;
        double srcW = src.Size.Width;
        double srcH = src.Size.Height;

        // Uniform stretch: find the rendered image area inside the Image control
        double ctrlW = img.Bounds.Width;
        double ctrlH = img.Bounds.Height;
        double scale = Math.Min(ctrlW / srcW, ctrlH / srcH);
        double rendW = srcW * scale;
        double rendH = srcH * scale;
        double offX  = (ctrlW - rendW) / 2;
        double offY  = (ctrlH - rendH) / 2;

        int rx = (int)Math.Clamp((pos.X - offX) / scale, 0, srcW - 1);
        int ry = (int)Math.Clamp((pos.Y - offY) / scale, 0, srcH - 1);
        return (rx, ry);
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as MainViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
