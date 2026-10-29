using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PixelWizard.AvaloniaClient.Services;
using PixelWizard.AvaloniaClient.ViewModels;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;
using PixelWizard.Session;
using PixelWizard.Transport.Tcp;
using Xunit;

namespace PixelWizard.AvaloniaClient.Tests;

public sealed class ViewerViewModelTests : IDisposable
{
    private static readonly HelloMessage OurHello = new()
    {
        ProtocolVersion = ProtocolVersions.Current,
        Role = PeerRole.Full,
        Codecs = SupportedCodecs.Jpeg,
        MaxConcurrentStreams = 1
    };

    private sealed class FakeShell : IViewerShell
    {
        public readonly List<string> Statuses = new();
        public int LiveShown;
        public readonly List<bool> ConnectShown = new(); // onlyFromLive per call
        public readonly List<string> Clipboard = new();
        public string Status { set => Statuses.Add(value); }
        public void ShowLiveScreen() => LiveShown++;
        public void ShowConnectScreen(bool onlyFromLive) => ConnectShown.Add(onlyFromLive);
        public void WriteClipboard(string text) => Clipboard.Add(text);
    }

    private sealed class NoRouter : IRouterClient
    {
        public Task<RouterRegistrationResult> RegisterHostAsync(string h, int p, string e) => throw new NotSupportedException();
        public Task<RouterConnectResult> ResolveEndpointAsync(string h, int p, string c) => throw new NotSupportedException();
    }

