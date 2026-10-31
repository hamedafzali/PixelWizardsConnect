using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PixelWizard.AvaloniaClient.Input;
using PixelWizard.AvaloniaClient.ViewModels;

namespace PixelWizard.AvaloniaClient.Views;

public partial class LiveScreenView : UserControl
{
    // The chat panel, the sidebar toggle and pop out belong to the shell, not to the viewer.
    // A button whose command is not set is hidden.
    public static readonly StyledProperty<ChatViewModel?> ChatProperty =
        AvaloniaProperty.Register<LiveScreenView, ChatViewModel?>(nameof(Chat));

    public static readonly StyledProperty<ICommand?> ToggleSidebarCommandProperty =
        AvaloniaProperty.Register<LiveScreenView, ICommand?>(nameof(ToggleSidebarCommand));

    public static readonly StyledProperty<ICommand?> PopOutCommandProperty =
        AvaloniaProperty.Register<LiveScreenView, ICommand?>(nameof(PopOutCommand));

    public ChatViewModel? Chat
    {
        get => GetValue(ChatProperty);
        set => SetValue(ChatProperty, value);
    }

    public ICommand? ToggleSidebarCommand
    {
        get => GetValue(ToggleSidebarCommandProperty);
        set => SetValue(ToggleSidebarCommandProperty, value);
    }

    public ICommand? PopOutCommand
    {
        get => GetValue(PopOutCommandProperty);
        set => SetValue(PopOutCommandProperty, value);
    }

    private ViewerViewModel? _subscribedVm;
    private TopLevel? _keyboardSource;

    public LiveScreenView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedVm != null) _subscribedVm.FrameRendered -= OnFrameRendered;
            _subscribedVm = VM;
            if (_subscribedVm != null) _subscribedVm.FrameRendered += OnFrameRendered;
        };
    }

    private ViewerViewModel? VM => DataContext as ViewerViewModel;

    // Frames are drawn into the same bitmap in place, so the Images must be told to repaint.
    private void OnFrameRendered()
    {
        FitScreenImage.InvalidateVisual();
        ZoomScreenImage.InvalidateVisual();
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    // Keys are taken from the whole window, not just this view, so a key released while
    // focus is elsewhere still reaches the host.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keyboardSource = TopLevel.GetTopLevel(this);
        _keyboardSource?.AddHandler(KeyDownEvent, OnKeyDown);
        _keyboardSource?.AddHandler(KeyUpEvent, OnKeyUp);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyboardSource?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _keyboardSource?.RemoveHandler(KeyUpEvent, OnKeyUp);
        _keyboardSource = null;
        base.OnDetachedFromVisualTree(e);
    }

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
        // With nothing focused, keys go to the window, so a focused chat box doesn't also
        // get the letters meant for the host.
        _keyboardSource?.FocusManager?.ClearFocus();
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
}
