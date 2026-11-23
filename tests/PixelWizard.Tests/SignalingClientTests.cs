using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PixelWizard.Signaling;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>SignalingClient against an in-process HttpListener WebSocket fake of the router.</summary>
public class SignalingClientTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    /// <summary>One accepted WebSocket; every frame it receives is also queued on the router.</summary>
    private sealed class FakeSocket
    {
        private readonly WebSocket _ws;
        private readonly Channel<JsonNode> _received;
        public TaskCompletionSource<WebSocketCloseStatus?> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeSocket(WebSocket ws, Channel<JsonNode> received) { _ws = ws; _received = received; }

        public async Task<JsonNode?> ReceiveAsync()
        {
            var buf = new byte[8192];
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    Closed.TrySetResult(r.CloseStatus);
                    try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                    return null;
                }
                ms.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var node = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()))!;
                _received.Writer.TryWrite(node);
                return node;
            }
        }

        public Task SendAsync(string json) =>
            _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

        /// <summary>Router behaviour for a fatal problem: error, then close.</summary>
        public async Task FailAsync(string code)
        {
            await SendAsync($"{{\"v\":1,\"type\":\"error\",\"code\":\"{code}\",\"message\":\"x\"}}");
            await _ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, code, CancellationToken.None);
        }

        public async Task CloseAsync() { try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { } }
        public void Abort() => _ws.Abort();
    }

    private sealed class FakeRouter : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<FakeSocket, int, Task> _onConnection;
        private readonly Channel<JsonNode> _received = Channel.CreateUnbounded<JsonNode>();
        private readonly List<Task> _handlers = new();
        private readonly Task _acceptLoop;
        private int _connections;
        public int Port { get; }

        public FakeRouter(Func<FakeSocket, int, Task> onConnection)
        {
            _onConnection = onConnection;
            // No fixed ports: probe a free one, retrying if another process grabs it first.
            for (int attempt = 0; ; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                try { _listener.Prefixes.Clear(); _listener.Prefixes.Add($"http://127.0.0.1:{port}/"); _listener.Start(); Port = port; break; }
                catch (HttpListenerException) when (attempt < 10) { }
            }
            _acceptLoop = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                if (!ctx.Request.IsWebSocketRequest || ctx.Request.Url!.AbsolutePath != "/signal")
                {
                    ctx.Response.StatusCode = 404; ctx.Response.Close(); continue;
                }
                int n = Interlocked.Increment(ref _connections);
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var fake = new FakeSocket(wsCtx.WebSocket, _received);
                lock (_handlers) _handlers.Add(Task.Run(async () => { try { await _onConnection(fake, n); } catch { } }));
            }
        }

        public async Task<JsonNode> NextReceivedAsync() => await _received.Reader.ReadAsync().AsTask().WaitAsync(Limit);

        public async ValueTask DisposeAsync()
        {
            _listener.Abort();
            await _acceptLoop;
            Task[] hs; lock (_handlers) hs = _handlers.ToArray();
            try { await Task.WhenAll(hs).WaitAsync(Limit); } catch { }
        }
    }

    private static async Task<T> Bounded<T>(Task<T> t) => await t.WaitAsync(Limit);

    private const string Turn =
        "\"turn\":{\"urls\":[\"turn:t.example.org:3478?transport=udp\"],\"username\":\"1767225600:ab\",\"credential\":\"c=\"}";

    private static async Task<List<SignalingMessage>> Drain(SignalingClient c)
    {
        var all = new List<SignalingMessage>();
        await foreach (var m in c.ReadMessagesAsync().WithCancellation(new CancellationTokenSource(Limit).Token)) all.Add(m);
        return all;
    }

    [Fact]
    public void SignalUri_FollowsRouterUrlRules()
    {
        Assert.Equal("ws://r.example:9000/signal", SignalingClient.SignalUri("r.example", 9000).ToString());
        Assert.Equal("wss://r.example/signal", SignalingClient.SignalUri("https://r.example", 443).ToString());
        Assert.Equal("ws://r.example/signal", SignalingClient.SignalUri("http://r.example", 80).ToString());
        Assert.Equal("wss://r.example:8443/signal", SignalingClient.SignalUri("https://r.example/", 8443).ToString());
    }

    [Fact]
    public async Task Register_ParsesCodeAndResumeToken()
    {
        await using var router = new FakeRouter(async (s, _) =>
        {
            await s.ReceiveAsync();
            await s.SendAsync("{\"v\":1,\"type\":\"registered\",\"code\":\"K7Q2MX\",\"resumeToken\":\"tok\"}");
            while (await s.ReceiveAsync() != null) { }
        });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        var reg = await Bounded(client.RegisterAsync());
        Assert.Equal("K7Q2MX", reg.Code);
        Assert.Equal("tok", reg.ResumeToken);
        Assert.Equal("K7Q2MX", client.Code);
        Assert.Equal("{\"v\":1,\"type\":\"register\"}", (await router.NextReceivedAsync()).ToJsonString());
    }

    [Fact]
    public async Task Host_PeerJoinedWithTurn_ThenStreamsRelayedMessages()
    {
        await using var router = new FakeRouter(async (s, _) =>
        {
            await s.ReceiveAsync();
            await s.SendAsync("{\"v\":1,\"type\":\"registered\",\"code\":\"AAAAAA\",\"resumeToken\":\"t\"}");
            await s.SendAsync($"{{\"v\":1,\"type\":\"peer-joined\",{Turn}}}");
            await s.ReceiveAsync(); // offer
            await s.SendAsync("{\"v\":1,\"type\":\"answer\",\"sdp\":\"A\"}");
            await s.SendAsync("{\"v\":1,\"type\":\"peer-left\"}");
            await s.CloseAsync();
        });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        await Bounded(client.RegisterAsync());
        var pj = await Bounded(client.WaitForPeerAsync());
        Assert.Equal("turn:t.example.org:3478?transport=udp", Assert.Single(pj.Turn!.Urls));
        Assert.Equal("1767225600:ab", pj.Turn.Username);
        await client.SendOfferAsync("O");
        var got = await Drain(client);
        Assert.Equal(new SignalingMessage[] { new AnswerMessage("A"), new PeerLeftMessage() }, got);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Join_ParsesJoined_WithAndWithoutTurn(bool withTurn)
    {
        await using var router = new FakeRouter(async (s, _) =>
        {
            await s.ReceiveAsync();
            await s.SendAsync("{\"v\":1,\"type\":\"joined\"" + (withTurn ? "," + Turn : "") + "}");
            while (await s.ReceiveAsync() != null) { }
        });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        var joined = await Bounded(client.JoinAsync("K7Q2MX"));
        Assert.Equal(withTurn, joined.Turn != null);
        Assert.Equal("K7Q2MX", client.Code);
        Assert.Equal("{\"v\":1,\"type\":\"join\",\"code\":\"K7Q2MX\"}", (await router.NextReceivedAsync()).ToJsonString());
    }

    [Fact]
    public async Task Sends_ProduceTheExactJsonShapes()
    {
        await using var router = new FakeRouter(async (s, _) =>
        {
            await s.ReceiveAsync();
            await s.SendAsync("{\"v\":1,\"type\":\"joined\"}");
            while (await s.ReceiveAsync() != null) { }
        });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        await Bounded(client.JoinAsync("K7Q2MX"));
        await router.NextReceivedAsync(); // join

        await client.SendOfferAsync("o\r\n");
        await client.SendAnswerAsync("a");
        await client.SendCandidateAsync("candidate:1 1 udp 1 192.0.2.1 5 typ host", "0", 0);
        await client.SendCandidateAsync("candidate:2", null, null);
        await client.SendEndOfCandidatesAsync();
        await client.SendByeAsync();
        await client.SendByeAsync("done");

        string[] expected =
        {
            "{\"v\":1,\"type\":\"offer\",\"sdp\":\"o\\r\\n\"}",
            "{\"v\":1,\"type\":\"answer\",\"sdp\":\"a\"}",
            "{\"v\":1,\"type\":\"candidate\",\"candidate\":\"candidate:1 1 udp 1 192.0.2.1 5 typ host\",\"sdpMid\":\"0\",\"sdpMLineIndex\":0}",
            "{\"v\":1,\"type\":\"candidate\",\"candidate\":\"candidate:2\",\"sdpMid\":null,\"sdpMLineIndex\":null}",
            "{\"v\":1,\"type\":\"end-of-candidates\"}",
            "{\"v\":1,\"type\":\"bye\"}",
            "{\"v\":1,\"type\":\"bye\",\"reason\":\"done\"}",
        };
        foreach (string e in expected) Assert.Equal(e, (await router.NextReceivedAsync()).ToJsonString());
    }

    [Fact]
    public async Task ConcurrentSends_AreSerialized()
    {
        await using var router = new FakeRouter(async (s, _) =>
        {
            await s.ReceiveAsync();
            await s.SendAsync("{\"v\":1,\"type\":\"joined\"}");
            while (await s.ReceiveAsync() != null) { }
        });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        await Bounded(client.JoinAsync("K7Q2MX"));
        await router.NextReceivedAsync();
        const int n = 50;
        var sends = new Task[n];
        for (int i = 0; i < n; i++) sends[i] = client.SendCandidateAsync("c" + i, "0", 0);
        await Task.WhenAll(sends).WaitAsync(Limit);
        var seen = new HashSet<string>();
        for (int i = 0; i < n; i++) seen.Add((string)(await router.NextReceivedAsync())["candidate"]!);
        Assert.Equal(n, seen.Count);
    }

    [Fact]
    public async Task IncomingCandidates_AreConsumableAsTheyArrive_InOrder()
    {
        // The fake sends the next candidate only after the consumer has seen the previous one, so a
        // client that buffered until end-of-candidates would deadlock (and time out).
        var seenIt = new SemaphoreSlim(0);
        await using var router = new FakeRouter(async (s, _) =>
        {
            await s.ReceiveAsync();
            await s.SendAsync("{\"v\":1,\"type\":\"joined\"}");
            for (int i = 0; i < 3; i++)
            {
                await s.SendAsync($"{{\"v\":1,\"type\":\"candidate\",\"candidate\":\"c{i}\",\"sdpMid\":null,\"sdpMLineIndex\":null}}");
                await seenIt.WaitAsync(Limit);
            }
            await s.SendAsync("{\"v\":1,\"type\":\"end-of-candidates\"}");
            await s.SendAsync("{\"v\":1,\"type\":\"bye\"}");
            while (await s.ReceiveAsync() != null) { }
        });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        await Bounded(client.JoinAsync("K7Q2MX"));
        var got = new List<SignalingMessage>();
        using var cts = new CancellationTokenSource(Limit);
        await foreach (var m in client.ReadMessagesAsync(cts.Token))
        {
            got.Add(m);
            if (m is CandidateMessage) seenIt.Release();
            if (m is ByeMessage) break;
        }
        Assert.Equal(new SignalingMessage[]
        {
            new CandidateMessage("c0", null, null), new CandidateMessage("c1", null, null), new CandidateMessage("c2", null, null),
            new EndOfCandidatesMessage(), new ByeMessage(),
        }, got);
    }

    [Fact]
    public async Task UnknownIncomingType_IsIgnored()
    {
        await using var router = new FakeRouter(async (s, _) =>
        {
            await s.ReceiveAsync();
            await s.SendAsync("{\"v\":1,\"type\":\"joined\"}");
            await s.SendAsync("{\"v\":1,\"type\":\"future-thing\",\"x\":1}");
            await s.SendAsync("{\"v\":1,\"type\":\"offer\",\"sdp\":\"S\"}");
            await s.SendAsync("{\"v\":1,\"type\":\"peer-left\"}");
            await s.CloseAsync();
        });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        await Bounded(client.JoinAsync("K7Q2MX"));
        Assert.Equal(new SignalingMessage[] { new OfferMessage("S"), new PeerLeftMessage() }, await Drain(client));
    }

    [Fact]
    public async Task RouterError_OnJoin_ThrowsTypedExceptionWithCode()
    {
        await using var router = new FakeRouter(async (s, _) => { await s.ReceiveAsync(); await s.FailAsync("code-unknown"); });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        var ex = await Assert.ThrowsAsync<SignalingErrorException>(() => client.JoinAsync("NOPE22").WaitAsync(Limit));
        Assert.Equal("code-unknown", ex.Code);
    }

    [Fact]
    public async Task RouterError_AfterPairing_SurfacesOnTheStream()
    {
        await using var router = new FakeRouter(async (s, _) =>
        {
            await s.ReceiveAsync();
            await s.SendAsync("{\"v\":1,\"type\":\"joined\"}");
            await s.ReceiveAsync();
            await s.FailAsync("rate-limited");
        });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        await Bounded(client.JoinAsync("K7Q2MX"));
        await client.SendByeAsync();
        var ex = await Assert.ThrowsAsync<SignalingErrorException>(() => Drain(client));
        Assert.Equal("rate-limited", ex.Code);
    }

    [Fact]
    public async Task Resume_AfterDroppedSocket_YieldsTheSameCode()
    {
        // The drop waits until the host has its registration: aborting right after the send
        // can reset the connection before the client reads the frame (seen on Windows).
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var router = new FakeRouter(async (s, n) =>
        {
            var first = await s.ReceiveAsync();
            if (n == 1)
            {
                await s.SendAsync("{\"v\":1,\"type\":\"registered\",\"code\":\"K7Q2MX\",\"resumeToken\":\"tok\"}");
                await registered.Task;
                s.Abort(); // the host's network went away
                return;
            }
            Assert.Equal("resume", (string)first!["type"]!);
            Assert.Equal("tok", (string)first["resumeToken"]!);
            await s.SendAsync("{\"v\":1,\"type\":\"resumed\",\"code\":\"K7Q2MX\"}");
            while (await s.ReceiveAsync() != null) { }
        });
        await using var first = new SignalingClient("127.0.0.1", router.Port);
        var reg = await Bounded(first.RegisterAsync());
        registered.SetResult();
        await using var second = new SignalingClient("127.0.0.1", router.Port);
        var resumed = await Bounded(second.ResumeAsync(reg.ResumeToken));
        Assert.Equal(reg.Code, resumed.Code);
        Assert.Equal("K7Q2MX", second.Code);
    }

    [Fact]
    public async Task Resume_Rejected_ThrowsWithCode()
    {
        await using var router = new FakeRouter(async (s, _) => { await s.ReceiveAsync(); await s.FailAsync("resume-rejected"); });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        var ex = await Assert.ThrowsAsync<SignalingErrorException>(() => client.ResumeAsync("bad").WaitAsync(Limit));
        Assert.Equal("resume-rejected", ex.Code);
    }

    [Fact]
    public async Task Cancellation_AbortsAPendingHandshake()
    {
        await using var router = new FakeRouter(async (s, _) => { await s.ReceiveAsync(); while (await s.ReceiveAsync() != null) { } });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        using var cts = new CancellationTokenSource();
        var pending = client.RegisterAsync(cts.Token);
        await router.NextReceivedAsync(); // register arrived; the router never answers
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Limit));
    }

    [Fact]
    public async Task Cancellation_StopsTheStreamEnumeration()
    {
        await using var router = new FakeRouter(async (s, _) =>
        {
            await s.ReceiveAsync();
            await s.SendAsync("{\"v\":1,\"type\":\"joined\"}");
            while (await s.ReceiveAsync() != null) { }
        });
        await using var client = new SignalingClient("127.0.0.1", router.Port);
        await Bounded(client.JoinAsync("K7Q2MX"));
        using var cts = new CancellationTokenSource();
        var reading = Task.Run(async () => { await foreach (var _ in client.ReadMessagesAsync(cts.Token)) { } });
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(Limit));
    }

    [Fact]
    public async Task Dispose_ClosesTheSocketCleanly()
    {
        FakeSocket? fake = null;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var router = new FakeRouter(async (s, _) =>
        {
            fake = s;
            await s.ReceiveAsync();
            await s.SendAsync("{\"v\":1,\"type\":\"registered\",\"code\":\"K7Q2MX\",\"resumeToken\":\"t\"}");
            connected.TrySetResult();
            while (await s.ReceiveAsync() != null) { }
        });
        var client = new SignalingClient("127.0.0.1", router.Port);
        await Bounded(client.RegisterAsync());
        await connected.Task.WaitAsync(Limit);
        await client.DisposeAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await fake!.Closed.Task.WaitAsync(Limit));
        await client.DisposeAsync(); // idempotent
    }
}
