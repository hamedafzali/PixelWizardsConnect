// Throwaway spike code. Ugly on purpose. Never referenced by production code.
using System.Diagnostics;
using System.Net;
using System.Text;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

// T8 Windows-partial: FFmpeg's native lib location differs per OS/distro, so
// this is now an env var override (default stays T8's original Homebrew path
// on macOS) instead of a hardcoded path, letting the same Program.cs run
// unmodified against a downloaded Windows FFmpeg build.
string ffmpegLibPath = Environment.GetEnvironmentVariable("SPIKE_FFMPEG_LIB_PATH")
    ?? (OperatingSystem.IsMacOS() ? "/opt/homebrew/lib" : "");
FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, ffmpegLibPath);

string sigDir = args.Length > 0 ? args[0] : "/tmp/webrtc-spike-sig";
Directory.CreateDirectory(sigDir);
string offerPath = Path.Combine(sigDir, "offer.json");
string answerPath = Path.Combine(sigDir, "answer.json");
File.Delete(offerPath);
File.Delete(answerPath);

// T8 Windows-partial (finding #4 addendum): relay-only is right for the real
// relay-traversal test (proven on macOS), but the "does SIPSorcery merge
// gathered candidates into localDescription.sdp" question is library
// behaviour independent of candidate type -- host candidates gather with no
// TURN server involved. SPIKE_ICE_TRANSPORT_POLICY lets that check run with
// policy=all (host candidates on loopback) without touching the default
// relay-only path everything else here uses.
var icePolicy = Environment.GetEnvironmentVariable("SPIKE_ICE_TRANSPORT_POLICY") == "all"
    ? RTCIceTransportPolicy.all
    : RTCIceTransportPolicy.relay;
var pc = new RTCPeerConnection(new RTCConfiguration
{
    iceServers = new System.Collections.Generic.List<RTCIceServer> {
        new RTCIceServer { urls = "turn:127.0.0.1:3478", username = "spike", credential = "spikepass" }
    },
    iceTransportPolicy = icePolicy,
});
Console.WriteLine("[dotnet] iceTransportPolicy=" + icePolicy);

var gatheredCandidates = new List<string>();
pc.onicecandidate += c => { Console.WriteLine($"[dotnet] local candidate: {c?.candidate}"); if (c?.candidate != null) gatheredCandidates.Add(c.candidate); };
pc.onconnectionstatechange += s => Console.WriteLine($"[dotnet] connection state -> {s}");
pc.oniceconnectionstatechange += s => Console.WriteLine($"[dotnet] ice state -> {s}");

// ---- Video: use FFmpegVideoEncoder directly against synthetic frames.
// We drive raw BGR frames into a manually-built VideoTrack rather than
// FFmpegScreenSource, because live screen capture on macOS needs an
// interactive Screen Recording permission grant that is not obtainable in
// this headless/automated run. This substitution avoids an unrelated
// macOS permission wall while still exercising the real encode+RTP path.
// Explicit fmtp is required: SIPSorcery's H264 negotiation checks
// profile-level-id/packetization-mode compatibility against the answer, and
// an offer with no fmtp at all fails that check (observed as
// setRemoteDescription returning VideoIncompatible against a real Chrome
// answer). 42e01f/packetization-mode=1 matches one of Chrome's offered
// H264 profiles.
var videoFormat = new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.H264, 96,
    90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"));
var videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video,
    false, new System.Collections.Generic.List<SDPAudioVideoMediaFormat> { videoFormat }, MediaStreamStatusEnum.SendRecv);
pc.addTrack(videoTrack);

// ---- Data channel: continuous small messages while video runs.
var dc = await pc.createDataChannel("spike");
int dcSent = 0;
dc.onopen += () => Console.WriteLine("[dotnet] data channel open");
dc.onmessage += (chan, proto, data) =>
{
    Console.WriteLine($"[dotnet] data channel recv: {Encoding.UTF8.GetString(data)}");
};

// ---- FFmpeg H.264 encoder. Try hardware (VideoToolbox) first, note whether it actually engages.
Dictionary<string, string> encOpts = new();
FFmpegVideoEncoder encoder;
bool usingHardware = true;
if (OperatingSystem.IsMacOS())
{
    try
    {
        encoder = new FFmpegVideoEncoder(encOpts, FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX);
        Console.WriteLine("[dotnet] FFmpegVideoEncoder constructed with AV_HWDEVICE_TYPE_VIDEOTOOLBOX");
    }
    catch (Exception ex)
    {
        Console.WriteLine("[dotnet] VideoToolbox hw device init failed, falling back to software: " + ex.Message);
        usingHardware = false;
        encoder = new FFmpegVideoEncoder(encOpts, FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE);
    }
}
else
{
    // T8 Windows-partial runs on GitHub Actions windows-latest, which has no
    // GPU/Quick Sync hardware encoder -- skip the hardware attempt outright
    // (rather than trying D3D11VA/DXVA2 and catching a failure) so the log
    // states plainly that this is an environment limitation, not an error.
    Console.WriteLine("[dotnet] non-macOS host: no hardware encoder attempt (windows-latest CI has no GPU); encoding in software");
    usingHardware = false;
    encoder = new FFmpegVideoEncoder(encOpts, FFmpeg.AutoGen.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE);
}
Console.WriteLine("[dotnet] usingHardware=" + usingHardware);

