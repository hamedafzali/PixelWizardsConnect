using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PixelWizard.AvaloniaClient.Services;
using PixelWizard.AvaloniaClient.ViewModels;
using PixelWizard.Core.Interfaces;
using PixelWizard.Core.Models;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.AvaloniaClient.Tests;

public sealed class HostViewModelTests : IDisposable
{
    private static readonly HelloMessage OurHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };

    private sealed class FakeShell : IHostShell
    {
        public readonly List<string> Statuses = new();
        public readonly List<string> Clipboard = new();
        public bool HasClipboard = true;
        public string Status { set => Statuses.Add(value); }
        public bool TryWriteClipboard(string text)
        {
            if (!HasClipboard) return false;
            Clipboard.Add(text);
            return true;
        }
    }

    private sealed class FakeCapture : IScreenCapture
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public (int Width, int Height) Resolution => (4, 4);
        public List<ScreenDelta> Capture(bool forceFullFrame, long jpegQuality)
        {
            Interlocked.Increment(ref _calls);
            return new List<ScreenDelta> { new() { X = 0, Y = 0, Width = 4, Height = 4, ImageData = new byte[] { 1, 2, 3 } } };
        }
        public void Reset() { }
        public void Dispose() { }
    }

    private sealed class FakeInput : IInputInjector
    {
        public readonly List<string> Calls = new();
        private void Add(string s) { lock (Calls) Calls.Add(s); }
        public int Count { get { lock (Calls) return Calls.Count; } }
        public void MoveMouse(int x, int y) => Add($"move {x},{y}");
        public void Click(int x, int y, bool leftButton) => Add($"click {x},{y}");
        public void ButtonDown(int x, int y, bool leftButton) => Add($"down {x},{y}");
        public void ButtonUp(int x, int y, bool leftButton) => Add($"up {x},{y}");
        public void SendKey(int virtualKey, bool isKeyDown) => Add($"key {virtualKey} {isKeyDown}");
        public void Scroll(int x, int y, int deltaX, int deltaY) => Add($"scroll {x},{y} {deltaX},{deltaY}");
    }

    private sealed class FakeProvider : IHostProvider
    {
        public bool IsAvailable { get; set; } = true;
        public bool MonitorsThrow;
        public readonly FakeCapture Capture = new();
        public readonly FakeInput Input = new();
        public IReadOnlyList<MonitorInfo> ListMonitors() => MonitorsThrow
            ? throw new InvalidOperationException("no monitors")
            : new List<MonitorInfo> { new(0, "Main", 800, 600, true), new(1, "Side", 640, 480, false) };
        public IScreenCapture CreateCapture(TimeSpan fullRefreshInterval, int monitorIndex = 0) => Capture;
        public IInputInjector CreateInput() => Input;
    }

    private readonly string _settingsFile =
        Path.Combine(Path.GetTempPath(), "pw-host-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly FakeShell _shell = new();
    private readonly FakeProvider _provider = new();
    private readonly ChatViewModel _chat;
    private readonly List<IDisposable> _cleanup = new();
    private int _badgeShown, _badgeHidden;

    public HostViewModelTests()
    {
        _chat = new ChatViewModel(() => null, () => Task.FromResult<string?>(null));
    }

    private readonly FakeRelayConnector _relay = new();

    private HostViewModel Vm(AppSettings? settings = null, IRelayConnector? relay = null)
    {
        var vm = new HostViewModel(_shell, _chat, settings ?? AppSettings.Load(_settingsFile), relay ?? _relay,
            OurHello, _provider, webViewerPort: GetFreePort());
        vm.ShowViewingBadgeCallback = () => _badgeShown++;
        vm.HideViewingBadgeCallback = () => _badgeHidden++;
        _cleanup.Add(vm);
        return vm;
    }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_cleanup)) d.Dispose();
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

    // What the host says once an approved viewer leaves; one that never got in leaves it plain.
    private const string ViewerLeft = "Viewer disconnected — waiting for next connection…";
    private static bool IsWaitingForAViewer(string status) =>
        status.EndsWith("aiting for next connection…", StringComparison.Ordinal);

    // Keeps the UI thread pumping for a while, so anything that was going to arrive has.
    private static async Task Settle(int ms = 400)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private async Task<int> StartHost(HostViewModel vm)
    {
        int port = GetFreePort();
        vm.HostPort = port.ToString();
        vm.HostTlsEnabled = false;
        vm.StartDirectHostCommand.Execute().Subscribe();
        // Listening is reported once the port is bound, without waiting for a viewer.
        await WaitUntil(() => vm.HostStatus.StartsWith("Listening"), "host to start");
        return port;
    }

    private sealed class Viewer
    {
        public readonly ViewerSession Session;
        public int Frames;
        public bool Disconnected;
        public readonly List<string> Chat = new();
        public Viewer(ViewerSession session)
        {
            Session = session;
            session.FullScreenReceived  += _ => Interlocked.Increment(ref Frames);
            session.ScreenDeltaReceived += _ => Interlocked.Increment(ref Frames);
            session.Disconnected        += () => Disconnected = true;
            session.ChatReceived        += t => { lock (Chat) Chat.Add(t); };
        }
    }

    private async Task<Viewer> ConnectViewer(int port)
    {
        var session = new ViewerSession(() => new TcpTransport(), OurHello);
        _cleanup.Add(session);
        var viewer = new Viewer(session);
        await session.ConnectAsync("127.0.0.1", port, useTls: false);
        return viewer;
    }

    // Everything a viewer can push at the host.
    private static async Task SendEverything(ViewerSession s, int tag)
    {
        await s.SendMouseMoveAsync(tag, tag);
        await s.SendMouseDownAsync(tag, tag, true);
        await s.SendMouseUpAsync(tag, tag, true);
        await s.SendMouseClickAsync(tag, tag, true);
        await s.SendMouseWheelAsync(tag, tag, -tag, 120);
        await s.SendKeyAsync(tag, true);
        await s.SendKeyAsync(tag, false);
        await s.SendQualityPresetAsync(3);
        await s.SendClipboardAsync("clip " + tag);
        await s.SendChatAsync("chat " + tag);
    }

    // ── Form and settings ─────────────────────────────────────────────────────

    [Fact]
    public void RestoresRouterAddress_FromSettings()
    {
        var settings = AppSettings.Load(_settingsFile);
        settings.LastRouterAddress = "https://router.example";
        Assert.Equal("https://router.example", Vm(settings).HostRouterAddress);
    }

    [Fact]
    public void RouterToggle_SwitchesPanels()
    {
        var vm = Vm();
        Assert.True(vm.ShowDirectHostPanel);
        vm.UseRouterForHost = true;
        Assert.True(vm.ShowRouterHostPanel);
        Assert.False(vm.ShowDirectHostPanel);
    }

    [Fact]
    public void LoadMonitors_FallsBackToADefault_WhenListingFails()
    {
        var vm = Vm();
        vm.LoadMonitors();
        Assert.Equal(2, vm.AvailableMonitors.Count);
        _provider.MonitorsThrow = true;
        vm.LoadMonitors();
        Assert.Equal("Default", Assert.Single(vm.AvailableMonitors).Name);
    }

    [AvaloniaFact]
    public async Task StartDirectHost_Unavailable_OnlySetsStatus()
    {
        _provider.IsAvailable = false;
        var vm = Vm();
        Assert.False(vm.CanHost);
        await vm.StartDirectHostCommand.Execute();
        Assert.Equal("Host not available on this platform", _shell.Statuses.Last());
        Assert.False(vm.IsHostRunning);
    }

    [Theory]
    [InlineData(true, "", false)]
    [InlineData(false, "ABC123", false)]
    [InlineData(true, "ABC123", true)]
    public async Task CopyCode_NeedsACodeAndAClipboard(bool hasClipboard, string code, bool copied)
    {
        _shell.HasClipboard = hasClipboard;
        var vm = Vm();
        vm.HostConnectionCode = code;
        await vm.CopyCodeCommand.Execute();
        Assert.Equal(copied, _shell.Statuses.Contains("Code copied to clipboard"));
        Assert.Equal(copied ? new[] { code } : Array.Empty<string>(), _shell.Clipboard);
    }

    // ── Starting (over a real socket) ─────────────────────────────────────────

    private const string PortInUse =
        "That port is already in use by another app. Choose a different port, or close the app using it.";

    [AvaloniaFact]
    public async Task StartDirectHost_NoViewer_IsReadyOnceBound()
    {
        var vm = Vm();
        int port = GetFreePort();
        vm.HostPort = port.ToString();
        vm.HostTlsEnabled = false;

        await vm.StartDirectHostCommand.Execute();

        Assert.True(vm.IsHostRunning);
        Assert.StartsWith("Listening — tell viewer to connect to ", vm.HostStatus);
        Assert.EndsWith($":{port}", vm.HostStatus);
        Assert.StartsWith("Host ready. Viewer address: ", _shell.Statuses.Last());
    }

    [AvaloniaFact]
    public async Task StartDirectHost_PortInUse_ShowsWhy_StopsAndDoesNotRelisten()
    {
        var blocker = new TcpListener(IPAddress.Any, 0);
        blocker.Start();
        try
        {
            var vm = Vm();
            vm.HostPort = ((IPEndPoint)blocker.LocalEndpoint).Port.ToString();
            vm.HostTlsEnabled = false;

            await vm.StartDirectHostCommand.Execute();
            int statuses = _shell.Statuses.Count;
            await Settle();

            Assert.Equal(PortInUse, _shell.Statuses.Last());
            Assert.Equal(statuses, _shell.Statuses.Count); // no relisten loop behind it
            Assert.DoesNotContain(_shell.Statuses, IsWaitingForAViewer);
            Assert.False(vm.IsHostRunning);
            Assert.Equal("Stopped", vm.HostStatus);
        }
        finally { blocker.Stop(); }
    }

    // ── Router mode (over /signal + WebRTC, faked here) ───────────────────────

    private async Task<(HostViewModel Vm, FakeRegistration Reg)> StartRouterHost(Func<string, Task<bool>>? decider)
    {
        var vm = Vm();
        vm.ConsentCallback = decider;
        vm.UseRouterForHost = true;
        vm.HostRouterAddress = "router.example.org:9000";
        await vm.RegisterHostCommand.Execute();
        FakeRegistration reg;
        lock (_relay.Registrations) reg = _relay.Registrations[0];
        return (vm, reg);
    }

    private async Task<(ViewerSession Viewer, MemoryTransport T, Viewer Probe)> JoinRouterViewer(FakeRegistration reg)
    {
        var t = _relay.Join(reg);
        var session = new ViewerSession(() => t, OurHello);
        _cleanup.Add(session);
        var probe = new Viewer(session);
        await t.EstablishAsync();
        return (session, t, probe);
    }

    [AvaloniaFact]
    public async Task RouterHost_ShowsTheCode_AndStartsNothingBeforeAViewerIsApproved()
    {
        var (vm, reg) = await StartRouterHost(_ => Task.FromResult(true));
        await Settle();

        Assert.Equal("ROOM01", vm.HostConnectionCode);
        Assert.True(vm.ShowCodeCard);
        Assert.True(vm.IsHostRunning);
        Assert.Equal("Ready — code: ROOM01", vm.HostStatus);
        Assert.Equal(1, _relay.RegisterCalls);
        Assert.Equal(0, _provider.Capture.Calls);
        Assert.Null(vm.ActiveChatLink);
    }

    [AvaloniaFact]
    public async Task RouterHost_SessionReachesTheConsentDecider_AndCaptureStartsOnlyAfterApproval()
    {
        var decision = new TaskCompletionSource<bool>();
        var asked = new List<string>();
        var (vm, reg) = await StartRouterHost(who => { asked.Add(who); return decision.Task; });

        var (_, _, probe) = await JoinRouterViewer(reg);
        await WaitUntil(() => asked.Count == 1, "the consent decider to be asked");
        await Settle();

        // The modal is open: nothing is captured or sent, and nothing has approved.
        Assert.Equal(new[] { "incoming viewer" }, asked);
        Assert.Equal(0, _provider.Capture.Calls);
        Assert.Equal(0, Volatile.Read(ref probe.Frames));
        Assert.Null(vm.ActiveChatLink);
        Assert.Equal(0, _badgeShown);

        decision.SetResult(true);
        await WaitUntil(() => vm.HostStatus == "Client connected — sharing screen", "approval");
        await WaitUntil(() => Volatile.Read(ref probe.Frames) > 0, "frames at the viewer");
        Assert.True(_provider.Capture.Calls > 0);
        Assert.Equal(1, _badgeShown);
    }

    [AvaloniaTheory]
    [InlineData(Refusal.Deny)]
    [InlineData(Refusal.NoCallback)]
    [InlineData(Refusal.Throws)]
    [InlineData(Refusal.Faults)]
    public async Task RouterHost_NotExplicitlyAllowed_DisconnectsTheTransport_AndSharesNothing(Refusal refusal)
    {
        int asked = 0;
        var decider = Decider(refusal);
        var (vm, reg) = await StartRouterHost(decider == null ? null : who => { asked++; return decider(who); });

        var (_, viewerT, probe) = await JoinRouterViewer(reg);
        await WaitUntil(() => probe.Disconnected, "the viewer to be dropped");
        await WaitUntil(() => _shell.Statuses.Contains("Connection denied"), "denial");
        await Settle();

        if (decider != null) Assert.Equal(1, asked);
        Assert.False(viewerT.IsConnected);
        Assert.Equal(0, Volatile.Read(ref probe.Frames));
        Assert.Equal(0, _provider.Capture.Calls);
        Assert.DoesNotContain("Client connected", _shell.Statuses);
        Assert.Equal(0, _badgeShown);
        Assert.Null(vm.ActiveChatLink);
        // The host goes back to waiting, with a new one-time code.
        await WaitUntil(() => vm.HostConnectionCode == "ROOM02", "a fresh registration");
        Assert.True(vm.IsHostRunning);
    }

    [AvaloniaFact]
    public async Task RouterHost_AfterAnApprovedViewerLeaves_RegistersAgainAndAsksConsentAgain()
    {
        int asked = 0;
        var (vm, reg) = await StartRouterHost(_ => { asked++; return Task.FromResult(true); });
        var (_, viewerT, _) = await JoinRouterViewer(reg);
        await WaitUntil(() => vm.HostStatus == "Client connected — sharing screen", "approval");

        viewerT.Disconnect();
        await WaitUntil(() => vm.HostConnectionCode == "ROOM02", "a fresh registration");
        await WaitUntil(() => vm.HostStatus == "Ready — code: ROOM02", "ready again");
        Assert.True(reg.Disposed);
        Assert.Equal(1, _badgeHidden);
        Assert.Null(vm.ActiveChatLink);

        FakeRegistration second;
        lock (_relay.Registrations) second = _relay.Registrations[1];
        var captured = _provider.Capture.Calls;
        await JoinRouterViewer(second);
        await WaitUntil(() => asked == 2, "a second consent question");
    }

    [AvaloniaFact]
    public async Task RouterHost_Stop_DisposesTheRegistration_AndDoesNotRegisterAgain()
    {
        var (vm, reg) = await StartRouterHost(_ => Task.FromResult(true));
        vm.Stop();
        await Settle();

        Assert.True(reg.Disposed);
        Assert.False(vm.IsHostRunning);
        Assert.Equal("Stopped", vm.HostStatus);
        Assert.Equal(1, _relay.RegisterCalls);
    }

    [AvaloniaFact]
    public async Task RouterHost_RouterUnreachable_ShowsWhy_AndStops()
    {
        _relay.RegisterFails = new System.Net.WebSockets.WebSocketException("refused");
        var vm = Vm();
        vm.UseRouterForHost = true;
        vm.HostRouterAddress = "router.example.org:9000";

        await vm.RegisterHostCommand.Execute();

        Assert.Equal("Could not reach the router server. Check the router address.", _shell.Statuses.Last());
        Assert.False(vm.IsHostRunning);
        Assert.False(vm.ShowCodeCard);
    }

    [AvaloniaFact]
    public async Task RouterHost_RemembersTheRouterAddress()
    {
        var settings = AppSettings.Load(_settingsFile);
        var vm = Vm(settings);
        vm.UseRouterForHost = true;
        vm.HostRouterAddress = "https://router.example.org";
        await vm.RegisterHostCommand.Execute();
        Assert.Equal("https://router.example.org", settings.LastRouterAddress);
    }

    // ── Consent (over a real socket) ──────────────────────────────────────────

    [AvaloniaFact]
    public async Task Consent_Allowed_SharesScreen_AndAcceptsInput()
    {
        var vm = Vm();
        var asked = new List<string>();
        vm.ConsentCallback = who => { asked.Add(who); return Task.FromResult(true); };
        int port = await StartHost(vm);
        Assert.Equal("Hosting — waiting for viewer", vm.TrayTooltip);

        var viewer = await ConnectViewer(port);
        await WaitUntil(() => vm.HostStatus == "Client connected — sharing screen", "approval");

        Assert.Equal(new[] { "incoming viewer" }, asked);
        Assert.Equal("Client connected", _shell.Statuses.Last());
        Assert.Equal("Hosting — viewer connected", vm.TrayTooltip);
        Assert.Equal(1, _badgeShown);
        Assert.True(vm.ActiveChatLink!.IsHost);

        await WaitUntil(() => Volatile.Read(ref viewer.Frames) > 0, "frames at the viewer");

        await SendEverything(viewer.Session, 7);
        await WaitUntil(() => _provider.Input.Count == 7, "input on the host");
        await WaitUntil(() => _chat.Messages.Count == 1 && _shell.Clipboard.Count == 1, "chat and clipboard");
        Assert.Equal(new[] { "move 7,7", "down 7,7", "up 7,7", "click 7,7", "scroll 7,7 -7,120", "key 7 True", "key 7 False" }, _provider.Input.Calls);
        Assert.Equal("chat 7", _chat.Messages[0].Text);
        Assert.False(_chat.Messages[0].IsHost);
        Assert.Equal("clip 7", _shell.Clipboard[0]);
        await WaitUntil(() => vm.HostQualityIndex == 3, "quality change");

        await vm.ActiveChatLink!.SendChat("hi viewer");
        await WaitUntil(() => { lock (viewer.Chat) return viewer.Chat.Count == 1; }, "chat at the viewer");
    }

    public enum Refusal { Deny, NoCallback, Throws, Faults }

    private static Func<string, Task<bool>>? Decider(Refusal r) => r switch
    {
        Refusal.Deny       => _ => Task.FromResult(false),
        Refusal.NoCallback => null,
        Refusal.Throws     => _ => throw new InvalidOperationException("dialog blew up"),
        Refusal.Faults     => _ => Task.FromException<bool>(new InvalidOperationException("dialog faulted")),
        _ => throw new ArgumentOutOfRangeException(nameof(r))
    };

    [AvaloniaTheory]
    [InlineData(Refusal.Deny)]
    [InlineData(Refusal.NoCallback)]
    [InlineData(Refusal.Throws)]
    [InlineData(Refusal.Faults)]
    public async Task Consent_NotExplicitlyAllowed_DisconnectsEveryViewer_AndSharesNothing(Refusal refusal)
    {
        var vm = Vm();
        int asked = 0;
        var decider = Decider(refusal);
        vm.ConsentCallback = decider == null ? null : who => { asked++; return decider(who); };
        int port = await StartHost(vm);

        // Two viewers in a row: the relistened session goes through consent again.
        for (int n = 1; n <= 2; n++)
        {
            var viewer = await ConnectViewer(port);
            await WaitUntil(() => viewer.Disconnected, $"viewer {n} to be dropped");
            await WaitUntil(() => _shell.Statuses.Count(s => s == "Connection denied") == n, $"denial {n}");
            await WaitUntil(() => _shell.Statuses.Count(s => s == "Waiting for next connection…") == n, $"relisten {n}");
            await Task.Delay(50); // let the relistened listener bind
            Assert.Equal(0, Volatile.Read(ref viewer.Frames));
        }

        if (decider != null) Assert.Equal(2, asked);
        Assert.DoesNotContain("Client connected", _shell.Statuses);
        Assert.Equal(0, _badgeShown);
        Assert.Equal(0, _provider.Capture.Calls);
        Assert.Null(vm.ActiveChatLink);
        Assert.True(vm.IsHostRunning);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consent_WhilePending_NothingFlowsEitherWay_AndNothingIsReplayedAfter(bool thenAllow)
    {
        var vm = Vm();
        var decision = new TaskCompletionSource<bool>();
        int asked = 0;
        vm.ConsentCallback = _ => { asked++; return decision.Task; };
        int port = await StartHost(vm);

        var viewer = await ConnectViewer(port);
        await WaitUntil(() => asked == 1, "the consent prompt");

        await SendEverything(viewer.Session, 1);
        await Settle();

        Assert.Equal(0, _provider.Input.Count);
        Assert.Empty(_chat.Messages);
        Assert.Empty(_shell.Clipboard);
        Assert.Equal(1, vm.HostQualityIndex);
        Assert.Equal(0, _provider.Capture.Calls);
        Assert.Equal(0, Volatile.Read(ref viewer.Frames));
        Assert.Null(vm.ActiveChatLink);
        Assert.Equal(0, _badgeShown);
        Assert.False(viewer.Disconnected);

        decision.SetResult(thenAllow);

        if (!thenAllow)
        {
            await WaitUntil(() => viewer.Disconnected, "viewer to be dropped");
            Assert.Equal(0, _provider.Input.Count);
            Assert.Equal(0, Volatile.Read(ref viewer.Frames));
            return;
        }

        await WaitUntil(() => vm.HostStatus == "Client connected — sharing screen", "approval");
        await viewer.Session.SendMouseMoveAsync(2, 2);
        await WaitUntil(() => _provider.Input.Count > 0, "input after approval");
        await Settle(100);
        Assert.Equal(new[] { "move 2,2" }, _provider.Input.Calls);
        Assert.Empty(_chat.Messages);
        Assert.Empty(_shell.Clipboard);
        await WaitUntil(() => Volatile.Read(ref viewer.Frames) > 0, "frames after approval");
    }

    // The consent dialog can outlive its session. Stop while it is open has already reset the
    // host; the late answer must not bring a session, status or badge back.
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Consent_AnsweredAfterStop_ChangesNothing(bool allow)
    {
        var vm = Vm();
        var decision = new TaskCompletionSource<bool>();
        int asked = 0;
        vm.ConsentCallback = _ => { asked++; return decision.Task; };
        int port = await StartHost(vm);
        var viewer = await ConnectViewer(port);
        await WaitUntil(() => asked == 1, "the consent prompt");

        vm.Stop();
        int afterStop = _shell.Statuses.Count;
        await WaitUntil(() => viewer.Disconnected, "viewer to be dropped");
        decision.SetResult(allow);
        await Settle();

        Assert.False(vm.IsHostRunning);
        Assert.Equal("Stopped", vm.HostStatus);
        Assert.Equal("Idle", vm.TrayTooltip);
        Assert.Equal(0, _badgeShown);
        Assert.Empty(_shell.Statuses.Skip(afterStop));
        Assert.Equal(0, Volatile.Read(ref viewer.Frames));
    }

    // The viewer gives up while the dialog is open. Allowing it afterwards must not claim a
    // viewer is connected: the host keeps waiting for the next one.
    [AvaloniaFact]
    public async Task Consent_AllowedAfterTheViewerLeft_KeepsWaitingForTheNextViewer()
    {
        var vm = Vm();
        var decision = new TaskCompletionSource<bool>();
        int asked = 0;
        vm.ConsentCallback = _ => { asked++; return decision.Task; };
        int port = await StartHost(vm);
        var viewer = await ConnectViewer(port);
        await WaitUntil(() => asked == 1, "the consent prompt");

        viewer.Session.Disconnect();
        await WaitUntil(() => _shell.Statuses.Contains("Waiting for next connection…"), "host to notice");
        await Settle();
        int afterLeave = _shell.Statuses.Count;
        decision.SetResult(true);
        await Settle();

        Assert.True(vm.IsHostRunning);
        Assert.Equal("Hosting — waiting for viewer", vm.TrayTooltip);
        Assert.Equal(0, _badgeShown);
        Assert.Empty(_shell.Statuses.Skip(afterLeave));
        Assert.Null(vm.ActiveChatLink);
    }

    // Relistening disposes the old session, which raises its Disconnected a second time.
    // The host reports the viewer leaving once and is left waiting for the next one.
    [AvaloniaFact]
    public async Task ViewerLeaves_HandledOnce_HostWaitsForTheNextViewer()
    {
        var vm = Vm();
        vm.ConsentCallback = _ => Task.FromResult(true);
        int port = await StartHost(vm);
        var viewer = await ConnectViewer(port);
        await WaitUntil(() => _badgeShown == 1, "approval");
        int afterApproval = _shell.Statuses.Count;

        viewer.Session.Disconnect();
        await WaitUntil(() => _shell.Statuses.Contains(ViewerLeft), "host to notice");
        await Settle();

        Assert.Equal(new[] { "Client disconnected", ViewerLeft },
                     _shell.Statuses.Skip(afterApproval));
        Assert.Equal(ViewerLeft, vm.HostStatus);
        Assert.Equal("Hosting — waiting for viewer", vm.TrayTooltip);
        Assert.Equal(1, _badgeHidden);
        Assert.True(vm.IsHostRunning);
    }

    // ── Stop ──────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Stop_DropsTheViewer_AndResetsHostState()
    {
        var vm = Vm();
        vm.ConsentCallback = _ => Task.FromResult(true);
        int port = await StartHost(vm);
        var viewer = await ConnectViewer(port);
        await WaitUntil(() => _badgeShown == 1, "approval");
        _chat.Receive(isFromHost: false, "old");
        vm.SessionNotesVisible = true;

        await vm.StopHostCommand.Execute();
        await WaitUntil(() => viewer.Disconnected, "viewer to be dropped");

        Assert.False(vm.IsHostRunning);
        Assert.Equal("Stopped", vm.HostStatus);
        Assert.Equal("Idle", vm.TrayTooltip);
        Assert.False(vm.ShowWebViewer);
        Assert.False(vm.SessionNotesVisible);
        Assert.Empty(_chat.Messages);
        Assert.True(_badgeHidden >= 1);
        Assert.Null(vm.ActiveChatLink);
    }

    // The session's disconnect handler is posted to the UI thread, so it runs after Stop has
    // already reset the host. It must not bring any of that state back.
    [AvaloniaFact]
    public async Task Stop_ThenTheLateDisconnect_ChangesNothing()
    {
        var vm = Vm();
        vm.ConsentCallback = _ => Task.FromResult(true);
        int port = await StartHost(vm);
        var viewer = await ConnectViewer(port);
        await WaitUntil(() => _badgeShown == 1, "approval");

        vm.Stop();
        int afterStop = _shell.Statuses.Count;
        await WaitUntil(() => viewer.Disconnected, "viewer to be dropped");
        await Settle();

        Assert.False(vm.IsHostRunning);
        Assert.Equal("Stopped", vm.HostStatus);
        Assert.Equal("Idle", vm.TrayTooltip);
        Assert.Empty(_shell.Statuses.Skip(afterStop));
    }

    // Stop and start again straight away: the old session's late disconnect belongs to a
    // listener that is gone, so it must not touch the new host or make it relisten.
    [AvaloniaFact]
    public async Task Restart_ThenTheOldSessionsLateDisconnect_LeavesTheNewHostAlone()
    {
        var vm = Vm();
        vm.ConsentCallback = _ => Task.FromResult(true);
        int port = await StartHost(vm);
        var viewer = await ConnectViewer(port);
        await WaitUntil(() => _badgeShown == 1, "approval");

        vm.Stop();
        int afterStop = _shell.Statuses.Count;
        int port2 = await StartHost(vm);
        await WaitUntil(() => viewer.Disconnected, "old viewer to be dropped");
        await Settle();

        Assert.DoesNotContain("Client disconnected", _shell.Statuses.Skip(afterStop));
        Assert.DoesNotContain(_shell.Statuses.Skip(afterStop), IsWaitingForAViewer);
        Assert.True(vm.IsHostRunning);

        var viewer2 = await ConnectViewer(port2);
        await WaitUntil(() => _badgeShown == 2, "the new viewer's approval");
        await WaitUntil(() => Volatile.Read(ref viewer2.Frames) > 0, "frames at the new viewer");
    }
}
