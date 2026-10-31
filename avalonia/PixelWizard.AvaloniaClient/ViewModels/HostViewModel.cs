using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using PixelWizard.AvaloniaClient.Services;
using PixelWizard.Core.Interfaces;
using PixelWizard.Core.Models;
using PixelWizard.Media;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Transport.Tcp;
using PixelWizard.Transport.WebSocket;

namespace PixelWizard.AvaloniaClient.ViewModels;

// What the host role needs from the window around it.
public interface IHostShell
{
    string Status { set; }
    // False when there is no clipboard to write to.
    bool TryWriteClipboard(string text);
}

public sealed class HostViewModel : ReactiveObject, IDisposable
{
    private readonly IHostShell     _shell;
    private readonly ChatViewModel  _chat;
    private readonly AppSettings    _settings;
    private readonly IRouterClient  _router;
    private readonly HelloMessage   _ourHello;
    private readonly IHostProvider  _hostProvider;
    private readonly int            _webViewerPort;

    public HostViewModel(IHostShell shell, ChatViewModel chat, AppSettings settings, IRouterClient router,
        HelloMessage ourHello, IHostProvider hostProvider, int webViewerPort = 9001)
    {
        _shell         = shell;
        _chat          = chat;
        _settings      = settings;
        _router        = router;
        _ourHello      = ourHello;
        _hostProvider  = hostProvider;
        _webViewerPort = webViewerPort;

        StartDirectHostCommand    = ReactiveCommand.CreateFromTask(StartDirectHost);
        RegisterHostCommand       = ReactiveCommand.CreateFromTask(RegisterWithRouter);
        StopHostCommand           = ReactiveCommand.Create(Stop);
        CopyCodeCommand           = ReactiveCommand.Create(CopyCode);
        ToggleSessionNotesCommand = ReactiveCommand.Create(() => { SessionNotesVisible = !SessionNotesVisible; });

        if (!string.IsNullOrWhiteSpace(_settings.LastRouterAddress))
            HostRouterAddress = _settings.LastRouterAddress;
    }

    public bool CanHost => _hostProvider.IsAvailable;

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
    public bool IsHostRunning { get => _isHostRunning; set => this.RaiseAndSetIfChanged(ref _isHostRunning, value); }

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

    public void LoadMonitors()
    {
        try { AvailableMonitors = _hostProvider.ListMonitors(); }
        catch { AvailableMonitors = new List<MonitorInfo> { new MonitorInfo(0, "Default", 1920, 1080, true) }; }
    }

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

    public ReactiveCommand<Unit, Unit> StartDirectHostCommand    { get; }
    public ReactiveCommand<Unit, Unit> RegisterHostCommand       { get; }
    public ReactiveCommand<Unit, Unit> StopHostCommand           { get; }
    public ReactiveCommand<Unit, Unit> CopyCodeCommand           { get; }
    public ReactiveCommand<Unit, Unit> ToggleSessionNotesCommand { get; }

    // ── Internal state ────────────────────────────────────────────────────────

    private HostListener?         _hostListener;
    private CaptureLoop?          _captureLoop;
    private IInputInjector?       _input;
    private WebSocketHostServer?  _wsServer;
    private CancellationTokenSource? _announceCts;

    private System.Timers.Timer? _sessionWatchdog;

    private string _expectedSessionSecret = "";
    private int    _activeHostPort = 8888;

    // Decides whether an authenticated viewer may see this screen. Anything but an
    // explicit true denies (see ConsentGate).
    public Func<string, Task<bool>>? ConsentCallback { get; set; }

    // ── Host mode ─────────────────────────────────────────────────────────────

    private async Task StartDirectHost()
    {
        if (!_hostProvider.IsAvailable) { _shell.Status = "Host not available on this platform"; return; }
        if (!int.TryParse(HostPort, out int port)) port = 8888;
        _expectedSessionSecret = "";
        _activeHostPort        = port;
        _shell.Status = "Starting host…";
        try
        {
            SetupHostServices();
            await StartHostListenerAsync(port);
            _captureLoop!.Start();
            _announceCts = new CancellationTokenSource();
            _ = NetworkDiscovery.AnnounceAsync(_activeHostPort, _announceCts.Token);
            string lanIp = GetLanIp();
            HostStatus    = $"Listening — tell viewer to connect to {lanIp}:{port}";
            _shell.Status = $"Host ready. Viewer address: {lanIp}:{port}";
        }
        catch (Exception ex) { _shell.Status = FriendlyError.Describe(ex); }
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
        if (!_hostProvider.IsAvailable) { _shell.Status = "Host not available on this platform"; return; }
        var (host, port) = RouterEndpoint.Parse(HostRouterAddress);
        _shell.Status = "Registering with router…";
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
            HostStatus    = $"Ready — code: {result.ConnectionCode}";
            _shell.Status = $"Host registered. Share code: {result.ConnectionCode}";
        }
        catch (Exception ex) { _shell.Status = FriendlyError.Describe(ex); }
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
        _captureLoop.CaptureError += _ => _shell.Status = "Screen capture error — the session may be unstable.";
        _input   = _hostProvider.CreateInput();