const int width = 1920, height = 1080;
byte[] MakeFrame(int frameNo)
{
    // Simple synthetic BGR24 frame: solid color that cycles + an
    // embedded little-endian long timestamp in the first 8 bytes of row 0,
    // used later for a rough software glass-to-glass latency estimate
    // (see README in this spike dir for why this replaces a photographed
    // clock).
    var buf = new byte[width * height * 3];
    byte c = (byte)(frameNo % 256);
    for (int i = 0; i < buf.Length; i += 3) { buf[i] = c; buf[i + 1] = (byte)(255 - c); buf[i + 2] = 128; }
    var ts = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    Array.Copy(ts, 0, buf, 0, ts.Length);
    return buf;
}

pc.OnRtpPacketReceived += (ep, media, pkt) => { };

// ---- Signaling: crude file drop. Wait for browser's process to write an offer? No —
// we are the offerer here (desktop hosts). Create offer, write it, poll for the answer file.
var offer = pc.createOffer();
await pc.setLocalDescription(offer);
while (pc.signalingState != RTCSignalingState.have_local_offer) { await Task.Delay(50); }
await Task.Delay(2500); // crude: let candidates gather instead of proper ICE-complete signalling / trickle
// Workaround: pc.localDescription.sdp never gains the trickled candidates from
// onicecandidate (confirmed by inspecting the written offer -- zero a=candidate
// lines even after gathering finished), so a non-trickle blob exchange sends
// Chrome an offer it can never pair against. Append them manually.
string offerSdpText = pc.localDescription.sdp.ToString();
if (gatheredCandidates.Count > 0)
{
    // Insert right after the first m= section's own lines (before the next
    // "m=" line), not appended at the end of the whole SDP -- appending at
    // the end put the candidate under the LAST m= section (application/data
    // channel) instead of the first bundled m= section (video, mid:0), which
    // Chrome's BUNDLE-aware parser needs it under. First naive append attempt
    // silently produced an offer where the video section had zero candidates.
    var lines = offerSdpText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    var outLines = new List<string>();
    bool inserted = false;
    int mLineCount = 0;
    foreach (var line in lines)
    {
        if (line.StartsWith("m="))
        {
            mLineCount++;
            if (mLineCount == 2 && !inserted)
            {
                foreach (var cand in gatheredCandidates) outLines.Add("a=candidate:" + cand);
                outLines.Add("a=end-of-candidates");
                inserted = true;
            }
        }
        outLines.Add(line);
    }
    if (!inserted)
    {
        foreach (var cand in gatheredCandidates) outLines.Add("a=candidate:" + cand);
        outLines.Add("a=end-of-candidates");
    }
    offerSdpText = string.Join("\r\n", outLines) + "\r\n";
}
File.WriteAllText(offerPath, offerSdpText);
Console.WriteLine("[dotnet] wrote offer to " + offerPath);

Console.WriteLine("[dotnet] waiting for answer at " + answerPath);
while (!File.Exists(answerPath)) await Task.Delay(200);
await Task.Delay(300);
string answerSdp = File.ReadAllText(answerPath);
var answerDesc = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp };
var setResult = pc.setRemoteDescription(answerDesc);
Console.WriteLine("[dotnet] setRemoteDescription result: " + setResult);

var sw = Stopwatch.StartNew();
int frameNo = 0;
var proc = Process.GetCurrentProcess();
var cpuLog = new List<(double sec, double cpuPct, long rssMb)>();
TimeSpan lastCpu = proc.TotalProcessorTime;
var lastSample = sw.Elapsed;

_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(1000);
        proc.Refresh();
        var nowCpu = proc.TotalProcessorTime;
        var nowElapsed = sw.Elapsed;
        double cpuPct = (nowCpu - lastCpu).TotalMilliseconds / (nowElapsed - lastSample).TotalMilliseconds / Environment.ProcessorCount * 100.0;
        lastCpu = nowCpu; lastSample = nowElapsed;
        long rssMb = proc.WorkingSet64 / 1024 / 1024;
        cpuLog.Add((nowElapsed.TotalSeconds, cpuPct, rssMb));
        Console.WriteLine($"[dotnet][sample] t={nowElapsed.TotalSeconds:F0}s cpu={cpuPct:F1}% rssMb={rssMb}");
    }
});

_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(500);
        if (dc.readyState == RTCDataChannelState.open)
        {
            dc.send($"ping {dcSent++} t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        }
    }
});

int runSeconds = args.Length > 1 ? int.Parse(args[1]) : 600;
Console.WriteLine($"[dotnet] running for {runSeconds}s...");
while (sw.Elapsed.TotalSeconds < runSeconds)
{
    var frame = MakeFrame(frameNo++);
    try
    {
        var encoded = encoder.EncodeVideo(width, height, frame, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.H264);
        if (encoded != null && encoded.Length > 0 && pc.connectionState == RTCPeerConnectionState.connected)
        {
            pc.SendVideo(3000, encoded); // 3000 = duration units per SIPSorcery sample convention @ 90kHz/30fps-ish
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("[dotnet] encode/send error: " + ex.Message);
    }
    await Task.Delay(33); // ~30fps
}

Console.WriteLine("[dotnet] run complete. CPU/mem samples:");
foreach (var s in cpuLog) Console.WriteLine($"  t={s.sec:F0}s cpu={s.cpuPct:F1}% rssMb={s.rssMb}");
File.WriteAllLines(Path.Combine(sigDir, "cpu-mem-log.csv"), cpuLog.Select(s => $"{s.sec},{s.cpuPct},{s.rssMb}"));

double achievedEncodeFps = frameNo / sw.Elapsed.TotalSeconds;
Console.WriteLine($"[dotnet] achieved encode fps: {achievedEncodeFps:F2} ({frameNo} frames / {sw.Elapsed.TotalSeconds:F1}s, requested ~30fps via 33ms delay)");
