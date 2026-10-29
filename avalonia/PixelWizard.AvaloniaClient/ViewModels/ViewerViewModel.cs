using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using PixelWizard.AvaloniaClient.Services;
using PixelWizard.Core.Interfaces;
using PixelWizard.Core.Models;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Transport.Tcp;

namespace PixelWizard.AvaloniaClient.ViewModels;

/// <summary>What the viewer needs from the window: the shared status line, screen
/// switching and the system clipboard.</summary>
public interface IViewerShell
{
    string Status { set; }
    void ShowLiveScreen();
    /// <summary>Back to the connect screen; with <paramref name="onlyFromLive"/>, only if the live screen is showing.</summary>
    void ShowConnectScreen(bool onlyFromLive);
    void WriteClipboard(string text);
}

/// <summary>
/// Viewer role: connecting (direct or via router code), the ViewerSession wiring, the remote
/// canvas, zoom, metrics and ping, the no-frames banner, quality, recent and discovered
/// hosts, the certificate pin-mismatch offer, and input forwarding.
/// </summary>
public sealed class ViewerViewModel : ReactiveObject, IDisposable
{
    private readonly IViewerShell   _shell;
    private readonly ChatViewModel  _chat;
    private readonly AppSettings    _settings;
    private readonly IRouterClient  _router;
    private readonly HelloMessage   _ourHello;

    public ViewerViewModel(IViewerShell shell, ChatViewModel chat, AppSettings settings,
                           IRouterClient router, HelloMessage ourHello)
    {
        _shell    = shell;
        _chat     = chat;
        _settings = settings;
        _router   = router;
        _ourHello = ourHello;

        ConnectDirectCommand  = ReactiveCommand.CreateFromTask(ConnectDirect);
        ConnectViaCodeCommand = ReactiveCommand.CreateFromTask(ConnectViaCode);
        DisconnectCommand     = ReactiveCommand.Create(Disconnect);
        FitWindowCommand      = ReactiveCommand.Create(() => { ZoomPresetIndex = 0; });
        Zoom50Command         = ReactiveCommand.Create(() => { ZoomPresetIndex = 1; });
        Zoom75Command         = ReactiveCommand.Create(() => { ZoomPresetIndex = 2; });
        Zoom100Command        = ReactiveCommand.Create(() => { ZoomPresetIndex = 3; });
        ZoomInCommand         = ReactiveCommand.Create(() =>
        {
            int next = _zoomPresets.Length - 1;
            for (int i = 0; i < _zoomPresets.Length - 1; i++)
                if (_zoomPresets[i] <= _viewScale && _viewScale < _zoomPresets[i + 1]) { next = i + 1; break; }
            if (_viewScale <= 0) next = 3; // jump to 100% from Fit
            ZoomPresetIndex = Math.Min(next, _zoomPresets.Length - 1);
        });
        ZoomOutCommand        = ReactiveCommand.Create(() =>
        {
            if (_viewScale <= 0) return;
            int prev = 0; // Fit, when nothing is below the current scale
            for (int i = _zoomPresets.Length - 1; i > 0; i--)
                if (_zoomPresets[i] < _viewScale) { prev = i; break; }
            ZoomPresetIndex = prev;
        });
        ScanNetworkCommand    = ReactiveCommand.Create(ToggleScan);

        if (!string.IsNullOrWhiteSpace(_settings.LastRouterAddress))
            RouterAddress = _settings.LastRouterAddress;
        SyncRecentHosts();

        StartMetricsTimer();
    }

    // ── Connect form ──────────────────────────────────────────────────────────

    private string _hostAddress = "127.0.0.1";
    public string HostAddress { get => _hostAddress; set => this.RaiseAndSetIfChanged(ref _hostAddress, value); }

    private string _connectionCode = "";
    public string ConnectionCode { get => _connectionCode; set => this.RaiseAndSetIfChanged(ref _connectionCode, value); }

    private string _routerAddress = "localhost:9000";
    public string RouterAddress { get => _routerAddress; set => this.RaiseAndSetIfChanged(ref _routerAddress, value); }