    private readonly string _settingsFile =
        Path.Combine(Path.GetTempPath(), "pw-viewer-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly FakeShell _shell = new();
    private readonly ChatViewModel _chat;
    private readonly List<ViewerViewModel> _created = new();

    public ViewerViewModelTests()
    {
        _chat = new ChatViewModel(() => null, () => Task.FromResult<string?>(null));
    }

    private ViewerViewModel Vm(AppSettings? settings = null)
    {
        var vm = new ViewerViewModel(_shell, _chat, settings ?? AppSettings.Load(_settingsFile), new NoRouter(), OurHello);
        _created.Add(vm);
        return vm;
    }

    public void Dispose()
    {
        foreach (var vm in _created) vm.Dispose();
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

    // Posted UI work (every session event lands via Dispatcher.UIThread.Post) runs while
    // this waits on the headless UI thread.
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

    // ── Form and settings ─────────────────────────────────────────────────────

    [Fact]
    public void RestoresRouterAddressAndRecentHosts_FromSettings()
    {
        var settings = AppSettings.Load(_settingsFile);
        settings.LastRouterAddress = "https://router.example";
        AppSettings.PushRecent(settings.RecentHosts, "10.0.0.2");
        AppSettings.PushRecent(settings.RecentHosts, "10.0.0.3:9999");

        var vm = Vm(settings);

        Assert.Equal("https://router.example", vm.RouterAddress);
        Assert.Equal(new[] { "10.0.0.3:9999", "10.0.0.2" }, vm.RecentHosts);
        Assert.True(vm.HasRecentHosts);
    }

    [Fact]
    public void PickingARecentOrDiscoveredHost_FillsTheAddress()
    {
        var vm = Vm();
        vm.SelectedRecentHost = "10.0.0.7";
        Assert.Equal("10.0.0.7", vm.HostAddress);
        vm.SelectedDiscoveredHost = "10.0.0.8:9000";
        Assert.Equal("10.0.0.8:9000", vm.HostAddress);
    }

    [AvaloniaFact]
    public async Task ConnectDirect_InvalidAddress_OnlySetsStatus()
    {
        var vm = Vm();
        vm.HostAddress = "  ";

        await vm.ConnectDirectCommand.Execute();

        Assert.Equal("Enter the host's address, e.g. 192.168.1.10 or 192.168.1.10:8888", _shell.Statuses.Last());
        Assert.False(vm.IsConnecting);
        Assert.Null(vm.ActiveChatLink);
    }

    [AvaloniaFact]
    public async Task ConnectViaCode_EmptyCode_OnlySetsStatus()
    {
        var vm = Vm();
        vm.ConnectionCode = " ";

        await vm.ConnectViaCodeCommand.Execute();

        Assert.Equal("Enter a connection code", _shell.Statuses.Last());
        Assert.False(vm.IsConnecting);
    }

    [AvaloniaFact]
    public async Task ConnectDirect_NothingListening_ReportsFailure_AndStopsConnecting()
    {
        var vm = Vm();
        vm.UseTls = false;
        vm.HostAddress = $"127.0.0.1:{GetFreePort()}";

        await vm.ConnectDirectCommand.Execute();
        await WaitUntil(() => !vm.IsConnecting, "connect attempt to finish");

        Assert.False(vm.IsConnected);
        Assert.Equal(0, _shell.LiveShown);
    }

    // ── Zoom ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Zoom_PresetsAndStepping()
    {
        var vm = Vm();
        Assert.True(vm.IsFitMode);
        Assert.Equal("Fit", vm.ZoomLabel);

        await vm.ZoomInCommand.Execute();     // Fit -> 100%
        Assert.Equal(1.0, vm.ViewScale);
        await vm.ZoomInCommand.Execute();     // -> 150%
        Assert.Equal("150%", vm.ZoomLabel);
        await vm.Zoom50Command.Execute();
        Assert.Equal(0.5, vm.ViewScale);
        await vm.FitWindowCommand.Execute();
        Assert.True(vm.IsFitMode);
        await vm.ZoomOutCommand.Execute();    // nothing below Fit
        Assert.True(vm.IsFitMode);
    }

    // Pins today's behaviour, moved unchanged from MainViewModel: Zoom out goes straight to
    // Fit from any zoom (its loop picks the first preset below the current one, which is
    // always 50%, and steps one past it). Known bug, fixed in its own commit.
    [Theory]
    [InlineData(3)] // 100%
    [InlineData(5)] // 200%
    public async Task ZoomOut_CurrentlyAlwaysReturnsToFit(int presetIndex)
    {
        var vm = Vm();
        vm.ZoomPresetIndex = presetIndex;
        await vm.ZoomOutCommand.Execute();
        Assert.True(vm.IsFitMode);
    }

    // ── Disconnect ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Disconnect_ResetsViewAndChat_LeavesLiveScreenOnly()
    {
        var vm = Vm();
        vm.ViewScale = 1.0;
        vm.KeyboardActive = true;
        _chat.Receive(isFromHost: true, "old");

        await vm.DisconnectCommand.Execute();

        Assert.Equal(0, vm.ViewScale);
        Assert.False(vm.KeyboardActive);
        Assert.Null(vm.RemoteScreen);
        Assert.Empty(_chat.Messages);
        Assert.Equal(new[] { true }, _shell.ConnectShown);
    }

    // ── Over a real socket ────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Connect_GoesLive_ChatAndInputFlow_HostDropReturnsToConnect()
    {
        int port = GetFreePort();
        using var host = new HostSession(new TcpTransport(), OurHello);
        bool verified = false;
        host.HandshakeVerified += () => verified = true;
        var chatFromViewer = new List<string>();
        var moves = new List<(int, int)>();
        host.ChatReceived += t => { lock (chatFromViewer) chatFromViewer.Add(t); };
        host.MouseMoveReceived += m => { lock (moves) moves.Add((m.X, m.Y)); };
        _ = host.StartServerAsync(port, useTls: false);
        await Task.Delay(50); // let the listener bind

        var vm = Vm();
        vm.UseTls = false;
        vm.HostAddress = $" 127.0.0.1:{port} ";

        await vm.ConnectDirectCommand.Execute();
        await WaitUntil(() => vm.IsConnected, "viewer to go live");

        Assert.Equal(1, _shell.LiveShown);
        Assert.Equal("Connected", _shell.Statuses.Last());
        Assert.False(vm.IsConnecting);
        Assert.Equal(new[] { $"127.0.0.1:{port}" }, vm.RecentHosts);
        Assert.Equal(new[] { $"127.0.0.1:{port}" }, AppSettings.Load(_settingsFile).RecentHosts);

        await WaitUntil(() => verified, "host to verify the handshake");
        host.Approve();

        await host.SendChatAsync("hi viewer");
        await WaitUntil(() => _chat.Messages.Count == 1, "chat from host");
        Assert.True(_chat.Messages[0].IsHost);

        await host.SendClipboardAsync("copied on host");
        await WaitUntil(() => _shell.Clipboard.Count == 1, "clipboard from host");
        Assert.Equal("copied on host", _shell.Clipboard[0]);

        var link = vm.ActiveChatLink;
        Assert.NotNull(link);
        Assert.False(link!.IsHost);
        await link.SendChat("hi host");
        vm.SendMouseMove(12, 34);
        await WaitUntil(() => { lock (moves) return moves.Count == 1; }, "mouse move on host");
        await WaitUntil(() => { lock (chatFromViewer) return chatFromViewer.Count == 1; }, "chat on host");
        Assert.Equal((12, 34), moves[0]);
        Assert.Equal("hi host", chatFromViewer[0]);

        host.Disconnect();
        await WaitUntil(() => !vm.IsConnected, "viewer to see the host drop");
        await WaitUntil(() => _shell.ConnectShown.Count > 0, "connect screen");

        Assert.Equal(new[] { false }, _shell.ConnectShown);
        Assert.Equal("Disconnected", _shell.Statuses.Last());
        Assert.Null(vm.ActiveChatLink);
    }
}
