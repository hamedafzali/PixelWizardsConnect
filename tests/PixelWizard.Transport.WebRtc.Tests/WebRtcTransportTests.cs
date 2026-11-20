using System.Collections.Concurrent;
using PixelWizard.Protocol;
using PixelWizard.Signaling;
using SIPSorcery.Net;
using Xunit;

namespace PixelWizard.Transport.WebRtc.Tests;

[Collection("router")]
public class WebRtcTransportTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private readonly RouterFixture _router;

    public WebRtcTransportTests(RouterFixture router) => _router = router;

    private sealed class Peer : IDisposable
    {
        public WebRtcTransport T { get; } = new() { EstablishTimeout = TimeSpan.FromSeconds(30) };
        public ConcurrentQueue<NetworkMessage> Messages { get; } = new();
        public SemaphoreSlim MessageSignal { get; } = new(0);
        public int Disconnects;
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Exception> Errors { get; } = new();

        public Peer()
        {
            T.MessageReceived += m => { Messages.Enqueue(m); MessageSignal.Release(); };
            T.Disconnected += () => { Interlocked.Increment(ref Disconnects); Disconnected.TrySetResult(); };
            T.Error += e => { lock (Errors) Errors.Add(e); };
        }

        public async Task<NetworkMessage> NextAsync()
        {
            await MessageSignal.WaitAsync(Limit);
            Assert.True(Messages.TryDequeue(out var m));
            return m!;
        }

        public void Dispose() => T.Dispose();
    }

    private async Task<(Peer host, Peer viewer)> PairAsync()
    {
        using var cts = new CancellationTokenSource(Limit);
        var ct = cts.Token;
        var hostSig = new SignalingClient("http://127.0.0.1", _router.Port);
        var viewerSig = new SignalingClient("http://127.0.0.1", _router.Port);
        var reg = await hostSig.RegisterAsync(ct);
        var waitForPeer = hostSig.WaitForPeerAsync(ct);
        var joined = await viewerSig.JoinAsync(reg.Code, ct);
        var peerJoined = await waitForPeer;

        var host = new Peer();
        var viewer = new Peer();
        await Task.WhenAll(
            host.T.EstablishAsync(hostSig, peerJoined.Turn, WebRtcRole.Host, ct: ct),
            viewer.T.EstablishAsync(viewerSig, joined.Turn, WebRtcRole.Viewer, ct: ct)).WaitAsync(Limit);
        Assert.True(host.T.IsConnected);
        Assert.True(viewer.T.IsConnected);
        return (host, viewer);
    }

    private static NetworkMessage Msg(MessageType type, byte[] data) => new() { Type = type, Data = data };

    [RouterFact]
    public async Task Control_messages_of_several_types_arrive_in_order_both_ways()
    {
        var (host, viewer) = await PairAsync();
        using var _h = host; using var _v = viewer;
        MessageType[] types = { MessageType.Ping, MessageType.MouseMove, MessageType.KeyPress, MessageType.QualityPreset, MessageType.Pong, MessageType.MouseClick };
        for (int i = 0; i < types.Length; i++) await host.T.SendMessageAsync(Msg(types[i], new[] { (byte)i, (byte)(i * 2) }));
        for (int i = types.Length - 1; i >= 0; i--) await viewer.T.SendMessageAsync(Msg(types[i], new[] { (byte)i }));

        for (int i = 0; i < types.Length; i++)
        {
            var m = await viewer.NextAsync();
            Assert.Equal(types[i], m.Type);
            Assert.Equal(new[] { (byte)i, (byte)(i * 2) }, m.Data);
        }
        for (int i = types.Length - 1; i >= 0; i--)
        {
            var m = await host.NextAsync();
            Assert.Equal(types[i], m.Type);
            Assert.Equal(new[] { (byte)i }, m.Data);
        }
    }

    [RouterFact]
    public async Task Many_small_messages_keep_their_order_and_byte_counters_move()
    {
        var (host, viewer) = await PairAsync();
        using var _h = host; using var _v = viewer;
        int sent = 0, received = 0;
        host.T.BytesSent += n => Interlocked.Add(ref sent, n);
        viewer.T.BytesReceived += n => Interlocked.Add(ref received, n);
        for (int i = 0; i < 200; i++) await host.T.SendMessageAsync(Msg(MessageType.MouseMove, BitConverter.GetBytes(i)));
        for (int i = 0; i < 200; i++) Assert.Equal(i, BitConverter.ToInt32((await viewer.NextAsync()).Data));
        Assert.True(sent > 0);
        Assert.Equal(sent, received);
    }

    [RouterFact]
    public async Task Large_message_round_trips_intact()
    {
        var (host, viewer) = await PairAsync();
        using var _h = host; using var _v = viewer;
        var payload = new byte[1024 * 1024 + 123];
        new Random(42).NextBytes(payload);
        await host.T.SendMessageAsync(Msg(MessageType.FullScreen, payload));
        await host.T.SendMessageAsync(Msg(MessageType.Ping, new byte[] { 7 })); // must still arrive after it
        var m = await viewer.NextAsync();
        Assert.Equal(MessageType.FullScreen, m.Type);
        Assert.True(payload.AsSpan().SequenceEqual(m.Data));
        Assert.Equal(MessageType.Ping, (await viewer.NextAsync()).Type);

        await viewer.T.SendMessageAsync(Msg(MessageType.FullScreen, payload));
        var back = await host.NextAsync();
        Assert.True(payload.AsSpan().SequenceEqual(back.Data));
    }

    [RouterFact]
    public async Task SCTP_sender_tuning_is_applied_on_both_ends()
    {
        var (host, viewer) = await PairAsync();
        using var _h = host; using var _v = viewer;
        // False means a SIPSorcery upgrade renamed the private sender fields: fix SctpTuning, do not relax this.
        Assert.True(host.T.SctpTuned);
        Assert.True(viewer.T.SctpTuned);
    }

    [RouterFact]
    public async Task Local_disconnect_raises_Disconnected_once_on_both_sides()
    {
        var (host, viewer) = await PairAsync();
        using var _h = host; using var _v = viewer;
        host.T.Disconnect();
        host.T.Disconnect();
        await host.Disconnected.Task.WaitAsync(Limit);
        await viewer.Disconnected.Task.WaitAsync(Limit);
        await Task.Delay(500); // a second raise would show up by now
        Assert.Equal(1, host.Disconnects);
        Assert.Equal(1, viewer.Disconnects);
        Assert.False(host.T.IsConnected);
        Assert.False(viewer.T.IsConnected);
        await host.T.SendMessageAsync(Msg(MessageType.Ping, Array.Empty<byte>())); // silently ignored, like TCP
        host.T.Dispose();
        host.T.Dispose();
        Assert.Equal(1, host.Disconnects);
    }

    [RouterFact]
    public async Task A_peer_that_drops_without_bye_raises_Disconnected_once_on_the_other()
    {
        var (host, viewer) = await PairAsync();
        using var _v = viewer;
        host.T.Dispose(); // no bye: the signaling socket just goes away
        await viewer.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(500);
        Assert.Equal(1, viewer.Disconnects);
        Assert.Equal(1, host.Disconnects);
        Assert.False(viewer.T.IsConnected);
    }

    [RouterFact]
    public async Task A_duplicate_remote_candidate_is_harmless()
    {
        var (host, viewer) = await PairAsync();
        using var _h = host; using var _v = viewer;
        var applied = viewer.T.AppliedRemoteCandidates;
        Assert.NotEmpty(applied);
        var dup = applied[0];
        host.T.InjectRemoteCandidate(new RTCIceCandidateInit { candidate = dup.candidate, sdpMid = dup.sdpMid, sdpMLineIndex = dup.sdpMLineIndex });
        viewer.T.InjectRemoteCandidate(dup);
        viewer.T.InjectRemoteCandidate(dup);

        await host.T.SendMessageAsync(Msg(MessageType.Ping, new byte[] { 1 }));
        Assert.Equal(MessageType.Ping, (await viewer.NextAsync()).Type);
        await viewer.T.SendMessageAsync(Msg(MessageType.Pong, new byte[] { 2 }));
        Assert.Equal(MessageType.Pong, (await host.NextAsync()).Type);
        Assert.True(host.T.IsConnected && viewer.T.IsConnected);
        Assert.Empty(host.Errors);
        Assert.Empty(viewer.Errors);
    }

    [RouterFact]
    public async Task SelectedPairType_is_reported_and_is_host_on_loopback()
    {
        var (host, viewer) = await PairAsync();
        using var _h = host; using var _v = viewer;
        Assert.Equal("host/host", host.T.SelectedPairType);
        Assert.Equal("host/host", viewer.T.SelectedPairType);
    }

    [RouterFact]
    public async Task A_throwing_handler_raises_HandlerError_and_the_connection_survives()
    {
        var (host, viewer) = await PairAsync();
        using var _h = host; using var _v = viewer;
        var handlerErrors = new ConcurrentQueue<Exception>();
        viewer.T.HandlerError += e => handlerErrors.Enqueue(e);
        int calls = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.T.MessageReceived += _ => { if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom"); second.TrySetResult(); };
        await host.T.SendMessageAsync(Msg(MessageType.Ping, new byte[] { 1 }));
        await host.T.SendMessageAsync(Msg(MessageType.Ping, new byte[] { 2 }));
        await second.Task.WaitAsync(Limit);
        Assert.Single(handlerErrors);
        Assert.True(viewer.T.IsConnected);
    }

    [RouterFact]
    public async Task Establish_faults_and_cleans_up_when_the_peer_never_answers()
    {
        using var cts = new CancellationTokenSource(Limit);
        var hostSig = new SignalingClient("http://127.0.0.1", _router.Port);
        var viewerSig = new SignalingClient("http://127.0.0.1", _router.Port);
        var reg = await hostSig.RegisterAsync(cts.Token);
        var wait = hostSig.WaitForPeerAsync(cts.Token);
        await viewerSig.JoinAsync(reg.Code, cts.Token);
        var pj = await wait;
        using var host = new Peer();
        host.T.EstablishTimeout = TimeSpan.FromSeconds(3);
        // The viewer never establishes, so no answer arrives.
        await Assert.ThrowsAsync<TimeoutException>(() => host.T.EstablishAsync(hostSig, pj.Turn, WebRtcRole.Host));
        Assert.Equal(1, host.Disconnects);
        Assert.False(host.T.IsConnected);
        await viewerSig.DisposeAsync();
    }
}