    private bool _useTls = true;
    public bool UseTls { get => _useTls; set => this.RaiseAndSetIfChanged(ref _useTls, value); }

    private bool _isConnecting;
    public bool IsConnecting
    {
        get => _isConnecting;
        set
        {
            this.RaiseAndSetIfChanged(ref _isConnecting, value);
            this.RaisePropertyChanged(nameof(InputEnabled));
        }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            this.RaiseAndSetIfChanged(ref _isConnected, value);
            this.RaisePropertyChanged(nameof(ShowConnectPanel));
        }
    }

    public bool ShowConnectPanel => !_isConnected;
    public bool InputEnabled     => !_isConnecting;

    // ── Remote screen ─────────────────────────────────────────────────────────

    // Raised on the UI thread after each frame is drawn into the canvas. Deltas draw into the
    // same bitmap instance, so RemoteScreen does not change and the Image would not repaint;
    // the view invalidates it on this event.
    public event Action? FrameRendered;

    private Bitmap? _remoteScreen;
    public Bitmap? RemoteScreen
    {
        get => _remoteScreen;
        set
        {
            this.RaiseAndSetIfChanged(ref _remoteScreen, value);
            this.RaisePropertyChanged(nameof(ScaledImageWidth));
            this.RaisePropertyChanged(nameof(ScaledImageHeight));
        }
    }

    private bool _keyboardActive;
    public bool KeyboardActive { get => _keyboardActive; set => this.RaiseAndSetIfChanged(ref _keyboardActive, value); }

    // ── Zoom ──────────────────────────────────────────────────────────────────

    private static readonly double[] _zoomPresets = { 0, 0.5, 0.75, 1.0, 1.5, 2.0 };

    private double _viewScale = 0;
    public double ViewScale
    {
        get => _viewScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _viewScale, value);
            this.RaisePropertyChanged(nameof(IsFitMode));
            this.RaisePropertyChanged(nameof(ZoomLabel));
            this.RaisePropertyChanged(nameof(ScaledImageWidth));
            this.RaisePropertyChanged(nameof(ScaledImageHeight));
        }
    }
    public bool   IsFitMode       => _viewScale <= 0;
    public string ZoomLabel       => _viewScale <= 0 ? "Fit" : $"{(int)(_viewScale * 100)}%";
    public double ScaledImageWidth  => _remoteScreen != null && _viewScale > 0 ? _remoteScreen.Size.Width  * _viewScale : 0;
    public double ScaledImageHeight => _remoteScreen != null && _viewScale > 0 ? _remoteScreen.Size.Height * _viewScale : 0;

    private int _zoomPresetIndex = 0;
    public int ZoomPresetIndex
    {
        get => _zoomPresetIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _zoomPresetIndex, value);
            if (value >= 0 && value < _zoomPresets.Length)
                ViewScale = _zoomPresets[value];
        }
    }

    // ── Metrics ───────────────────────────────────────────────────────────────

    private string _fpsText = "FPS: —";
    public string FpsText { get => _fpsText; set => this.RaiseAndSetIfChanged(ref _fpsText, value); }

    private string _latencyText = "Latency: —";
    public string LatencyText { get => _latencyText; set => this.RaiseAndSetIfChanged(ref _latencyText, value); }

    private string _bandwidthText = "↓ —";
    public string BandwidthText { get => _bandwidthText; set => this.RaiseAndSetIfChanged(ref _bandwidthText, value); }

    // ── Feature 3: Frame timeout banner ───────────────────────────────────────

    private bool _showNoFramesBanner;
    public bool ShowNoFramesBanner
    {
        get => _showNoFramesBanner;
        set => this.RaiseAndSetIfChanged(ref _showNoFramesBanner, value);
    }

    // ── Feature 9: Viewer-side quality ────────────────────────────────────────

    private int _viewerQualityIndex = 1;
    public int ViewerQualityIndex
    {
        get => _viewerQualityIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _viewerQualityIndex, value);
            if (_session?.IsConnected == true) _ = _session.SendQualityPresetAsync(value);
        }
    }

    // ── Connection history ────────────────────────────────────────────────────

    public ObservableCollection<string> RecentHosts { get; } = new();

    private string? _selectedRecentHost;
    public string? SelectedRecentHost
    {
        get => _selectedRecentHost;
        set { this.RaiseAndSetIfChanged(ref _selectedRecentHost, value); if (!string.IsNullOrEmpty(value)) HostAddress = value!; }
    }

    public bool HasRecentHosts => RecentHosts.Count > 0;

    private void SyncRecentHosts()
    {
        RecentHosts.Clear();
        foreach (var h in _settings.RecentHosts) RecentHosts.Add(h);
        this.RaisePropertyChanged(nameof(HasRecentHosts));
    }

    private void RememberHost(string host)
    {
        AppSettings.PushRecent(_settings.RecentHosts, host);
        SyncRecentHosts();
        _settings.Save();
    }

    // ── Network discovery ─────────────────────────────────────────────────────

    public ObservableCollection<string> DiscoveredHosts { get; } = new();

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; set => this.RaiseAndSetIfChanged(ref _isScanning, value); }

    private string? _selectedDiscoveredHost;
    public string? SelectedDiscoveredHost
    {
        get => _selectedDiscoveredHost;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedDiscoveredHost, value);
            if (value != null) HostAddress = value; // "ip:port" -- keeps a non-default port
        }
    }

    // ── Commands and callbacks ────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ConnectDirectCommand  { get; }
    public ReactiveCommand<Unit, Unit> ConnectViaCodeCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand     { get; }
    public ReactiveCommand<Unit, Unit> FitWindowCommand      { get; }
    public ReactiveCommand<Unit, Unit> Zoom50Command         { get; }
    public ReactiveCommand<Unit, Unit> Zoom75Command         { get; }
    public ReactiveCommand<Unit, Unit> Zoom100Command        { get; }
    public ReactiveCommand<Unit, Unit> ZoomInCommand         { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand        { get; }
    public ReactiveCommand<Unit, Unit> ScanNetworkCommand    { get; }

    // (pin key "host:port", recorded fingerprint, presented fingerprint) -> trust the new one?
    public Func<string, string, string, Task<bool>>? PinMismatchCallback { get; set; }

    // ── Internal state ────────────────────────────────────────────────────────

    private ViewerSession? _session;
    private CancellationTokenSource? _discoveryCts;

    private RenderTargetBitmap? _canvas;
    private int _canvasWidth, _canvasHeight;
    private int _renderedFrames, _receivedBytes, _lastLatencyMs = -1;
    private (int x, int y) _lastMousePos;

    private System.Timers.Timer? _metricsTimer;
    private System.Timers.Timer? _pingTimer;
    private System.Timers.Timer? _frameTimeoutTimer;

    private string _sessionSecret = "";

    // Advertised by whichever host this instance is viewing, learned from HelloAck. Desktop
    // hosts always advertise Full (IInputInjector is always present here); ShareOnly is for a
    // future share-only peer (e.g. a phone with no mouse/keyboard) that nothing in this
    // codebase produces yet -- see PixelWizard.Protocol.PeerRole.
    private PeerRole _hostPeerRole = PeerRole.Full;

    /// <summary>Chat and clipboard target while connected to a host.</summary>
    public ChatLink? ActiveChatLink =>
        _session is { IsConnected: true } s ? new ChatLink(IsHost: false, s.SendChatAsync, s.SendClipboardAsync) : null;

    // ── Connect ───────────────────────────────────────────────────────────────

    private async Task ConnectDirect()
    {
        if (!DirectAddress.TryParse(HostAddress, out string host, out int port))
        {
            _shell.Status = "Enter the host's address, e.g. 192.168.1.10 or 192.168.1.10:8888";
            return;
        }
        _sessionSecret = "";
        IsConnecting = true;
        _shell.Status = $"Connecting to {host}:{port}…";
        try
        {
            _session = BuildSession();
            await _session.ConnectAsync(host, port, UseTls);
        }
        catch (Exception ex) { _shell.Status = FriendlyError.Describe(ex); IsConnecting = false; }
    }

    private async Task ConnectViaCode()
    {
        string code = ConnectionCode.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code)) { _shell.Status = "Enter a connection code"; return; }
        var (host, port) = RouterEndpoint.Parse(RouterAddress);
        IsConnecting = true;
        _shell.Status = "Resolving via router…";
        try
        {
            var result = await _router.ResolveEndpointAsync(host, port, code);
            _sessionSecret = result.SessionSecret;
            _settings.LastRouterAddress = RouterAddress;
            _settings.Save();
            var parts = result.HostEndpoint.Split(':');
            _session = BuildSession();
            await _session.ConnectAsync(parts[0], int.Parse(parts[1]), UseTls);
        }
        catch (Exception ex) { _shell.Status = FriendlyError.Describe(ex); IsConnecting = false; }
    }

    // Refusing is the default: nothing changes unless the user explicitly trusts. Trusting
    // only forgets the old pin; the next connect (which the user starts) pins whatever the
    // host presents then, exactly as on a first connection.
    private async Task OfferPinResetAsync(CertificatePinMismatchException mismatch)
    {
        if (PinMismatchCallback == null ||
            !await PinMismatchCallback(mismatch.Key, mismatch.ExpectedFingerprint, mismatch.ActualFingerprint))
            return;

        int sep = mismatch.Key.LastIndexOf(':');
        if (sep > 0 && int.TryParse(mismatch.Key[(sep + 1)..], out int port)
            && TcpTransport.ForgetPin(mismatch.Key[..sep], port))
            _shell.Status = "Old certificate forgotten. Connect again to trust the host's new certificate.";
    }

    private ViewerSession BuildSession()
    {
        // Hello-on-connect and awaiting-Hello tracking live in ViewerSession (T9.3a);
        // dispatch classification too (T9.2a). The handlers below only do what still needs
        // Dispatcher.UIThread or touches state ViewerSession doesn't own (see ViewerSession's
        // class comment).
        var session = new ViewerSession(() => new TcpTransport(), _ourHello, _sessionSecret);
        // A certificate failure is raised on Error just before the transport disconnects,
        // while the session is not yet connected -- so the Error handler below would drop it
        // and the user would only see "Disconnected". Keep it for the Disconnected handler.
        Exception? certFailure = null;
        session.Disconnected += () =>
        {
            // Read synchronously: it's a per-report snapshot (see ViewerSession).
            bool helloUnanswered = session.HelloUnansweredAtDisconnect;
            var failure = certFailure;
            certFailure = null;
            Dispatcher.UIThread.Post(() =>
            {
                StopFrameTimeoutTimer();
                IsConnected  = false;
                IsConnecting = false;
                _shell.ShowConnectScreen(onlyFromLive: false);
                // Deliberately hedged: see ViewerSession.HelloUnansweredAtDisconnect.
                _shell.Status = failure != null ? FriendlyError.Describe(failure)
                    : helloUnanswered
                    ? "Disconnected — the host may be running an older, incompatible version"
                    : "Disconnected";
                _pingTimer?.Stop();
                KeyboardActive = false;
                if (failure is CertificatePinMismatchException mismatch)
                    _ = OfferPinResetAsync(mismatch);
            });
        };
        session.BytesReceived   += b => _receivedBytes += b;
        session.Error += ex => {
            if (ex is CertificatePinMismatchException or CertificateMissingException
                   or CertificatePinStoreCorruptedException)
                certFailure = ex;
            if (session.IsConnected)
                Dispatcher.UIThread.Post(() => _shell.Status = FriendlyError.Describe(ex));
        };
        // A handler bug, not a transport failure -- the connection survives, so this is
        // surfaced for diagnostics only and never touches Status/IsConnected.
        session.HandlerError += ex => System.Diagnostics.Debug.WriteLine($"[Viewer] handler error (session continues): {ex}");
        session.FullScreenReceived += data =>
        {
            ApplyFullScreen(data);
            ResetFrameTimeoutTimer();
        };
        session.ScreenDeltaReceived += delta =>
        {
            ApplyDelta(delta);
            ResetFrameTimeoutTimer();
        };
        session.HostHelloAcknowledged += hostHello =>
        {
            _hostPeerRole = hostHello.Role;
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected  = true;
                IsConnecting = false;
                _shell.ShowLiveScreen();
                _shell.Status = "Connected";
                RememberHost(HostAddress.Trim());
                _pingTimer?.Start();
                StartFrameTimeoutTimer();
            });
        };
        session.HostHelloRejected += rejected => Dispatcher.UIThread.Post(() =>
        {
            _shell.Status = $"Host rejected connection: {rejected.Message}";
            Disconnect();
        });
        session.HandshakeRejected += () => Dispatcher.UIThread.Post(() =>
        {
            _shell.Status = "Host rejected: invalid session token";
            Disconnect();
        });
        session.LatencyMeasured += ms => _lastLatencyMs = ms;
        session.ClipboardReceived += text => Dispatcher.UIThread.Post(() => _shell.WriteClipboard(text));
        session.ChatReceived += text => Dispatcher.UIThread.Post(() => _chat.Receive(isFromHost: true, text));
        return session;
    }

    public void Disconnect()
    {
        StopFrameTimeoutTimer();
        _session?.Disconnect();
        _session = null;
        IsConnected  = false;
        IsConnecting = false;
        RemoteScreen = null;
        ViewScale    = 0;
        var toDispose = _canvas;
        _canvas       = null;
        _canvasWidth  = 0;
        _canvasHeight = 0;
        toDispose?.Dispose();
        KeyboardActive = false;
        _chat.Reset();
        _shell.ShowConnectScreen(onlyFromLive: true);
    }

    // ── Feature 3: Frame timeout timer ────────────────────────────────────────

    private void StartFrameTimeoutTimer()
    {
        _frameTimeoutTimer?.Dispose();
        _frameTimeoutTimer = new System.Timers.Timer(30_000) { AutoReset = false };
        _frameTimeoutTimer.Elapsed += (_, _) =>
            Dispatcher.UIThread.Post(() => ShowNoFramesBanner = true);
        _frameTimeoutTimer.Start();
    }

    private void ResetFrameTimeoutTimer()
    {
        if (_frameTimeoutTimer == null) return;
        _frameTimeoutTimer.Stop();
        ShowNoFramesBanner = false;
        _frameTimeoutTimer.Start();
    }

    private void StopFrameTimeoutTimer()
    {
        _frameTimeoutTimer?.Stop();
        _frameTimeoutTimer?.Dispose();
        _frameTimeoutTimer  = null;
        ShowNoFramesBanner  = false;
    }

    // ── Screen rendering ──────────────────────────────────────────────────────

    private void ApplyFullScreen(byte[] data)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toDispose = _canvas;
            _canvas       = null;
            _canvasWidth  = 0;
            _canvasHeight = 0;

            using var ms      = new MemoryStream(data);
            using var decoded = new Bitmap(ms);
            int w = (int)decoded.Size.Width, h = (int)decoded.Size.Height;
            EnsureCanvas(w, h);
            using var dc = _canvas!.CreateDrawingContext();
            dc.DrawImage(decoded, new Rect(0, 0, w, h));

            RemoteScreen = _canvas;
            _renderedFrames++;
            FrameRendered?.Invoke();
            toDispose?.Dispose();
        });
    }

    private void ApplyDelta(ScreenDelta delta)
    {
        // Decode JPEG on background thread (safe — no shared state)
        byte[] imageData = delta.ImageData;
        int dx = delta.X, dy = delta.Y, dw = delta.Width, dh = delta.Height;

        Dispatcher.UIThread.Post(() =>
        {
            EnsureCanvas(Math.Max(_canvasWidth, dx + dw), Math.Max(_canvasHeight, dy + dh));
            using var ms    = new MemoryStream(imageData);
            using var patch = new Bitmap(ms);
            // clear: false -- the parameterless overload wipes the canvas to transparent, so
            // every delta blanked the screen except its own patch until the next full frame.
            using var dc    = _canvas!.CreateDrawingContext(false);
            dc.DrawImage(patch, new Rect(dx, dy, dw, dh));
            RemoteScreen = _canvas;
            _renderedFrames++;
            FrameRendered?.Invoke();
        });
    }

    private void EnsureCanvas(int w, int h)
    {
        if (_canvas != null && _canvasWidth >= w && _canvasHeight >= h) return;
        var old  = _canvas;
        _canvas  = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        _canvasWidth = w; _canvasHeight = h;
        if (old != null)
        {
            using var dc = _canvas.CreateDrawingContext();
            dc.DrawImage(old, new Rect(0, 0, old.Size.Width, old.Size.Height));
        }
    }

    // ── Input forwarding ─────────────────────────────────────────────────────

    public async void SendMouseMove(int rx, int ry)
    {
        if (_session?.IsConnected != true || !_hostPeerRole.AcceptsInput()) return;
        if (Math.Abs(rx - _lastMousePos.x) < 1 && Math.Abs(ry - _lastMousePos.y) < 1) return;
        _lastMousePos = (rx, ry);
        await _session.SendMouseMoveAsync(rx, ry);
    }

    public async void SendMouseClick(int rx, int ry, bool leftButton)
    {
        if (_session?.IsConnected != true || !_hostPeerRole.AcceptsInput()) return;
        await _session.SendMouseClickAsync(rx, ry, leftButton);
    }

    public async void SendMouseDown(int rx, int ry, bool leftButton)
    {
        if (_session?.IsConnected != true || !_hostPeerRole.AcceptsInput()) return;
        await _session.SendMouseDownAsync(rx, ry, leftButton);
    }

    public async void SendMouseUp(int rx, int ry, bool leftButton)
    {
        if (_session?.IsConnected != true || !_hostPeerRole.AcceptsInput()) return;
        await _session.SendMouseUpAsync(rx, ry, leftButton);
    }

    public async void SendKey(int vk, bool isDown)
    {
        if (_session?.IsConnected != true || vk == 0 || !_hostPeerRole.AcceptsInput()) return;
        await _session.SendKeyAsync(vk, isDown);
    }

    // ── Metrics ───────────────────────────────────────────────────────────────

    private void StartMetricsTimer()
    {
        _metricsTimer = new System.Timers.Timer(1000);
        _metricsTimer.Elapsed += (_, _) =>
        {
            int fps = _renderedFrames, bytes = _receivedBytes;
            _renderedFrames = 0; _receivedBytes = 0;
            string lat = _lastLatencyMs >= 0 ? $"{_lastLatencyMs} ms" : "—";
            Dispatcher.UIThread.Post(() =>
            {
                FpsText       = $"FPS: {fps}";
                LatencyText   = $"Latency: {lat}";
                BandwidthText = $"↓ {bytes / 1024.0:0} KB/s";
            });
        };
        _metricsTimer.Start();

        _pingTimer = new System.Timers.Timer(2000) { AutoReset = true };
        _pingTimer.Elapsed += async (_, _) =>
        {
            if (_session?.IsConnected == true)
                await _session.SendPingAsync();
        };
    }

    // ── Network discovery ─────────────────────────────────────────────────────

    private void ToggleScan()
    {
        if (_isScanning)
        {
            StopScan();
        }
        else
        {
            DiscoveredHosts.Clear();
            _discoveryCts = new CancellationTokenSource();
            IsScanning = true;
            _ = Task.Run(async () =>
            {
                await NetworkDiscovery.ListenAsync(host =>
                    Dispatcher.UIThread.Post(() => DiscoveredHosts.Add(host)),
                    _discoveryCts.Token);
                Dispatcher.UIThread.Post(() => IsScanning = false);
            });
            // Auto-stop after 10 s
            _ = Task.Delay(10_000).ContinueWith(_ => Dispatcher.UIThread.Post(StopScan));
        }
    }

    private void StopScan()
    {
        _discoveryCts?.Cancel();
        _discoveryCts = null;
        IsScanning = false;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        Disconnect();
        _metricsTimer?.Dispose();
        _pingTimer?.Dispose();
        _frameTimeoutTimer?.Dispose();
        _discoveryCts?.Cancel();
        _canvas?.Dispose();
    }
}
