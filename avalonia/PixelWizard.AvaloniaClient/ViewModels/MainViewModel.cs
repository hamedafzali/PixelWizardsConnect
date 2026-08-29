using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ReactiveUI;
using PixelWizard.AvaloniaClient.Platform;
using PixelWizard.AvaloniaClient.Services;
using PixelWizard.Core.Interfaces;
using PixelWizard.Core.Models;
using PixelWizard.Protocol;
using PixelWizard.Transport;

namespace PixelWizard.AvaloniaClient.ViewModels;

public enum AppScreen { ModeSelection, Viewer, Host, LiveScreen }

public class MainViewModel : ReactiveObject, IDisposable
{
    // ── Settings ──────────────────────────────────────────────────────────────

    private readonly AppSettings _settings = AppSettings.Load();

    // ── Window title ──────────────────────────────────────────────────────────

    private string _windowTitle = "PixelWizard Connect";
    public string WindowTitle { get => _windowTitle; set => this.RaiseAndSetIfChanged(ref _windowTitle, value); }

    private void UpdateWindowTitle()
    {
        WindowTitle = _screen switch
        {
            AppScreen.LiveScreen => $"PixelWizard Connect — Viewing {HostAddress}",
            AppScreen.Host when IsHostRunning => "PixelWizard Connect — Hosting",
            _ => "PixelWizard Connect"
        };
    }

    // ── Onboarding ────────────────────────────────────────────────────────────

    private bool _showOnboarding;
    public bool ShowOnboarding { get => _showOnboarding; set => this.RaiseAndSetIfChanged(ref _showOnboarding, value); }
    public ReactiveCommand<Unit, Unit> DismissOnboardingCommand { get; }

    private void DismissOnboarding()
    {
        ShowOnboarding = false;
        _settings.OnboardingSeen = true;
        _settings.Save();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private AppScreen _screen = AppScreen.ModeSelection;
    private AppScreen Screen
    {
        get => _screen;
        set
        {
            _screen = value;
            this.RaisePropertyChanged(nameof(ShowModeSelection));
            this.RaisePropertyChanged(nameof(ShowViewerPanel));
            this.RaisePropertyChanged(nameof(ShowHostPanel));
            this.RaisePropertyChanged(nameof(ShowScreenDisplay));
            UpdateWindowTitle();
        }
    }

    public bool ShowModeSelection => _screen == AppScreen.ModeSelection;
    public bool ShowViewerPanel   => _screen == AppScreen.Viewer;
    public bool ShowHostPanel     => _screen == AppScreen.Host;
    public bool ShowScreenDisplay => _screen == AppScreen.LiveScreen;

    // ── Platform ──────────────────────────────────────────────────────────────

    private readonly IHostProvider _hostProvider = HostProviderFactory.Create();
    public bool CanHost => _hostProvider.IsAvailable;

    // ── Shared state ──────────────────────────────────────────────────────────

    private string _status = "Choose a mode to begin";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

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

    // ── Viewer properties ─────────────────────────────────────────────────────

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

    public bool ShowConnectPanel => !_isConnected;
    public bool InputEnabled     => !_isConnecting;

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

    private bool _keyboardActive;
    public bool KeyboardActive { get => _keyboardActive; set => this.RaiseAndSetIfChanged(ref _keyboardActive, value); }

    // ── Host properties ───────────────────────────────────────────────────────

    private string _hostPort = "8888";
    public string HostPort { get => _hostPort; set => this.RaiseAndSetIfChanged(ref _hostPort, value); }

    private string _hostRouterAddress = "localhost:9000";
    public string HostRouterAddress { get => _hostRouterAddress; set => this.RaiseAndSetIfChanged(ref _hostRouterAddress, value); }

    private bool _useRouterForHost;
    public bool UseRouterForHost
    {
        get => _useRouterForHost;
        set
        {
            this.RaiseAndSetIfChanged(ref _useRouterForHost, value);
            this.RaisePropertyChanged(nameof(ShowDirectHostPanel));
            this.RaisePropertyChanged(nameof(ShowRouterHostPanel));
        }
    }
    public bool ShowDirectHostPanel => !_useRouterForHost;
    public bool ShowRouterHostPanel =>  _useRouterForHost;

    private int _hostQualityIndex = 1;
    public int HostQualityIndex { get => _hostQualityIndex; set => this.RaiseAndSetIfChanged(ref _hostQualityIndex, value); }

    private bool _hostTlsEnabled = true;
    public bool HostTlsEnabled { get => _hostTlsEnabled; set => this.RaiseAndSetIfChanged(ref _hostTlsEnabled, value); }

    private string _hostConnectionCode = "";
    public string HostConnectionCode { get => _hostConnectionCode; set => this.RaiseAndSetIfChanged(ref _hostConnectionCode, value); }

    private string _hostStatus = "Not started";
    public string HostStatus { get => _hostStatus; set => this.RaiseAndSetIfChanged(ref _hostStatus, value); }

    private bool _isHostRunning;
    public bool IsHostRunning
    {
        get => _isHostRunning;
        set { this.RaiseAndSetIfChanged(ref _isHostRunning, value); UpdateWindowTitle(); }
    }

    private bool _showCodeCard;
    public bool ShowCodeCard { get => _showCodeCard; set => this.RaiseAndSetIfChanged(ref _showCodeCard, value); }

    private string _webViewerUrl = "";
    public string WebViewerUrl { get => _webViewerUrl; set => this.RaiseAndSetIfChanged(ref _webViewerUrl, value); }

    private bool _showWebViewer;
    public bool ShowWebViewer { get => _showWebViewer; set => this.RaiseAndSetIfChanged(ref _showWebViewer, value); }

    // ── Metrics ───────────────────────────────────────────────────────────────

    private string _fpsText = "FPS: —";
    public string FpsText { get => _fpsText; set => this.RaiseAndSetIfChanged(ref _fpsText, value); }

    private string _latencyText = "Latency: —";
    public string LatencyText { get => _latencyText; set => this.RaiseAndSetIfChanged(ref _latencyText, value); }

    private string _bandwidthText = "↓ —";
    public string BandwidthText { get => _bandwidthText; set => this.RaiseAndSetIfChanged(ref _bandwidthText, value); }

    // ── Feature 2: Monitor selection ──────────────────────────────────────────

    private IReadOnlyList<MonitorInfo> _availableMonitors = new List<MonitorInfo>();
    public IReadOnlyList<MonitorInfo> AvailableMonitors
    {
        get => _availableMonitors;
        set => this.RaiseAndSetIfChanged(ref _availableMonitors, value);
    }

    private int _selectedMonitorIndex = 0;
    public int SelectedMonitorIndex
    {
        get => _selectedMonitorIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedMonitorIndex, value);
    }

