using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PixelWizard.AvaloniaClient.Services;
using PixelWizard.AvaloniaClient.ViewModels;
using PixelWizard.AvaloniaClient.Views;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.AvaloniaClient.Tests;

// The real MainWindow, driven by headless input, connected to a real HostSession on
// localhost. These pin what the window's code-behind does today (Enter connects, pointer
// and keys reach the host, frames repaint) so moving it into its own views can't lose it.
// Controls are found by automation name or Name, not by where they sit in the XAML.
public sealed class MainWindowInputTests : IDisposable
{
    private static readonly HelloMessage OurHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };

    // 4:3 in a wider screen area, so Fit mode letterboxes and the mapping has an offset.
    private const int RemoteW = 160, RemoteH = 120;

    private readonly string _settingsFile =
        Path.Combine(Path.GetTempPath(), "pw-window-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly MainViewModel _vm;
    private readonly MainWindow _window;
    private readonly HostSession _host = new(new TcpTransport(), OurHello);
    private readonly int _port = GetFreePort();
    private bool _verified;

    private readonly List<(int X, int Y)> _moves = new();
    private readonly List<(int X, int Y, bool Left)> _downs = new();
    private readonly List<(int X, int Y, bool Left)> _ups = new();
    private readonly List<int> _keyDowns = new();
    private readonly List<int> _keyUps = new();

    public MainWindowInputTests()
    {
        _vm = new MainViewModel(AppSettings.Load(_settingsFile)) { ShowOnboarding = false };
        _window = new MainWindow { DataContext = _vm, Width = 1280, Height = 860 };
        _window.Show();

        _host.HandshakeVerified += () => _verified = true;
        _host.MouseMoveReceived += m => { lock (_moves) _moves.Add((m.X, m.Y)); };
        _host.MouseButtonDownReceived += m => { lock (_downs) _downs.Add((m.X, m.Y, m.LeftButton)); };
        _host.MouseButtonUpReceived += m => { lock (_ups) _ups.Add((m.X, m.Y, m.LeftButton)); };
        _host.KeyPressReceived += k => { lock (_keyDowns) _keyDowns.Add(k.VirtualKey); };
        _host.KeyReleaseReceived += k => { lock (_keyUps) _keyUps.Add(k.VirtualKey); };
        _ = _host.StartServerAsync(_port, useTls: false);
    }

    public void Dispose()
    {
        _window.Close(); // disposes the MainViewModel
        _host.Dispose();
        File.Delete(_settingsFile);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // Posted UI work runs while this waits on the headless UI thread.
    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    // Runs posted work, lays out and renders: a panel that just became visible then has its
    // controls, and pointer hit-testing (which uses the rendered composition tree) finds them.
    private void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        _window.UpdateLayout();
        _window.CaptureRenderedFrame()?.Dispose();
    }

    private T Find<T>(Func<T, bool> match) where T : Control =>
        _window.GetVisualDescendants().OfType<T>().Single(match);

    private TextBox TextBoxNamed(string automationName) =>
        Find<TextBox>(t => AutomationProperties.GetName(t) == automationName);

    private Image ScreenImage(string name) => Find<Image>(i => i.Name == name);

    // Press only, like a user whose Return is still down as the connection goes live; the
    // key-up test releases it later.
    private void PressEnterIn(TextBox box)
    {
        box.Focus();
        _window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    }

    // Live on the remote screen with the host approved and one solid frame shown.
    private async Task GoLive()
    {
        await Task.Delay(50); // let the listener bind
        _vm.GoViewerCommand.Execute().Subscribe();
        Settle();
        _vm.Viewer.UseTls = false;
        var address = TextBoxNamed("Remote host IP address");
        address.Text = $"127.0.0.1:{_port}";
        PressEnterIn(address);
        await WaitUntil(() => _vm.Viewer.IsConnected && _vm.ShowScreenDisplay, "viewer to go live");
        await WaitUntil(() => _verified, "host to verify the handshake");
        _host.Approve();
        await _host.SendFrameAsync(new ScreenDelta { ImageData = SolidPng(RemoteW, RemoteH, Colors.Red) }, full: true);
        await WaitUntil(() => _vm.Viewer.RemoteScreen != null, "first frame");
        Settle();
    }

    private static byte[] SolidPng(int w, int h, Color c)
    {
        using var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            var row = new byte[w * 4];
            for (int x = 0; x < w; x++) { row[x * 4] = c.B; row[x * 4 + 1] = c.G; row[x * 4 + 2] = c.R; row[x * 4 + 3] = c.A; }
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms);
        return ms.ToArray();
    }

    // Window position of the centre of remote pixel (rx, ry) in an Image showing the
    // remote screen with uniform stretch.
    private Point WindowPointOf(Image img, int rx, int ry)
    {
        double scale = Math.Min(img.Bounds.Width / RemoteW, img.Bounds.Height / RemoteH);
        double offX = (img.Bounds.Width - RemoteW * scale) / 2;
        double offY = (img.Bounds.Height - RemoteH * scale) / 2;
        var local = new Point(offX + (rx + 0.5) * scale, offY + (ry + 0.5) * scale);
        return img.TranslatePoint(local, _window) ?? throw new InvalidOperationException("image not in window");
    }

    private Color PixelAtCentreOf(Image img)
    {
        using var frame = _window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        var centre = img.TranslatePoint(new Point(img.Bounds.Width / 2, img.Bounds.Height / 2), _window)!.Value;
        var px = new byte[4];
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            frame.CopyPixels(new PixelRect((int)centre.X, (int)centre.Y, 1, 1), buffer, 4, 4);
            System.Runtime.InteropServices.Marshal.Copy(buffer, px, 0, 4);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
        return frame.Format == PixelFormat.Rgba8888
            ? Color.FromArgb(px[3], px[0], px[1], px[2])
            : Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    // ── Enter in the connect form ─────────────────────────────────────────────

    [AvaloniaFact]
    public async Task EnterInTheAddressBox_Connects()
    {
        await GoLive(); // connects by pressing Enter in the address box
        Assert.Equal("Connected", _vm.Status);
    }

    [AvaloniaFact]
    public async Task EnterInTheCodeBox_RunsConnectViaCode()
    {
        _vm.GoViewerCommand.Execute().Subscribe();
        Settle();
        PressEnterIn(TextBoxNamed("Connection code")); // empty code: ConnectViaCode refuses
        await WaitUntil(() => _vm.Status == "Enter a connection code", "connect-via-code to run");
        Assert.False(_vm.Viewer.IsConnecting);
    }

    // ── Pointer over the remote screen ────────────────────────────────────────

    [AvaloniaFact]
    public async Task PointerOverTheFitScreen_ReachesTheHostInRemotePixels()
    {
        await GoLive();
        Assert.True(_vm.Viewer.IsFitMode);
        var img = ScreenImage("FitScreenImage");

        _window.MouseMove(WindowPointOf(img, 150, 25), RawInputModifiers.None);
        await WaitUntil(() => { lock (_moves) return _moves.Contains((150, 25)); }, "move on host");

        _window.MouseDown(WindowPointOf(img, 40, 80), MouseButton.Left, RawInputModifiers.None);
        _window.MouseUp(WindowPointOf(img, 40, 80), MouseButton.Left, RawInputModifiers.None);
        await WaitUntil(() => { lock (_ups) return _ups.Count == 1; }, "button up on host");
        lock (_downs) Assert.Equal(new[] { (40, 80, true) }, _downs);
        lock (_ups) Assert.Equal(new[] { (40, 80, true) }, _ups);

        _window.MouseDown(WindowPointOf(img, 10, 10), MouseButton.Right, RawInputModifiers.None);
        _window.MouseUp(WindowPointOf(img, 10, 10), MouseButton.Right, RawInputModifiers.None);
        await WaitUntil(() => { lock (_ups) return _ups.Count == 2; }, "right button up on host");
        lock (_downs) Assert.Equal((10, 10, false), _downs[1]);
        lock (_ups) Assert.Equal((10, 10, false), _ups[1]);
    }

    [AvaloniaFact]
    public async Task PointerOverTheZoomedScreen_ReachesTheHostInRemotePixels()
    {
        await GoLive();
        await _vm.Viewer.Zoom100Command.Execute();
        Settle();
        Assert.False(_vm.Viewer.IsFitMode);
        var img = ScreenImage("ZoomScreenImage");

        _window.MouseMove(WindowPointOf(img, 120, 60), RawInputModifiers.None);
        await WaitUntil(() => { lock (_moves) return _moves.Contains((120, 60)); }, "move on host");

        _window.MouseDown(WindowPointOf(img, 5, 95), MouseButton.Left, RawInputModifiers.None);
        _window.MouseUp(WindowPointOf(img, 5, 95), MouseButton.Left, RawInputModifiers.None);
        await WaitUntil(() => { lock (_ups) return _ups.Count == 1; }, "button up on host");
        lock (_downs) Assert.Equal(new[] { (5, 95, true) }, _downs);
        lock (_ups) Assert.Equal(new[] { (5, 95, true) }, _ups);
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task KeyDowns_ReachTheHostOnlyWhileThePointerIsOverTheScreen()
    {
        await GoLive();
        var img = ScreenImage("FitScreenImage");
        var outside = img.TranslatePoint(new Point(0, -20), _window)!.Value; // the toolbar

        _window.MouseMove(outside, RawInputModifiers.None);
        Assert.False(_vm.Viewer.KeyboardActive);
        _window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);

        _window.MouseMove(WindowPointOf(img, 100, 50), RawInputModifiers.None);
        Assert.True(_vm.Viewer.KeyboardActive);
        _window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
        await WaitUntil(() => { lock (_keyDowns) return _keyDowns.Count > 0; }, "key down on host");
        lock (_keyDowns) Assert.Equal(new[] { 0x42 }, _keyDowns); // B only; TCP keeps order

        _window.MouseMove(outside, RawInputModifiers.None);
        Assert.False(_vm.Viewer.KeyboardActive);
        _window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
        _window.MouseMove(WindowPointOf(img, 100, 50), RawInputModifiers.None);
        _window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.None);
        await WaitUntil(() => { lock (_keyDowns) return _keyDowns.Count > 1; }, "second key down on host");
        lock (_keyDowns) Assert.Equal(new[] { 0x42, 0x44 }, _keyDowns);
    }

    // A key pressed over the screen is released on the host even if the pointer has left by
    // then. A key-up whose key-down the host never saw, such as the release of the Return
    // that connected, is not sent.
    [AvaloniaFact]
    public async Task KeyUps_ReachTheHostOnlyForKeysItSawGoDown()
    {
        await GoLive();
        var img = ScreenImage("FitScreenImage");
        var outside = img.TranslatePoint(new Point(0, -20), _window)!.Value; // the toolbar

        _window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        _window.MouseMove(WindowPointOf(img, 100, 50), RawInputModifiers.None);
        _window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        _window.MouseMove(outside, RawInputModifiers.None);
        Assert.False(_vm.Viewer.KeyboardActive);
        _window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.None);
        _window.KeyReleaseQwerty(PhysicalKey.B, RawInputModifiers.None);

        _window.MouseMove(WindowPointOf(img, 100, 50), RawInputModifiers.None);
        _window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
        _window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.None);
        await WaitUntil(() => { lock (_keyUps) return _keyUps.Contains(0x43); }, "last key up on host");
        lock (_keyUps) Assert.Equal(new[] { 0x41, 0x43 }, _keyUps); // TCP keeps order
    }

    // Keys typed over the screen go to the host; if the chat box kept focus they would
    // also be typed into chat.
    [AvaloniaFact]
    public async Task HoveringTheScreen_TakesFocusFromTheChatBox()
    {
        await GoLive();
        _vm.Chat.ToggleCommand.Execute().Subscribe();
        Settle();
        var chat = TextBoxNamed("Chat message");
        chat.Focus();
        Assert.Same(chat, _window.FocusManager?.GetFocusedElement());

        _window.MouseMove(WindowPointOf(ScreenImage("FitScreenImage"), 100, 50), RawInputModifiers.None);
        Assert.True(_vm.Viewer.KeyboardActive);
        Assert.NotSame(chat, _window.FocusManager?.GetFocusedElement());

        _window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
        _window.KeyTextInput("b");
        await WaitUntil(() => { lock (_keyDowns) return _keyDowns.Count > 0; }, "key down on host");
        Assert.True(string.IsNullOrEmpty(_vm.Chat.Input));
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    // Back ends the session; its Disconnected event arrives afterwards and must not pull the
    // window back to the Viewer screen.
    [AvaloniaFact]
    public async Task BackFromTheLiveScreen_StaysOnModeSelection()
    {
        bool hostSawDrop = false;
        _host.Disconnected += () => hostSawDrop = true;
        await GoLive();

        _vm.BackCommand.Execute().Subscribe();
        await WaitUntil(() => hostSawDrop, "host to see the viewer leave");
        await Task.Delay(100);
        Settle();

        Assert.True(_vm.ShowModeSelection);
        Assert.False(_vm.ShowViewerPanel);
        Assert.NotEqual("Disconnected", _vm.Status);
    }

    // ── Repaint ───────────────────────────────────────────────────────────────

    // A delta is drawn into the same bitmap in place, so the screen shows it only if the
    // Image is told to repaint.
    [AvaloniaFact]
    public async Task AFrameDrawnInPlace_RepaintsTheScreen()
    {
        await GoLive();
        var img = ScreenImage("FitScreenImage");
        // A capture can show the frame before last, so wait rather than check once.
        await WaitUntil(() => PixelAtCentreOf(img) == Colors.Red, "screen to show the first frame");
        var canvas = _vm.Viewer.RemoteScreen;

        await _host.SendFrameAsync(new ScreenDelta
        {
            X = 0, Y = 0, Width = RemoteW, Height = RemoteH,
            ImageData = SolidPng(RemoteW, RemoteH, Colors.Blue)
        }, full: false);
        await WaitUntil(() => PixelAtCentreOf(img) == Colors.Blue, "screen to show the delta");
        Assert.Same(canvas, _vm.Viewer.RemoteScreen);
    }
}
