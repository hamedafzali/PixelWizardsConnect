// ICE probe (PHASE3-PLAN P3.7): two peers pair through the router's /signal, trickle ICE candidates,
// open one data channel and exchange pings. Throwaway-grade: no capture, no codecs.
//
// Machine-readable output lines (stdout): CODE, LOCAL-CANDIDATE, ICE-STATE, SELECTED-PAIR, RTT, RESULT.
// Exit code 0 only after "RESULT ok". TURN credentials are never printed.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using PixelWizard.Signaling;
using SIPSorcery.Net;

const int Pings = 5;

string? router = null, joinCode = null, stun = null;
bool host = false, relayOnly = false;
int timeoutSeconds = 60;
try
{
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--router": router = args[++i]; break;
            case "--host": host = true; break;
            case "--join": joinCode = args[++i]; break;
            case "--relay-only": relayOnly = true; break;
            case "--timeout": timeoutSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--stun": stun = args[++i]; break;
            default: throw new ArgumentException($"unknown argument '{args[i]}'");
        }
    }
    if (router == null || host == (joinCode != null)) throw new ArgumentException("need --router <url> and exactly one of --host / --join <code>");
}
catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
{
    Console.Error.WriteLine("usage: PixelWizard.IceProbe --router <url> (--host | --join <code>) [--relay-only] [--timeout <seconds>] [--stun <url>]");
    Console.Error.WriteLine(e.Message);
    return 2;
}

var (routerHost, routerPort) = SplitRouter(router);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
var ct = cts.Token;
string stage = "start";
try
{
    return host ? await RunAsync(true, null) : await RunAsync(false, joinCode);
}
catch (OperationCanceledException)
{
    return Fail($"timeout after {timeoutSeconds}s at stage '{stage}'");
}
catch (Exception e)
{
    return Fail($"{e.GetType().Name}: {e.Message} (stage '{stage}')");
}