    // ── Feature 3: Viewer-side frame timeout banner ───────────────────────────

    private bool _showNoFramesBanner;
    public bool ShowNoFramesBanner
    {
        get => _showNoFramesBanner;
        set => this.RaiseAndSetIfChanged(ref _showNoFramesBanner, value);
    }

    private System.Timers.Timer? _frameTimeoutTimer;

    // ── Feature 4: Chat ───────────────────────────────────────────────────────

    public ObservableCollection<ChatEntry> ChatMessages { get; } = new();

    private bool _chatVisible;
    public bool ChatVisible
    {
        get => _chatVisible;
        set
        {
            this.RaiseAndSetIfChanged(ref _chatVisible, value);
            if (value) UnreadChatCount = 0;
        }
    }

    private int _unreadChatCount;
    public int UnreadChatCount
    {
        get => _unreadChatCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _unreadChatCount, value);
            this.RaisePropertyChanged(nameof(HasUnreadChat));
        }
    }
    public bool HasUnreadChat => _unreadChatCount > 0;

    private string _chatInput = "";
    public string ChatInput { get => _chatInput; set => this.RaiseAndSetIfChanged(ref _chatInput, value); }

    // ── Feature 6: Session notes ──────────────────────────────────────────────

    private string _sessionNotes = "";
    public string SessionNotes { get => _sessionNotes; set => this.RaiseAndSetIfChanged(ref _sessionNotes, value); }

    private bool _sessionNotesVisible;
    public bool SessionNotesVisible
    {
        get => _sessionNotesVisible;
        set => this.RaiseAndSetIfChanged(ref _sessionNotesVisible, value);
    }

    private DateTime _sessionStartTime;
    private string   _sessionRemoteEndpoint = "";

    // ── Feature 7: System tray tooltip ────────────────────────────────────────

    private string _trayTooltip = "Idle";
    public string TrayTooltip { get => _trayTooltip; set => this.RaiseAndSetIfChanged(ref _trayTooltip, value); }

    // ── Feature 8: Viewing badge callbacks ────────────────────────────────────

    public Action? ShowViewingBadgeCallback { get; set; }
    public Action? HideViewingBadgeCallback { get; set; }

    // ── Feature 9: Viewer-side quality ────────────────────────────────────────

    private int _viewerQualityIndex = 1;
    public int ViewerQualityIndex
    {
        get => _viewerQualityIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _viewerQualityIndex, value);
            SendViewerQualityPreset(value);
        }
    }

    // ── Connection history ────────────────────────────────────────────────────

    public System.Collections.ObjectModel.ObservableCollection<string> RecentHosts { get; } = new();

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

    private readonly System.Collections.ObjectModel.ObservableCollection<string> _discoveredHosts = new();
    public System.Collections.ObjectModel.ObservableCollection<string> DiscoveredHosts => _discoveredHosts;

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; set => this.RaiseAndSetIfChanged(ref _isScanning, value); }

    private string? _selectedDiscoveredHost;
    public string? SelectedDiscoveredHost
    {
        get => _selectedDiscoveredHost;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedDiscoveredHost, value);
            if (value != null)
            {
                var parts = value.Split(':');
                HostAddress = parts[0];
            }
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> GoHostCommand             { get; }
    public ReactiveCommand<Unit, Unit> GoViewerCommand           { get; }
    public ReactiveCommand<Unit, Unit> BackCommand               { get; }
    public ReactiveCommand<Unit, Unit> ConnectDirectCommand      { get; }
    public ReactiveCommand<Unit, Unit> ConnectViaCodeCommand     { get; }
    public ReactiveCommand<Unit, Unit> StartDirectHostCommand    { get; }
    public ReactiveCommand<Unit, Unit> RegisterHostCommand       { get; }
    public ReactiveCommand<Unit, Unit> StopHostCommand           { get; }
    public ReactiveCommand<Unit, Unit> CopyCodeCommand           { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand         { get; }
    public ReactiveCommand<Unit, Unit> FitWindowCommand          { get; }
    public ReactiveCommand<Unit, Unit> Zoom50Command             { get; }
    public ReactiveCommand<Unit, Unit> Zoom75Command             { get; }
    public ReactiveCommand<Unit, Unit> Zoom100Command            { get; }
    public ReactiveCommand<Unit, Unit> ZoomInCommand             { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand            { get; }
    public ReactiveCommand<Unit, Unit> SendClipboardCommand      { get; }
    public ReactiveCommand<Unit, Unit> ToggleChatCommand         { get; }
    public ReactiveCommand<Unit, Unit> SendChatCommand           { get; }
    public ReactiveCommand<Unit, Unit> ToggleSessionNotesCommand { get; }
    public ReactiveCommand<Unit, Unit> ScanNetworkCommand        { get; }

    // ── Internal state ────────────────────────────────────────────────────────

    private ISessionTransport?    _transport;
    private ISessionTransport?    _hostTransport;
    private readonly IRouterClient _router = new RouterHttpClient();
    private IScreenCapture?       _capture;
    private IInputInjector?       _input;
    private WebSocketHostServer?  _wsServer;
    private System.Timers.Timer?  _captureTimer;
    private bool                  _isSendingFrame;
    private bool                  _relistening = false;
    private CancellationTokenSource? _discoveryCts;
    private CancellationTokenSource? _announceCts;

    private RenderTargetBitmap? _canvas;
    private int _canvasWidth, _canvasHeight;
    private int _renderedFrames, _receivedBytes, _lastLatencyMs = -1;
    private (int x, int y) _lastMousePos;

    private System.Timers.Timer? _metricsTimer;
    private System.Timers.Timer? _pingTimer;
    private System.Timers.Timer? _sessionWatchdog;

    private string _sessionSecret         = "";
    private string _expectedSessionSecret = "";
    private bool   _hostHandshakeComplete;
    private bool   _hostHelloComplete;
    private int    _activeHostPort = 8888;

    // Viewer-side heuristic for detecting a v1 host (see BuildViewerTransport): true from the
    // moment Hello is sent until either any reply arrives or the connection drops.
    private bool _awaitingHelloResponse;

    // Advertised by whichever host this instance is viewing, learned from HelloAck. Desktop
    // hosts always advertise Full (IInputInjector is always present here); ShareOnly is for a
    // future share-only peer (e.g. a phone with no mouse/keyboard) that nothing in this
    // codebase produces yet -- see PixelWizard.Protocol.PeerRole.
    private PeerRole _hostPeerRole = PeerRole.Full;

    private static readonly HelloMessage OurHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };

    public Func<string, Task<bool>>? ConsentCallback     { get; set; }
    public Func<string, Task>?       ClipboardCallback   { get; set; }
    public Func<Task<string?>>?      GetClipboardCallback { get; set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel()
    {
        GoHostCommand             = ReactiveCommand.Create(GoHost);
        GoViewerCommand           = ReactiveCommand.Create(() => { Screen = AppScreen.Viewer; });
        BackCommand               = ReactiveCommand.Create(GoBack);
        ConnectDirectCommand      = ReactiveCommand.CreateFromTask(ConnectDirect);
        ConnectViaCodeCommand     = ReactiveCommand.CreateFromTask(ConnectViaCode);
        StartDirectHostCommand    = ReactiveCommand.CreateFromTask(StartDirectHost);
        RegisterHostCommand       = ReactiveCommand.CreateFromTask(RegisterWithRouter);
        StopHostCommand           = ReactiveCommand.Create(StopHost);
        CopyCodeCommand           = ReactiveCommand.Create(CopyCode);
        DisconnectCommand         = ReactiveCommand.Create(DisconnectViewer);
        FitWindowCommand          = ReactiveCommand.Create(() => { ZoomPresetIndex = 0; });
        Zoom50Command             = ReactiveCommand.Create(() => { ZoomPresetIndex = 1; });
        Zoom75Command             = ReactiveCommand.Create(() => { ZoomPresetIndex = 2; });
        Zoom100Command            = ReactiveCommand.Create(() => { ZoomPresetIndex = 3; });
        ZoomInCommand             = ReactiveCommand.Create(() =>
        {
            int next = _zoomPresets.Length - 1;
            for (int i = 0; i < _zoomPresets.Length - 1; i++)
                if (_zoomPresets[i] <= _viewScale && _viewScale < _zoomPresets[i + 1]) { next = i + 1; break; }
            if (_viewScale <= 0) next = 3; // jump to 100% from Fit
            ZoomPresetIndex = Math.Min(next, _zoomPresets.Length - 1);
        });
        ZoomOutCommand            = ReactiveCommand.Create(() =>
        {
            if (_viewScale <= 0) return;
            int prev = 0;
            for (int i = 1; i < _zoomPresets.Length; i++)
                if (_zoomPresets[i] < _viewScale || (_zoomPresets[i] <= _viewScale && i > 0)) { prev = i - 1; break; }
            ZoomPresetIndex = Math.Max(prev, 0);
        });
        SendClipboardCommand      = ReactiveCommand.CreateFromTask(SendClipboardAsync);
        ToggleChatCommand         = ReactiveCommand.Create(() => { ChatVisible = !ChatVisible; });
        SendChatCommand           = ReactiveCommand.Create(SendChatMessage);
        ToggleSessionNotesCommand = ReactiveCommand.Create(() => { SessionNotesVisible = !SessionNotesVisible; });
        ScanNetworkCommand        = ReactiveCommand.Create(ToggleScan);
        DismissOnboardingCommand  = ReactiveCommand.Create(DismissOnboarding);

        // Restore persisted preferences.
        if (!string.IsNullOrWhiteSpace(_settings.LastRouterAddress))
        {
            RouterAddress     = _settings.LastRouterAddress;
            HostRouterAddress = _settings.LastRouterAddress;
        }
        SyncRecentHosts();
        ShowOnboarding = !_settings.OnboardingSeen;

        StartMetricsTimer();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void GoHost()
    {
        try { AvailableMonitors = _hostProvider.ListMonitors(); }
        catch { AvailableMonitors = new List<MonitorInfo> { new MonitorInfo(0, "Default", 1920, 1080, true) }; }
        Screen = AppScreen.Host;
    }

    private void GoBack()
    {
        if (IsHostRunning) StopHost();
        DisconnectViewer();
        Screen = AppScreen.ModeSelection;
    }

    // ── Viewer connect ────────────────────────────────────────────────────────

    private async Task ConnectDirect()
    {
        _sessionSecret = "";
        IsConnecting = true;
        Status = $"Connecting to {HostAddress}…";
        try
        {
            _transport = BuildViewerTransport();
            await _transport.ConnectAsync(HostAddress.Trim(), 8888, UseTls);
        }
        catch (Exception ex) { Status = FriendlyError.Describe(ex); IsConnecting = false; }
    }

    private async Task ConnectViaCode()
    {
        string code = ConnectionCode.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code)) { Status = "Enter a connection code"; return; }
        var (host, port) = ParseAddress(RouterAddress);
        IsConnecting = true;
        Status = "Resolving via router…";
        try
        {
            var result = await _router.ResolveEndpointAsync(host, port, code);
            _sessionSecret = result.SessionSecret;
            _settings.LastRouterAddress = RouterAddress;
            _settings.Save();
            var parts = result.HostEndpoint.Split(':');
            _transport = BuildViewerTransport();
            await _transport.ConnectAsync(parts[0], int.Parse(parts[1]), UseTls);
        }
        catch (Exception ex) { Status = FriendlyError.Describe(ex); IsConnecting = false; }
    }

    private ISessionTransport BuildViewerTransport()
    {
        var t = new TcpTransport();
        t.Connected += async () =>
        {
            _awaitingHelloResponse = true;
            await t.SendMessageAsync(new NetworkMessage
            {
                Type = MessageType.Hello,
                Data = OurHello.Serialize()
            });
        };
        t.Disconnected += () => Dispatcher.UIThread.Post(() =>
        {
            StopFrameTimeoutTimer();
            IsConnected  = false;
            IsConnecting = false;
            Screen       = AppScreen.Viewer;
            // A v1 host has no concept of Hello: its own strict pre-handshake gate sees an
            // unrecognized message type and disconnects immediately with zero bytes sent back
            // (nothing like HelloAck/HelloRejected is possible from a build that predates
            // them). So "we sent Hello and the connection closed before any reply arrived" is
            // the only observable signal from this side -- unlike the host-side detection
            // above, it isn't a positive identification (an ordinary network drop in that same
            // narrow window looks identical), so the wording below is deliberately hedged.
            Status = _awaitingHelloResponse
                ? "Disconnected — the host may be running an older, incompatible version"
                : "Disconnected";
            _awaitingHelloResponse = false;
            _pingTimer?.Stop();
            KeyboardActive = false;
        });
        t.MessageReceived += OnViewerMessage;
        t.BytesReceived   += b => _receivedBytes += b;
        t.Error += ex => {
            if (t.IsConnected)
                Dispatcher.UIThread.Post(() => Status = FriendlyError.Describe(ex));
        };
        return t;
    }

    private void DisconnectViewer()
    {
        StopFrameTimeoutTimer();
        _transport?.Disconnect();
        _transport   = null;
        IsConnected  = false;
        IsConnecting = false;
        RemoteScreen = null;
        ViewScale    = 0;
        var toDispose = _canvas;
        _canvas       = null;
        _canvasWidth  = 0;
        _canvasHeight = 0;
        toDispose?.Dispose();
        KeyboardActive  = false;
        ChatMessages.Clear();
        ChatVisible     = false;
        UnreadChatCount = 0;
        if (Screen == AppScreen.LiveScreen) Screen = AppScreen.Viewer;
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

    // ── Host mode ─────────────────────────────────────────────────────────────

    private async Task StartDirectHost()
    {
        if (!_hostProvider.IsAvailable) { Status = "Host not available on this platform"; return; }
        if (!int.TryParse(HostPort, out int port)) port = 8888;
        _expectedSessionSecret = "";
        _activeHostPort        = port;
        Status = "Starting host…";
        try
        {
            SetupHostServices();
            _hostTransport = BuildHostTransport();
            await _hostTransport.StartServerAsync(port, HostTlsEnabled);
            StartCaptureTimer();
            _announceCts = new CancellationTokenSource();
            _ = NetworkDiscovery.AnnounceAsync(_activeHostPort, _announceCts.Token);
            string lanIp = GetLanIp();
            HostStatus = $"Listening — tell viewer to connect to {lanIp}:{port}";
            Status     = $"Host ready. Viewer address: {lanIp}:{port}";
        }
        catch (Exception ex) { Status = FriendlyError.Describe(ex); }
    }

    private static string GetLanIp()
    {
        foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !System.Net.IPAddress.IsLoopback(ip))
                return ip.ToString();
        return "127.0.0.1";
    }

    private async Task RegisterWithRouter()
    {
        if (!_hostProvider.IsAvailable) { Status = "Host not available on this platform"; return; }
        var (host, port) = ParseAddress(HostRouterAddress);
        Status = "Registering with router…";
        try
        {
            string localIp   = GetLocalEndpoint(host);
            var result       = await _router.RegisterHostAsync(host, port, localIp);
            _settings.LastRouterAddress = HostRouterAddress;
            _settings.Save();
            _expectedSessionSecret = result.SessionSecret;
            _activeHostPort        = 8888;

            HostConnectionCode = result.ConnectionCode;
            ShowCodeCard = true;

            SetupHostServices();
            _hostTransport = BuildHostTransport();
            await _hostTransport.StartServerAsync(8888, HostTlsEnabled);
            StartCaptureTimer();
            _announceCts = new CancellationTokenSource();
            _ = NetworkDiscovery.AnnounceAsync(_activeHostPort, _announceCts.Token);
            HostStatus = $"Ready — code: {result.ConnectionCode}";
            Status = $"Host registered. Share code: {result.ConnectionCode}";
        }
        catch (Exception ex) { Status = FriendlyError.Describe(ex); }
    }

    private void SetupHostServices()
    {
        var settings = StreamingSettings.FromPresetIndex(HostQualityIndex);
        _capture = _hostProvider.CreateCapture(settings.FullRefreshInterval, SelectedMonitorIndex);
        _input   = _hostProvider.CreateInput();

        _wsServer = new WebSocketHostServer(9001);
        _wsServer.Log += msg => Dispatcher.UIThread.Post(() => Status = msg);
        _wsServer.Start();
        WebViewerUrl  = "http://localhost:9001/";
        ShowWebViewer = true;
        IsHostRunning = true;

        _sessionStartTime      = DateTime.Now;
        _sessionRemoteEndpoint = "";
        SessionNotes           = "";
        TrayTooltip            = "Hosting — waiting for viewer";
    }

    private ISessionTransport BuildHostTransport()
    {
        var t = new TcpTransport();
        t.Connected += () =>
        {
            _hostHelloComplete     = false;
            _hostHandshakeComplete = false;
            Dispatcher.UIThread.Post(() =>
            {
                HostStatus = "Viewer connecting — authenticating…";
                Status     = "Authenticating viewer…";
            });
        };
        t.Disconnected += () => Dispatcher.UIThread.Post(async () =>
        {
            _sessionWatchdog?.Stop();
            _hostHelloComplete     = false;
            _hostHandshakeComplete = false;
            HideViewingBadgeCallback?.Invoke();
            TrayTooltip = "Hosting — waiting for viewer";
            SaveSessionNotes();
            HostStatus = "Client disconnected";
            Status     = "Client disconnected";
            if (IsHostRunning && !_relistening)
                await RelistenHostAsync();
        });
        t.MessageReceived += OnHostMessage;
        t.Error += ex => {
            // Only surface error if transport was actually connected — ignore EOF/reset on disconnect
            if (t.IsConnected)
                Dispatcher.UIThread.Post(() => Status = FriendlyError.Describe(ex));
        };
        return t;
    }

    private async Task RelistenHostAsync()
    {
        if (_relistening) return;
        _relistening = true;
        HostStatus = "Waiting for next connection…";
        Status     = "Waiting for next connection…";
        try
        {
            // Null out first so the Disconnected event fired by Dispose
            // sees _hostTransport == null and skips the re-listen guard.
            var old = _hostTransport;
            _hostTransport = null;
            old?.Dispose();

            _hostTransport = BuildHostTransport();
            await _hostTransport.StartServerAsync(_activeHostPort, HostTlsEnabled);
        }
        catch (Exception ex)
        {
            Status = FriendlyError.Describe(ex);
            IsHostRunning = false;
        }
        finally
        {
            _relistening = false;
        }
    }

    private void StopHost()
    {
        SaveSessionNotes();
        _captureTimer?.Stop();
        _captureTimer?.Dispose();
        _captureTimer = null;
        _announceCts?.Cancel();
        _announceCts = null;
        _sessionWatchdog?.Stop();
        _sessionWatchdog?.Dispose();
        _sessionWatchdog = null;
        _hostTransport?.Disconnect();
        _capture?.Dispose();
        _wsServer?.Stop();
        _hostTransport         = null;
        _capture               = null;
        _input                 = null;
        _wsServer              = null;
        _hostHelloComplete     = false;
        _hostHandshakeComplete = false;
        IsHostRunning  = false;
        ShowCodeCard   = false;
        ShowWebViewer  = false;
        HostStatus     = "Stopped";
        Status         = "Host stopped";
        TrayTooltip    = "Idle";
        HideViewingBadgeCallback?.Invoke();
        ChatMessages.Clear();
        ChatVisible     = false;
        UnreadChatCount = 0;
        SessionNotesVisible = false;
    }

    private void CopyCode()
    {
        if (!string.IsNullOrEmpty(HostConnectionCode) && ClipboardCallback != null)
        {
            _ = ClipboardCallback(HostConnectionCode);
            Status = "Code copied to clipboard";
        }
    }

    // ── Feature 6: Save session notes ─────────────────────────────────────────

    private void SaveSessionNotes()
    {
        string notes = SessionNotes?.Trim() ?? "";
        if (notes.Length == 0) return;

        try
        {
            string dir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PixelWizardNotes");
            Directory.CreateDirectory(dir);

            string ts      = _sessionStartTime.ToString("yyyy-MM-dd_HH-mm-ss");
            string machine = Environment.MachineName;
            string file    = Path.Combine(dir, $"{ts}_{machine}.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"Start time   : {_sessionStartTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"End time     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Remote       : {(_sessionRemoteEndpoint.Length > 0 ? _sessionRemoteEndpoint : "(unknown)")}");
            sb.AppendLine();
            sb.AppendLine("─── Notes ───────────────────────────────────────");
            sb.AppendLine(notes);

            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    // ── Session watchdog (host side) ──────────────────────────────────────────

    private void StartSessionWatchdog()
    {
        _sessionWatchdog?.Dispose();
        _sessionWatchdog = new System.Timers.Timer(30_000) { AutoReset = false };
        _sessionWatchdog.Elapsed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            HostStatus = "Session timed out — viewer inactive";
            Status     = "Session timed out";
            _hostTransport?.Disconnect();
        });
        _sessionWatchdog.Start();
    }

    private void ResetSessionWatchdog()
    {
        _sessionWatchdog?.Stop();
        _sessionWatchdog?.Start();
    }

    // ── Capture loop ──────────────────────────────────────────────────────────

    private void StartCaptureTimer()
    {
        var settings = StreamingSettings.FromPresetIndex(HostQualityIndex);
        _captureTimer?.Stop();
        _captureTimer?.Dispose();
        _captureTimer = new System.Timers.Timer(settings.FrameInterval.TotalMilliseconds)
        {
            AutoReset = true
        };
        _captureTimer.Elapsed += async (_, _) => await CaptureTickAsync();
        _captureTimer.Start();
    }

    private async Task CaptureTickAsync()
    {
        if (_capture == null || _hostTransport?.IsConnected != true || _isSendingFrame) return;
        _isSendingFrame = true;
        try
        {
            var settings = StreamingSettings.FromPresetIndex(HostQualityIndex);
            var deltas   = _capture.Capture(false, settings.JpegQuality);
            foreach (var delta in deltas)
            {
                bool full = delta.X == 0 && delta.Y == 0 &&
                            delta.Width  == _capture.Resolution.Width &&
                            delta.Height == _capture.Resolution.Height;

                await _hostTransport.SendMessageAsync(new NetworkMessage
                {
                    Type = full ? MessageType.FullScreen : MessageType.ScreenDelta,
                    Data = full ? delta.ImageData : delta.Serialize()
                });

                if (_wsServer != null)
                {
                    if (full) await _wsServer.BroadcastFrameAsync(delta.ImageData);
                    else      await _wsServer.BroadcastDeltaAsync(delta);
                }
            }
        }
        catch { Status = "Screen capture error — the session may be unstable."; }
        finally { _isSendingFrame = false; }
    }

    // ── Incoming messages ─────────────────────────────────────────────────────

    private void OnViewerMessage(NetworkMessage msg)
    {
        // Any reply at all -- even one we don't otherwise act on -- proves the host is
        // Hello-aware, so the v1-host heuristic in the Disconnected handler above no longer
        // applies to this connection.
        _awaitingHelloResponse = false;

        // What to do with this message is a pure function of its MessageType
        // (MessageDispatch.ClassifyForViewer, exhaustively unit tested in
        // MessageDispatchTests) -- only how each category is carried out below still
        // touches instance state and Dispatcher.UIThread.
        switch (MessageDispatch.ClassifyForViewer(msg.Type))
        {
            case ViewerDispatchAction.ApplyFullScreen:
                ApplyFullScreen(msg.Data);
                ResetFrameTimeoutTimer();
                break;
            case ViewerDispatchAction.ApplyScreenDelta:
                ApplyDelta(ScreenDelta.Deserialize(msg.Data));
                ResetFrameTimeoutTimer();
                break;
            case ViewerDispatchAction.HostHelloAck:
                var hostHello = HelloMessage.Deserialize(msg.Data);
                _hostPeerRole = hostHello.Role;
                _ = _transport?.SendMessageAsync(new NetworkMessage
                {
                    Type = MessageType.Handshake,
                    Data = Encoding.UTF8.GetBytes(_sessionSecret)
                });
                Dispatcher.UIThread.Post(() =>
                {
                    IsConnected  = true;
                    IsConnecting = false;
                    Screen       = AppScreen.LiveScreen;
                    Status       = "Connected";
                    RememberHost(HostAddress.Trim());
                    UpdateWindowTitle();
                    _pingTimer?.Start();
                    StartFrameTimeoutTimer();
                });
                break;
            case ViewerDispatchAction.HostHelloRejected:
                var rejected = HelloRejectedMessage.Deserialize(msg.Data);
                Dispatcher.UIThread.Post(() =>
                {
                    Status = $"Host rejected connection: {rejected.Message}";
                    DisconnectViewer();
                });
                break;
            case ViewerDispatchAction.HandshakeAcknowledged:
                break;
            case ViewerDispatchAction.HandshakeRejected:
                Dispatcher.UIThread.Post(() =>
                {
                    Status = "Host rejected: invalid session token";
                    DisconnectViewer();
                });
                break;
            case ViewerDispatchAction.LatencyPong:
                if (msg.Data.Length >= 8)
                    _lastLatencyMs = (int)Math.Max(0,
                        (DateTime.UtcNow - new DateTime(BitConverter.ToInt64(msg.Data, 0), DateTimeKind.Utc))
                        .TotalMilliseconds);
                break;
            case ViewerDispatchAction.Clipboard:
                string cbText = Encoding.UTF8.GetString(msg.Data);
                if (ClipboardCallback != null)
                    Dispatcher.UIThread.Post(() => _ = ClipboardCallback(cbText));
                break;
            case ViewerDispatchAction.Chat:
                string chat = Encoding.UTF8.GetString(msg.Data);
                Dispatcher.UIThread.Post(() => ReceiveChatMessage(isFromHost: true, text: chat));
                break;
        }
    }

    private void OnHostMessage(NetworkMessage msg)
    {
        if (!_hostHelloComplete)
        {
            HandleHello(msg);
            return;
        }

        if (!_hostHandshakeComplete)
        {
            HandleHandshake(msg);
            return;
        }

        ResetSessionWatchdog();

        // See the matching comment in OnViewerMessage: classification is pure and
        // exhaustively tested (MessageDispatchTests); execution below is not.
        switch (MessageDispatch.ClassifyForHost(msg.Type))
        {
            case HostDispatchAction.MouseMove:
                var mv = MouseMoveMessage.Deserialize(msg.Data);
                Dispatcher.UIThread.Post(() => _input?.MoveMouse(mv.X, mv.Y));
                break;
            case HostDispatchAction.MouseClick:
                var cl = MouseClickMessage.Deserialize(msg.Data);
                Dispatcher.UIThread.Post(() => _input?.Click(cl.X, cl.Y, cl.LeftButton));
                break;
            case HostDispatchAction.MouseButtonDown:
                var bd = MouseClickMessage.Deserialize(msg.Data);
                Dispatcher.UIThread.Post(() => _input?.ButtonDown(bd.X, bd.Y, bd.LeftButton));
                break;
            case HostDispatchAction.MouseButtonUp:
                var bu = MouseClickMessage.Deserialize(msg.Data);
                Dispatcher.UIThread.Post(() => _input?.ButtonUp(bu.X, bu.Y, bu.LeftButton));
                break;
            case HostDispatchAction.KeyPress:
                var kp = KeyMessage.Deserialize(msg.Data);
                Dispatcher.UIThread.Post(() => _input?.SendKey(kp.VirtualKey, true));
                break;
            case HostDispatchAction.KeyRelease:
                var kr = KeyMessage.Deserialize(msg.Data);
                Dispatcher.UIThread.Post(() => _input?.SendKey(kr.VirtualKey, false));
                break;
            case HostDispatchAction.PingReply:
                _ = _hostTransport?.SendMessageAsync(new NetworkMessage { Type = MessageType.Pong, Data = msg.Data });
                break;
            case HostDispatchAction.QualityChanged:
                if (msg.Data.Length >= 4)
                    Dispatcher.UIThread.Post(() => HostQualityIndex = BitConverter.ToInt32(msg.Data, 0));
                break;
            case HostDispatchAction.Clipboard:
                string cbText = Encoding.UTF8.GetString(msg.Data);
                if (ClipboardCallback != null)
                    Dispatcher.UIThread.Post(() => _ = ClipboardCallback(cbText));
                break;
            case HostDispatchAction.Chat:
                string chat = Encoding.UTF8.GetString(msg.Data);
                Dispatcher.UIThread.Post(() => ReceiveChatMessage(isFromHost: false, text: chat));
                break;
        }
    }

    private void HandleHello(NetworkMessage msg)
    {
        if (HelloCompatibility.LooksLikeV1Peer(msg.Type))
        {
            // A v1 viewer has no concept of Hello -- its first message is always Handshake.
            // This is a distinct, positively-identified case, not a generic bad-hello failure:
            // the peer isn't malformed, it's just too old to negotiate at all.
            Dispatcher.UIThread.Post(() =>
            {
                HostStatus = "Viewer is running an older, incompatible version — please update it";
                Status     = "Incompatible viewer version";
            });
            _hostTransport?.Disconnect();
            return;
        }

        if (msg.Type != MessageType.Hello)
        {
            Dispatcher.UIThread.Post(() => { HostStatus = "Bad hello — disconnecting"; Status = "Bad hello"; });
            _hostTransport?.Disconnect();
            return;
        }

        HelloMessage remoteHello;
        try
        {
            remoteHello = HelloMessage.Deserialize(msg.Data);
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() => { HostStatus = "Malformed hello — disconnecting"; Status = "Malformed hello"; });
            _hostTransport?.Disconnect();
            return;
        }

        var rejectReason = HelloNegotiator.Evaluate(OurHello, remoteHello);
        if (rejectReason != null)
        {
            string reasonText = rejectReason == HelloRejectReason.VersionMismatch
                ? $"viewer protocol v{remoteHello.ProtocolVersion} is incompatible with this host's v{ProtocolVersions.Current}"
                : "viewer and host share no compatible codec";
            _ = _hostTransport?.SendMessageAsync(new NetworkMessage
            {
                Type = MessageType.HelloRejected,
                Data = new HelloRejectedMessage { Reason = rejectReason.Value, Message = reasonText }.Serialize()
            });
            Dispatcher.UIThread.Post(() => { HostStatus = $"Rejected viewer: {reasonText}"; Status = "Rejected: " + reasonText; });
            _hostTransport?.Disconnect();
            return;
        }

        _hostHelloComplete = true;
        _ = _hostTransport?.SendMessageAsync(new NetworkMessage
        {
            Type = MessageType.HelloAck,
            Data = OurHello.Serialize()
        });
    }

    private void HandleHandshake(NetworkMessage msg)
    {
        if (msg.Type != MessageType.Handshake)
        {
            Dispatcher.UIThread.Post(() => { HostStatus = "Bad handshake — disconnecting"; Status = "Bad handshake"; });
            _hostTransport?.Disconnect();
            return;
        }

        string secret = Encoding.UTF8.GetString(msg.Data);
        // For a direct (non-router) connection, _expectedSessionSecret is never set and
        // stays "", matching the "" the host sends for the same reason — there is no
        // channel to share a secret out-of-band when a user just types an IP address, so
        // this check is a deliberate no-op on that path, not a bug. The consent dialog is
        // the sole gate for direct connections. See README "Security" for the full trust
        // model of both connection paths.
        if (secret != _expectedSessionSecret)
        {
            _ = _hostTransport?.SendMessageAsync(new NetworkMessage { Type = MessageType.HandshakeFailed });
            Dispatcher.UIThread.Post(() => { HostStatus = "Rejected: invalid token"; Status = "Rejected: invalid session token"; });
            _hostTransport?.Disconnect();
            return;
        }

        _hostHandshakeComplete = true;
        var transport = _hostTransport;
        _ = transport?.SendMessageAsync(new NetworkMessage { Type = MessageType.HandshakeOk });

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            bool allowed = ConsentCallback != null
                ? await ConsentCallback("incoming viewer")
                : true;

            if (!allowed)
            {
                HostStatus = "Connection denied";
                Status     = "Connection denied";
                transport?.Disconnect();
                return;
            }

            StartSessionWatchdog();
            HostStatus    = "Client connected — sharing screen";
            IsHostRunning = true;
            Status        = "Client connected";
            TrayTooltip   = "Hosting — viewer connected";
            ShowViewingBadgeCallback?.Invoke();
        });
    }

    // ── Feature 1: Clipboard sync ─────────────────────────────────────────────

    private async Task SendClipboardAsync()
    {
        string? text = null;
        if (GetClipboardCallback != null)
            text = await GetClipboardCallback();
        if (string.IsNullOrEmpty(text)) return;

        var msg = new NetworkMessage
        {
            Type = MessageType.ClipboardText,
            Data = Encoding.UTF8.GetBytes(text)
        };

        if (_transport?.IsConnected == true)
            await _transport.SendMessageAsync(msg);
        else if (_hostTransport?.IsConnected == true)
            await _hostTransport.SendMessageAsync(msg);
    }

    // ── Feature 4: Chat ───────────────────────────────────────────────────────

    private void SendChatMessage()
    {
        string text = ChatInput.Trim();
        if (string.IsNullOrEmpty(text)) return;
        ChatInput = "";

        bool isHost = _hostTransport?.IsConnected == true;
        ChatMessages.Add(new ChatEntry(DateTime.Now, isHost, text));

        var msg = new NetworkMessage
        {
            Type = MessageType.ChatMessage,
            Data = Encoding.UTF8.GetBytes(text)
        };

        if (_transport?.IsConnected == true)
            _ = _transport.SendMessageAsync(msg);
        else if (_hostTransport?.IsConnected == true)
            _ = _hostTransport.SendMessageAsync(msg);
    }

    private void ReceiveChatMessage(bool isFromHost, string text)
    {
        ChatMessages.Add(new ChatEntry(DateTime.Now, isFromHost, text));
        if (!ChatVisible)
            UnreadChatCount++;
    }

    // ── Feature 9: Viewer quality preset ─────────────────────────────────────

    private void SendViewerQualityPreset(int index)
    {
        if (_transport?.IsConnected != true) return;
        _ = _transport.SendMessageAsync(new NetworkMessage
        {
            Type = MessageType.QualityPreset,
            Data = BitConverter.GetBytes(index)
        });
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
            using var dc    = _canvas!.CreateDrawingContext();
            dc.DrawImage(patch, new Rect(dx, dy, dw, dh));
            RemoteScreen = _canvas;
            _renderedFrames++;
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
        if (_transport?.IsConnected != true || !_hostPeerRole.AcceptsInput()) return;
        if (Math.Abs(rx - _lastMousePos.x) < 1 && Math.Abs(ry - _lastMousePos.y) < 1) return;
        _lastMousePos = (rx, ry);
        await _transport.SendMessageAsync(new NetworkMessage
        {
            Type = MessageType.MouseMove,
            Data = new MouseMoveMessage { X = rx, Y = ry }.Serialize()
        });
    }

    public async void SendMouseClick(int rx, int ry, bool leftButton)
    {
        if (_transport?.IsConnected != true || !_hostPeerRole.AcceptsInput()) return;
        await _transport.SendMessageAsync(new NetworkMessage
        {
            Type = MessageType.MouseClick,
            Data = new MouseClickMessage { X = rx, Y = ry, LeftButton = leftButton, RightButton = !leftButton }.Serialize()
        });
    }

    public async void SendMouseDown(int rx, int ry, bool leftButton)
    {
        if (_transport?.IsConnected != true || !_hostPeerRole.AcceptsInput()) return;
        await _transport.SendMessageAsync(new NetworkMessage
        {
            Type = MessageType.MouseButtonDown,
            Data = new MouseClickMessage { X = rx, Y = ry, LeftButton = leftButton, RightButton = !leftButton }.Serialize()
        });
    }

    public async void SendMouseUp(int rx, int ry, bool leftButton)
    {
        if (_transport?.IsConnected != true || !_hostPeerRole.AcceptsInput()) return;
        await _transport.SendMessageAsync(new NetworkMessage
        {
            Type = MessageType.MouseButtonUp,
            Data = new MouseClickMessage { X = rx, Y = ry, LeftButton = leftButton, RightButton = !leftButton }.Serialize()
        });
    }

    public async void SendKey(int vk, bool isDown)
    {
        if (_transport?.IsConnected != true || vk == 0 || !_hostPeerRole.AcceptsInput()) return;
        await _transport.SendMessageAsync(new NetworkMessage
        {
            Type = isDown ? MessageType.KeyPress : MessageType.KeyRelease,
            Data = new KeyMessage { VirtualKey = vk, IsKeyDown = isDown }.Serialize()
        });
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
            if (_transport?.IsConnected == true)
                await _transport.SendMessageAsync(new NetworkMessage
                {
                    Type = MessageType.Ping,
                    Data = BitConverter.GetBytes(DateTime.UtcNow.Ticks)
                });
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Parses "host", "host:port", "https://host", "https://host:port".
    // The scheme (if any) is preserved on the returned host so the router client
    // can honour https. Default port is 443 for https, otherwise 9000.
    private static (string host, int port) ParseAddress(string addr)
    {
        addr = (addr ?? "").Trim();
        string scheme = "";
        if (addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { scheme = "https://"; addr = addr.Substring(8); }
        else if (addr.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { scheme = "http://"; addr = addr.Substring(7); }
        addr = addr.TrimEnd('/');
        var p = addr.Split(':');
        int defaultPort = scheme == "https://" ? 443 : 9000;
        int port = p.Length > 1 && int.TryParse(p[1], out var pp) ? pp : defaultPort;
        return (scheme + p[0], port);
    }

    private static string GetLocalEndpoint(string routerHost)
    {
        if (routerHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            routerHost == "127.0.0.1")
            return "localhost:8888";
        return $"{GetLanIp()}:8888";
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
            _discoveredHosts.Clear();
            _discoveryCts = new CancellationTokenSource();
            IsScanning = true;
            _ = Task.Run(async () =>
            {
                await NetworkDiscovery.ListenAsync(host =>
                    Dispatcher.UIThread.Post(() => _discoveredHosts.Add(host)),
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
        StopHost();
        DisconnectViewer();
        _metricsTimer?.Dispose();
        _pingTimer?.Dispose();
        _sessionWatchdog?.Dispose();
        _frameTimeoutTimer?.Dispose();
        _announceCts?.Cancel();
        _discoveryCts?.Cancel();
        _canvas?.Dispose();
    }
}
