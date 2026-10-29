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
using PixelWizard.Media;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Transport.Tcp;
using PixelWizard.Transport.WebSocket;

namespace PixelWizard.AvaloniaClient.ViewModels;

public enum AppScreen { ModeSelection, Viewer, Host, LiveScreen }

public class MainViewModel : ReactiveObject, IDisposable, IViewerShell
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
            AppScreen.LiveScreen => $"PixelWizard Connect — Viewing {Viewer.HostAddress}",
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
            this.RaisePropertyChanged(nameof(ShowSidebar));
            UpdateWindowTitle();
        }
    }

    void IViewerShell.ShowLiveScreen() => Screen = AppScreen.LiveScreen;
    void IViewerShell.ShowConnectScreen(bool onlyFromLive)
    {
        if (!onlyFromLive || Screen == AppScreen.LiveScreen) Screen = AppScreen.Viewer;
    }
    void IViewerShell.WriteClipboard(string text) => ClipboardCallback?.Invoke(text);

    public bool ShowModeSelection => _screen == AppScreen.ModeSelection;
    public bool ShowViewerPanel   => _screen == AppScreen.Viewer;
    public bool ShowHostPanel     => _screen == AppScreen.Host;
    public bool ShowScreenDisplay => _screen == AppScreen.LiveScreen;

    // The sidebar is collapsed while viewing so the remote screen gets the full width; the
    // toolbar's Panel button brings it back. Its live stats are duplicated in the toolbar.
    private bool _sidebarCollapsedWhileViewing = true;
    public bool ShowSidebar => !(ShowScreenDisplay && _sidebarCollapsedWhileViewing);

    // ── Platform ──────────────────────────────────────────────────────────────

    private readonly IHostProvider _hostProvider = HostProviderFactory.Create();
    public bool CanHost => _hostProvider.IsAvailable;
    public string HostCardSubtitle => CanHost
        ? "Share your screen — available on this device"
        : "Share your screen — host not available on this platform";

    // ── Shared state ──────────────────────────────────────────────────────────

    private string _status = "Choose a mode to begin";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

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

    // ── Feature 4: Chat (and clipboard send) ──────────────────────────────────

    public ChatViewModel Chat { get; }

    // ── Viewer role ───────────────────────────────────────────────────────────

    public ViewerViewModel Viewer { get; }

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

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> GoHostCommand             { get; }
    public ReactiveCommand<Unit, Unit> GoViewerCommand           { get; }
    public ReactiveCommand<Unit, Unit> BackCommand               { get; }
    public ReactiveCommand<Unit, Unit> StartDirectHostCommand    { get; }
    public ReactiveCommand<Unit, Unit> RegisterHostCommand       { get; }
    public ReactiveCommand<Unit, Unit> StopHostCommand           { get; }
    public ReactiveCommand<Unit, Unit> CopyCodeCommand           { get; }
    public ReactiveCommand<Unit, Unit> ToggleSidebarCommand      { get; }
    public ReactiveCommand<Unit, Unit> ToggleSessionNotesCommand { get; }

    // ── Internal state ────────────────────────────────────────────────────────

    private HostListener?         _hostListener;
    private readonly IRouterClient _router = new RouterHttpClient();
    private CaptureLoop?          _captureLoop;
    private IInputInjector?       _input;
    private WebSocketHostServer?  _wsServer;
    private CancellationTokenSource? _announceCts;

    private System.Timers.Timer? _sessionWatchdog;

    private string _expectedSessionSecret = "";
    private int    _activeHostPort = 8888;

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
        StartDirectHostCommand    = ReactiveCommand.CreateFromTask(StartDirectHost);
        RegisterHostCommand       = ReactiveCommand.CreateFromTask(RegisterWithRouter);
        StopHostCommand           = ReactiveCommand.Create(StopHost);
        CopyCodeCommand           = ReactiveCommand.Create(CopyCode);
        ToggleSidebarCommand      = ReactiveCommand.Create(() =>
        {
            _sidebarCollapsedWhileViewing = !_sidebarCollapsedWhileViewing;
            this.RaisePropertyChanged(nameof(ShowSidebar));
        });
        Chat = new ChatViewModel(ActiveChatLink,
            () => GetClipboardCallback?.Invoke() ?? Task.FromResult<string?>(null));
        Viewer = new ViewerViewModel(this, Chat, _settings, _router, OurHello);
        ToggleSessionNotesCommand = ReactiveCommand.Create(() => { SessionNotesVisible = !SessionNotesVisible; });
        DismissOnboardingCommand  = ReactiveCommand.Create(DismissOnboarding);

        // Restore persisted preferences.
        if (!string.IsNullOrWhiteSpace(_settings.LastRouterAddress))
            HostRouterAddress = _settings.LastRouterAddress;
        ShowOnboarding = !_settings.OnboardingSeen;
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
        Viewer.Disconnect();
        Screen = AppScreen.ModeSelection;
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
            await StartHostListenerAsync(port);
            _captureLoop!.Start();
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
        var (host, port) = RouterEndpoint.Parse(HostRouterAddress);
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
            await StartHostListenerAsync(8888);
            _captureLoop!.Start();
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
        var capture  = _hostProvider.CreateCapture(settings.FullRefreshInterval, SelectedMonitorIndex);
        _captureLoop = new CaptureLoop(
            capture,
            () => StreamingSettings.FromPresetIndex(HostQualityIndex),
            () => _hostListener?.Current?.IsApproved == true);
        _captureLoop.DeltaCapturedAsync = async (delta, full) =>
        {
            await _hostListener!.Current!.SendFrameAsync(delta, full);

            if (_wsServer != null)
            {
                if (full) await _wsServer.BroadcastFrameAsync(delta.ImageData);
                else      await _wsServer.BroadcastDeltaAsync(delta);
            }
        };
        _captureLoop.CaptureError += _ => Status = "Screen capture error — the session may be unstable.";
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

    private Task StartHostListenerAsync(int port)
    {
        // Listen/relisten mechanics and per-connection HostSession creation live in
        // HostListener (T9.3b); whether to relisten stays here (it depends on IsHostRunning).
        _hostListener = new HostListener(() => new TcpTransport(), OurHello, port, () => HostTlsEnabled, _expectedSessionSecret);
        _hostListener.SessionCreated += WireHostSession;
        return _hostListener.StartAsync();
    }

    private void WireHostSession(HostSession session)
    {
        session.Connected += () => Dispatcher.UIThread.Post(() =>
        {
            HostStatus = "Viewer connecting — authenticating…";
            Status     = "Authenticating viewer…";
        });
        session.Disconnected += () => Dispatcher.UIThread.Post(async () =>
        {
            _sessionWatchdog?.Stop();
            HideViewingBadgeCallback?.Invoke();
            TrayTooltip = "Hosting — waiting for viewer";
            SaveSessionNotes();
            HostStatus = "Client disconnected";
            Status     = "Client disconnected";
            if (IsHostRunning && _hostListener is { IsRelistening: false } listener)
            {
                HostStatus = "Waiting for next connection…";
                Status     = "Waiting for next connection…";
                try { await listener.RelistenAsync(); }
                catch (Exception ex)
                {
                    Status = FriendlyError.Describe(ex);
                    IsHostRunning = false;
                }
            }
        });
        session.Error += ex => {
            // Only surface error if transport was actually connected — ignore EOF/reset on disconnect
            if (session.IsConnected)
                Dispatcher.UIThread.Post(() => Status = FriendlyError.Describe(ex));
        };
        // A handler bug, not a transport failure -- the connection survives, so this is
        // surfaced for diagnostics only and never touches Status/IsConnected.
        session.HandlerError += ex => System.Diagnostics.Debug.WriteLine($"[Host] handler error (session continues): {ex}");

        // Dispatch classification lives in HostSession (T9.2b); MainViewModel's handlers
        // below only do what still needs Dispatcher.UIThread or touches instance state
        // HostSession doesn't own (see HostSession's class comment, especially why the
        // consent callback stays here rather than being injected into HostSession).
        session.IncompatiblePeer += () => Dispatcher.UIThread.Post(() =>
        {
            HostStatus = "Viewer is running an older, incompatible version — please update it";
            Status     = "Incompatible viewer version";
        });
        session.BadHello += () => Dispatcher.UIThread.Post(() => { HostStatus = "Bad hello — disconnecting"; Status = "Bad hello"; });
        session.MalformedHello += () => Dispatcher.UIThread.Post(() => { HostStatus = "Malformed hello — disconnecting"; Status = "Malformed hello"; });
        session.HelloRejectedLocally += reasonText => Dispatcher.UIThread.Post(() =>
        {
            HostStatus = $"Rejected viewer: {reasonText}";
            Status     = "Rejected: " + reasonText;
        });
        session.BadHandshake += () => Dispatcher.UIThread.Post(() => { HostStatus = "Bad handshake — disconnecting"; Status = "Bad handshake"; });
        session.HandshakeTokenInvalid += () => Dispatcher.UIThread.Post(() => { HostStatus = "Rejected: invalid token"; Status = "Rejected: invalid session token"; });
        session.HandshakeVerified += () =>
        {
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                bool allowed = await session.ResolveConsentAsync(ConsentCallback, "incoming viewer");

                if (!allowed)
                {
                    HostStatus = "Connection denied";
                    Status     = "Connection denied";
                    return;
                }

                StartSessionWatchdog();
                HostStatus    = "Client connected — sharing screen";
                IsHostRunning = true;
                Status        = "Client connected";
                TrayTooltip   = "Hosting — viewer connected";
                ShowViewingBadgeCallback?.Invoke();
            });
        };
        session.PostHandshakeMessageReceived += ResetSessionWatchdog;
        session.MouseMoveReceived       += mv => Dispatcher.UIThread.Post(() => _input?.MoveMouse(mv.X, mv.Y));
        session.MouseClickReceived      += cl => Dispatcher.UIThread.Post(() => _input?.Click(cl.X, cl.Y, cl.LeftButton));
        session.MouseButtonDownReceived += bd => Dispatcher.UIThread.Post(() => _input?.ButtonDown(bd.X, bd.Y, bd.LeftButton));
        session.MouseButtonUpReceived   += bu => Dispatcher.UIThread.Post(() => _input?.ButtonUp(bu.X, bu.Y, bu.LeftButton));
        session.KeyPressReceived        += kp => Dispatcher.UIThread.Post(() => _input?.SendKey(kp.VirtualKey, true));
        session.KeyReleaseReceived      += kr => Dispatcher.UIThread.Post(() => _input?.SendKey(kr.VirtualKey, false));
        session.QualityChanged          += q  => Dispatcher.UIThread.Post(() => HostQualityIndex = q);
        session.ClipboardReceived       += text =>
        {
            if (ClipboardCallback != null)
                Dispatcher.UIThread.Post(() => _ = ClipboardCallback(text));
        };
        session.ChatReceived += text => Dispatcher.UIThread.Post(() => Chat.Receive(isFromHost: false, text));
    }

    private void StopHost()
    {
        SaveSessionNotes();
        _announceCts?.Cancel();
        _announceCts = null;
        _sessionWatchdog?.Stop();
        _sessionWatchdog?.Dispose();
        _sessionWatchdog = null;
        _hostListener?.Stop();
        _captureLoop?.Dispose();
        _wsServer?.Stop();
        _hostListener  = null;
        _captureLoop   = null;
        _input         = null;
        _wsServer      = null;
        IsHostRunning  = false;
        ShowCodeCard   = false;
        ShowWebViewer  = false;
        HostStatus     = "Stopped";
        Status         = "Host stopped";
        TrayTooltip    = "Idle";
        HideViewingBadgeCallback?.Invoke();
        Chat.Reset();
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
            _hostListener?.Current?.Disconnect();
        });
        _sessionWatchdog.Start();
    }

    private void ResetSessionWatchdog()
    {
        _sessionWatchdog?.Stop();
        _sessionWatchdog?.Start();
    }

    // ── Incoming messages ─────────────────────────────────────────────────────
    // Dispatch (formerly OnViewerMessage/OnHostMessage/HandleHello/HandleHandshake) moved
    // to PixelWizard.Session's ViewerSession (T9.2a) and HostSession (T9.2b) -- see
    // BuildViewerSession/WireHostSession for the event wiring.

    // ── Feature 1/4: Clipboard and chat go to whichever session is live ───────

    private ChatLink? ActiveChatLink()
    {
        if (Viewer.ActiveChatLink is { } viewer) return viewer;
        if (_hostListener?.Current is { IsApproved: true } host)
            return new ChatLink(IsHost: true, host.SendChatAsync, host.SendClipboardAsync);
        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetLocalEndpoint(string routerHost)
    {
        if (routerHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            routerHost == "127.0.0.1")
            return "localhost:8888";
        return $"{GetLanIp()}:8888";
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        StopHost();
        Viewer.Dispose();
        _sessionWatchdog?.Dispose();
        _announceCts?.Cancel();
    }
}