        _wsServer = new WebSocketHostServer(_webViewerPort);
        _wsServer.Log += msg => Dispatcher.UIThread.Post(() => _shell.Status = msg);
        _wsServer.Start();
        WebViewerUrl  = $"http://localhost:{_webViewerPort}/";
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
        _hostListener = new HostListener(() => new TcpTransport(), _ourHello, port, () => HostTlsEnabled, _expectedSessionSecret);
        _hostListener.SessionCreated += WireHostSession;
        return _hostListener.StartAsync();
    }

    private void WireHostSession(HostSession session)
    {
        session.Connected += () => Dispatcher.UIThread.Post(() =>
        {
            HostStatus    = "Viewer connecting — authenticating…";
            _shell.Status = "Authenticating viewer…";
        });
        // The handler is posted, so it can run after Stop (or after a restart). Stop already
        // reset everything; only the listener that created this session may act on it.
        var owner = _hostListener;
        // Relistening disposes this session, which raises Disconnected a second time while
        // the host is already waiting for the next viewer. Only the first one is handled.
        bool disconnectHandled = false;
        session.Disconnected += () => Dispatcher.UIThread.Post(async () =>
        {
            if (_hostListener != owner || disconnectHandled) return;
            disconnectHandled = true;
            _sessionWatchdog?.Stop();
            HideViewingBadgeCallback?.Invoke();
            TrayTooltip = "Hosting — waiting for viewer";
            SaveSessionNotes();
            HostStatus    = "Client disconnected";
            _shell.Status = "Client disconnected";
            if (IsHostRunning && _hostListener is { IsRelistening: false } listener)
            {
                HostStatus    = "Waiting for next connection…";
                _shell.Status = "Waiting for next connection…";
                try { await listener.RelistenAsync(); }
                catch (Exception ex)
                {
                    _shell.Status = FriendlyError.Describe(ex);
                    IsHostRunning = false;
                }
            }
        });
        session.Error += ex => {
            // Only surface error if transport was actually connected — ignore EOF/reset on disconnect
            if (session.IsConnected)
                Dispatcher.UIThread.Post(() => _shell.Status = FriendlyError.Describe(ex));
        };
        // A handler bug, not a transport failure -- the connection survives, so this is
        // surfaced for diagnostics only and never touches Status/IsConnected.
        session.HandlerError += ex => System.Diagnostics.Debug.WriteLine($"[Host] handler error (session continues): {ex}");

        // Dispatch classification lives in HostSession (T9.2b); the handlers below only do
        // what still needs Dispatcher.UIThread or touches instance state HostSession doesn't
        // own (see HostSession's class comment, especially why the consent callback stays
        // here rather than being injected into HostSession).
        session.IncompatiblePeer += () => Dispatcher.UIThread.Post(() =>
        {
            HostStatus    = "Viewer is running an older, incompatible version — please update it";
            _shell.Status = "Incompatible viewer version";
        });
        session.BadHello += () => Dispatcher.UIThread.Post(() => { HostStatus = "Bad hello — disconnecting"; _shell.Status = "Bad hello"; });
        session.MalformedHello += () => Dispatcher.UIThread.Post(() => { HostStatus = "Malformed hello — disconnecting"; _shell.Status = "Malformed hello"; });
        session.HelloRejectedLocally += reasonText => Dispatcher.UIThread.Post(() =>
        {
            HostStatus    = $"Rejected viewer: {reasonText}";
            _shell.Status = "Rejected: " + reasonText;
        });
        session.BadHandshake += () => Dispatcher.UIThread.Post(() => { HostStatus = "Bad handshake — disconnecting"; _shell.Status = "Bad handshake"; });
        session.HandshakeTokenInvalid += () => Dispatcher.UIThread.Post(() => { HostStatus = "Rejected: invalid token"; _shell.Status = "Rejected: invalid session token"; });
        session.HandshakeVerified += () =>
        {
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                bool allowed = await session.ResolveConsentAsync(ConsentCallback, "incoming viewer");

                // The dialog can outlive this session. After Stop or a restart its answer
                // changes nothing; Stop has already reset the host.
                if (_hostListener != owner)
                {
                    session.Disconnect();
                    return;
                }

                if (!allowed)
                {
                    HostStatus    = "Connection denied";
                    _shell.Status = "Connection denied";
                    return;
                }

                // Allowed, but the viewer left while the dialog was open: Disconnected has
                // already run and the host is waiting for the next viewer.
                if (!session.IsApproved) return;

                StartSessionWatchdog();
                HostStatus    = "Client connected — sharing screen";
                IsHostRunning = true;
                _shell.Status = "Client connected";
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
        session.ClipboardReceived       += text => Dispatcher.UIThread.Post(() => _shell.TryWriteClipboard(text));
        session.ChatReceived += text => Dispatcher.UIThread.Post(() => _chat.Receive(isFromHost: false, text));
    }

    public void Stop()
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
        _shell.Status  = "Host stopped";
        TrayTooltip    = "Idle";
        HideViewingBadgeCallback?.Invoke();
        _chat.Reset();
        SessionNotesVisible = false;
    }

    private void CopyCode()
    {
        if (!string.IsNullOrEmpty(HostConnectionCode) && _shell.TryWriteClipboard(HostConnectionCode))
            _shell.Status = "Code copied to clipboard";
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
            HostStatus    = "Session timed out — viewer inactive";
            _shell.Status = "Session timed out";
            _hostListener?.Current?.Disconnect();
        });
        _sessionWatchdog.Start();
    }

    private void ResetSessionWatchdog()
    {
        _sessionWatchdog?.Stop();
        _sessionWatchdog?.Start();
    }

    // ── Feature 1/4: Clipboard and chat go to the approved session only ───────

    public ChatLink? ActiveChatLink => _hostListener?.Current is { IsApproved: true } s
        ? new ChatLink(IsHost: true, s.SendChatAsync, s.SendClipboardAsync)
        : null;

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
        Stop();
        _sessionWatchdog?.Dispose();
        _announceCts?.Cancel();
    }
}