async Task<int> RunAsync(bool isHost, string? code)
{
    await using var sig = new SignalingClient(routerHost, routerPort);
    TurnInfo? turn;
    if (isHost)
    {
        stage = "register";
        var reg = await sig.RegisterAsync(ct);
        Console.WriteLine($"CODE {reg.Code}");
        Console.Out.Flush();
        stage = "wait-for-peer";
        turn = (await sig.WaitForPeerAsync(ct)).Turn;
    }
    else
    {
        stage = "join";
        turn = (await sig.JoinAsync(code!, ct)).Turn;
    }
    Console.WriteLine($"TURN {(turn == null ? "none" : "issued")}");

    var servers = new List<RTCIceServer>();
    if (turn != null)
        servers.Add(new RTCIceServer { urls = string.Join(",", turn.Urls), username = turn.Username, credential = turn.Credential });
    if (stun != null) servers.Add(new RTCIceServer { urls = stun });
    using var pc = new RTCPeerConnection(new RTCConfiguration
    {
        iceServers = servers,
        iceTransportPolicy = relayOnly ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all,
    });
    Console.WriteLine($"POLICY {(relayOnly ? "relay" : "all")}");

    var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var peerBye = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var remoteSet = false;
    var pendingRemote = new List<RTCIceCandidateInit>();
    var gate = new object();
    var sdpSent = false;
    var pendingLocal = new List<RTCIceCandidateInit>();
    var sendQueue = System.Threading.Channels.Channel.CreateUnbounded<Func<Task>>();
    var rtts = new List<double>();
    string pair = "unknown/unknown";

    // All signaling sends run through one ordered queue, so a candidate that fires while the offer/answer
    // is still being sent can never overtake it. Candidates themselves are queued the moment they fire.
    var sender = Task.Run(async () =>
    {
        await foreach (var send in sendQueue.Reader.ReadAllAsync()) await send();
    });
    void Enqueue(Func<Task> send) => sendQueue.Writer.TryWrite(send);
    void QueueCandidate(RTCIceCandidateInit c) =>
        Enqueue(() => sig.SendCandidateAsync(c.candidate, c.sdpMid, c.sdpMLineIndex, ct));

    pc.onicecandidate += c =>
    {
        if (c == null) return;
        Console.WriteLine($"LOCAL-CANDIDATE {c.type} {c.protocol} {c.address}:{c.port}");
        var init = new RTCIceCandidateInit { candidate = c.ToString(), sdpMid = c.sdpMid ?? "0", sdpMLineIndex = c.sdpMLineIndex };
        lock (gate)
        {
            if (!sdpSent) { pendingLocal.Add(init); return; }
        }
        QueueCandidate(init);
    };
    pc.onicegatheringstatechange += s =>
    {
        Console.WriteLine($"ICE-GATHERING {s}");
        if (s == RTCIceGatheringState.complete) Enqueue(() => sig.SendEndOfCandidatesAsync(ct));
    };
    pc.oniceconnectionstatechange += s =>
    {
        Console.WriteLine($"ICE-STATE {s}");
        if (s == RTCIceConnectionState.connected) pair = ReportPair(pc);
        if (s == RTCIceConnectionState.failed) done.TrySetResult(Fail("ice failed"));
    };

    void AfterSdpSent()
    {
        lock (gate)
        {
            sdpSent = true;
            foreach (var c in pendingLocal) QueueCandidate(c);
            pendingLocal.Clear();
        }
    }

    void ApplyRemote(RTCIceCandidateInit c) => pc.addIceCandidate(c);

    // Data channel behaviour. Host pings, joiner echoes; host's final "done" is acknowledged before bye.
    void Wire(RTCDataChannel dc)
    {
        var sw = new Stopwatch();
        int sent = 0;
        void Ping() { sw.Restart(); dc.send($"ping {sent++}"); }
        dc.onopen += () => { Console.WriteLine("DATACHANNEL open"); if (isHost) Ping(); };
        dc.onmessage += (_, _, data) =>
        {
            string m = Encoding.UTF8.GetString(data);
            if (!isHost)
            {
                if (m.StartsWith("ping ", StringComparison.Ordinal)) dc.send(m);
                else if (m == "done")
                {
                    dc.send("done-ack");
                    Console.WriteLine($"RESULT ok pair={pair} role=join");
                    done.TrySetResult(0);
                }
            }
            else if (m.StartsWith("ping ", StringComparison.Ordinal))
            {
                double ms = sw.Elapsed.TotalMilliseconds;
                rtts.Add(ms);
                Console.WriteLine($"RTT {ms.ToString("F1", CultureInfo.InvariantCulture)}");
                if (sent < Pings) Ping(); else dc.send("done");
            }
            else if (m == "done-ack")
            {
                Console.WriteLine($"RESULT ok pair={pair} rtt_avg={rtts.Average().ToString("F1", CultureInfo.InvariantCulture)}");
                done.TrySetResult(0);
            }
        };
    }

    if (isHost)
    {
        stage = "create-offer";
        Wire(await pc.createDataChannel("probe"));
        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);
        stage = "send-offer";
        await sig.SendOfferAsync(offer.sdp, ct);
        AfterSdpSent();
    }
    else
    {
        pc.ondatachannel += Wire;
    }

    stage = "signaling";
    var reader = Task.Run(async () =>
    {
        await foreach (var msg in sig.ReadMessagesAsync(ct))
        {
            switch (msg)
            {
                case OfferMessage o when !isHost:
                    var res = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = o.Sdp });
                    if (res != SetDescriptionResultEnum.OK) throw new InvalidOperationException($"setRemoteDescription: {res}");
                    FlushRemote();
                    var answer = pc.createAnswer();
                    await pc.setLocalDescription(answer);
                    await sig.SendAnswerAsync(answer.sdp, ct);
                    AfterSdpSent();
                    break;
                case AnswerMessage a when isHost:
                    var ares = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = a.Sdp });
                    if (ares != SetDescriptionResultEnum.OK) throw new InvalidOperationException($"setRemoteDescription: {ares}");
                    FlushRemote();
                    break;
                case CandidateMessage c:
                    var init = new RTCIceCandidateInit { candidate = c.Candidate, sdpMid = c.SdpMid, sdpMLineIndex = (ushort)(c.SdpMLineIndex ?? 0) };
                    bool ready;
                    lock (gate) { ready = remoteSet; if (!ready) pendingRemote.Add(init); }
                    if (ready) ApplyRemote(init);
                    break;
                case EndOfCandidatesMessage:
                    Console.WriteLine("REMOTE end-of-candidates");
                    break;
                case ByeMessage b:
                    Console.WriteLine("REMOTE bye");
                    peerBye.TrySetResult();
                    break;
                case PeerLeftMessage:
                    Console.WriteLine("REMOTE peer-left");
                    peerBye.TrySetResult();
                    break;
            }
        }
    });

    void FlushRemote()
    {
        List<RTCIceCandidateInit> batch;
        lock (gate) { remoteSet = true; batch = new(pendingRemote); pendingRemote.Clear(); }
        foreach (var c in batch) ApplyRemote(c);
    }

    // First of: the data-channel exchange finished, signaling died, or the timeout fired.
    using var reg2 = ct.Register(() => done.TrySetCanceled());
    var finished = await Task.WhenAny(done.Task, reader);
    if (finished == reader) await reader; // surfaces a signaling exception; a clean end before result is a failure
    int rc = finished == reader ? Fail("signaling ended before the data-channel exchange finished") : await done.Task;

    if (isHost || rc != 0)
    {
        try { await sig.SendByeAsync("probe-done", CancellationToken.None); } catch { }
    }
    else
    {
        // The joiner leaves only when the host says bye (sent after the host has seen "done-ack"), so its
        // ack cannot be lost to an early close. Bounded by the overall timeout.
        stage = "wait-for-bye";
        await Task.WhenAny(peerBye.Task, reader);
        try { await sig.SendByeAsync("probe-done", CancellationToken.None); } catch { }
    }
    sendQueue.Writer.TryComplete();
    try { await sender.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
    pc.close();
    return rc;
}

// How the selected pair is obtained: the peer connection's single RTP/ICE channel exposes the checklist
// entry that connectivity checks nominated; its local and remote candidates carry the type.
static string ReportPair(RTCPeerConnection pc)
{
    var entry = pc.GetRtpChannel()?.NominatedEntry;
    if (entry == null) { Console.WriteLine("SELECTED-PAIR local=unknown remote=unknown"); return "unknown/unknown"; }
    string l = entry.LocalCandidate.type.ToString(), r = entry.RemoteCandidate.type.ToString();
    Console.WriteLine($"SELECTED-PAIR local={l} remote={r}");
    return $"{l}/{r}";
}

static int Fail(string reason)
{
    Console.WriteLine($"RESULT fail {reason}");
    return 1;
}

static (string host, int port) SplitRouter(string url)
{
    // SignalingClient takes a host (optionally with scheme) and a port; 443/80 are the scheme defaults.
    if (!url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;
    var u = new Uri(url);
    return ($"{u.Scheme}://{u.Host}", u.Port);
}
