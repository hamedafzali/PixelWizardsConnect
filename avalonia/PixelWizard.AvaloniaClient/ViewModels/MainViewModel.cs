using System;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using PixelWizard.AvaloniaClient.Platform;
using PixelWizard.AvaloniaClient.Services;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;
using PixelWizard.Transport.Tcp;

namespace PixelWizard.AvaloniaClient.ViewModels;

public enum AppScreen { ModeSelection, Viewer, Host, LiveScreen }

public class MainViewModel : ReactiveObject, IDisposable, IViewerShell, IHostShell
{
    // ── Settings ──────────────────────────────────────────────────────────────

    private readonly AppSettings _settings;

    // ── Window title ──────────────────────────────────────────────────────────

    private string _windowTitle = "PixelWizard Connect";
    public string WindowTitle { get => _windowTitle; set => this.RaiseAndSetIfChanged(ref _windowTitle, value); }

    private void UpdateWindowTitle()
    {
        WindowTitle = _screen switch
        {
            AppScreen.LiveScreen => $"PixelWizard Connect — Viewing {Viewer.HostAddress}",
            AppScreen.Host when Host.IsHostRunning => "PixelWizard Connect — Hosting",
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
    bool IHostShell.TryWriteClipboard(string text)
    {
        if (ClipboardCallback == null) return false;
        _ = ClipboardCallback(text);
        return true;
    }

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
    public bool CanHost => Host.CanHost;
    public string HostCardSubtitle => CanHost
        ? "Share your screen — available on this device"
        : "Share your screen — host not available on this platform";

    // ── Shared state ──────────────────────────────────────────────────────────

    private string _status = "Choose a mode to begin";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    // ── Feature 4: Chat (and clipboard send) ──────────────────────────────────

    public ChatViewModel Chat { get; }

    // ── Viewer role ───────────────────────────────────────────────────────────

    public ViewerViewModel Viewer { get; }

    // ── Host role ─────────────────────────────────────────────────────────────

    public HostViewModel Host { get; }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> GoHostCommand             { get; }
    public ReactiveCommand<Unit, Unit> GoViewerCommand           { get; }
    public ReactiveCommand<Unit, Unit> BackCommand               { get; }
    public ReactiveCommand<Unit, Unit> ToggleSidebarCommand      { get; }

    // ── Internal state ────────────────────────────────────────────────────────

    private readonly IRouterClient _router = new RouterHttpClient();
    private static readonly HelloMessage OurHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };

    public Func<string, Task>?       ClipboardCallback   { get; set; }
    public Func<Task<string?>>?      GetClipboardCallback { get; set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel() : this(AppSettings.Load()) { }

    // Tests pass their own settings so they never touch the user's settings file.
    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        GoHostCommand             = ReactiveCommand.Create(GoHost);
        GoViewerCommand           = ReactiveCommand.Create(() => { Screen = AppScreen.Viewer; });
        BackCommand               = ReactiveCommand.Create(GoBack);
        ToggleSidebarCommand      = ReactiveCommand.Create(() =>
        {
            _sidebarCollapsedWhileViewing = !_sidebarCollapsedWhileViewing;
            this.RaisePropertyChanged(nameof(ShowSidebar));
        });
        Chat = new ChatViewModel(ActiveChatLink,
            () => GetClipboardCallback?.Invoke() ?? Task.FromResult<string?>(null));
        Viewer = new ViewerViewModel(this, Chat, _settings, _router, OurHello);
        Host   = new HostViewModel(this, Chat, _settings, _router, OurHello, _hostProvider);
        Host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HostViewModel.IsHostRunning)) UpdateWindowTitle();
        };
        DismissOnboardingCommand  = ReactiveCommand.Create(DismissOnboarding);

        ShowOnboarding = !_settings.OnboardingSeen;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void GoHost()
    {
        Host.LoadMonitors();
        Screen = AppScreen.Host;
    }

    private void GoBack()
    {
        if (Host.IsHostRunning) Host.Stop();
        Viewer.Disconnect();
        Screen = AppScreen.ModeSelection;
    }

    // ── Feature 1/4: Clipboard and chat go to whichever session is live ───────

    private ChatLink? ActiveChatLink()
    {
        if (Viewer.ActiveChatLink is { } viewer) return viewer;
        return Host.ActiveChatLink;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        Host.Dispose();
        Viewer.Dispose();
    }
}
