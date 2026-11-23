using System.Diagnostics;
using PixelWizard.Core;
using PixelWizard.Core.Interfaces;

namespace PixelWizard.Media.H264;

/// <summary>
/// Capture, encode, send: grabs raw frames at a target rate, encodes them with <see cref="H264Encoder"/> and
/// writes each access unit to the video track. Built and started only after the host approved the viewer
/// (<c>HostSession.Approve</c>, PHASE4-PLAN D4); nothing here exists before that.
/// The first frame is a keyframe, a periodic IDR comes from the encoder's GOP, and a viewer PLI
/// (<see cref="IVideoTrackTransport.KeyframeRequested"/>) forces one. The encoder has pipeline latency
/// (VideoToolbox about 3 frames, h264_mf more), so the next output after a request is not the IDR; the
/// requester just waits. A change of capture size re-creates the encoder at the new size, again opening with
/// an IDR. The capture is not owned: the caller disposes it.
/// </summary>
public sealed class H264HostPipeline : IHostVideoPipeline
{
    public const int DefaultFps = 15;
    private readonly IVideoTrackTransport _track;
    private readonly IRawScreenCapture _capture;
    private readonly int _fps;
    private readonly int _gopSeconds;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private H264Encoder? _encoder;
    private Thread? _thread;
    private volatile bool _keyframeRequested;
    private bool _started;
    private bool _stopped;
    private volatile bool _runEnded;
    private long _framesEncoded, _accessUnitsSent, _keyframesSent;

    public H264HostPipeline(IVideoTrackTransport track, IRawScreenCapture capture, int fps = DefaultFps, int gopSeconds = 2, Action<string>? log = null)
    {
        _track = track ?? throw new ArgumentNullException(nameof(track));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _fps = fps; _gopSeconds = gopSeconds; _log = log;
    }

    public string? EncoderName => _encoder?.EncoderName;
    public long FramesEncoded => Interlocked.Read(ref _framesEncoded);
    public long AccessUnitsSent => Interlocked.Read(ref _accessUnitsSent);
    /// <summary>Access units that contained an IDR slice.</summary>
    public long KeyframesSent => Interlocked.Read(ref _keyframesSent);

    public bool IsRunning { get { lock (_gate) return _started && !_stopped && !_runEnded; } }

    public bool TryStart(out string reason)
    {
        lock (_gate)
        {
            if (_started || _stopped) { reason = "already started or stopped"; return false; }
            if (!FfmpegLoader.IsLoaded) { reason = "FFmpeg is not loaded"; return false; }
            RawFrame? first = null;
            try
            {
                first = _capture.CaptureRaw();
                if (first == null) { reason = "screen capture returned no frame"; return false; }
                if (!OpenEncoder(first.Width, first.Height, out reason)) return false;
                _track.KeyframeRequested += OnKeyframeRequested;
                _started = true;
                var firstFrame = first; first = null;
                _thread = new Thread(() => Run(firstFrame)) { IsBackground = true, Name = "h264-host-pipeline" };
                _thread.Start();
                return true;
            }
            catch (Exception ex) { reason = ex.Message; return false; }
            finally { first?.Dispose(); }
        }
    }

    private bool OpenEncoder(int w, int h, out string reason)
    {
        _encoder?.Dispose();
        _encoder = null;
        // About 0.1 bit per pixel per frame, within a range that suits a screen share over a relay.
        int bitrate = (int)Math.Clamp((long)w * h * _fps / 10, 1_000_000, 8_000_000);
        if (!H264Encoder.TryCreate(w, h, _fps, bitrate, out var enc, out reason, gopSize: _fps * _gopSeconds)) return false;
        _encoder = enc;
        _log?.Invoke($"H.264 host encoder {enc!.EncoderName} {w}x{h}@{_fps} {bitrate / 1000} kbps");
        return true;
    }

    private void OnKeyframeRequested() => _keyframeRequested = true;

    private void Run(RawFrame? first)
    {
        var token = _cts.Token;
        var clock = Stopwatch.StartNew();
        long frameTicks = Stopwatch.Frequency / _fps;
        long next = 0;
        long lastSend = 0;
        bool sentAny = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                long wait = next - clock.ElapsedTicks;
                if (wait > 0) { if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency))) break; }
                next = Math.Max(next + frameTicks, clock.ElapsedTicks); // a slow capture skips ahead instead of bursting

                var frame = first ?? _capture.CaptureRaw();
                first = null;
                if (frame == null) continue;
                using (frame)
                {
                    if (frame.Width != _encoder!.Width || frame.Height != _encoder.Height)
                    {
                        if (!OpenEncoder(frame.Width, frame.Height, out var why)) { _log?.Invoke("H.264 re-open failed: " + why); break; }
                    }
                    if (_keyframeRequested) { _keyframeRequested = false; _encoder!.RequestKeyframe(); }
                    var units = _encoder!.Encode(frame);
                    Interlocked.Increment(ref _framesEncoded);
                    foreach (var au in units)
                    {
                        long now = clock.ElapsedTicks;
                        long dur = sentAny ? now - lastSend : frameTicks;
                        lastSend = now; sentAny = true;
                        if (token.IsCancellationRequested) break;
                        _track.SendVideoAccessUnit(au.AnnexB, (uint)Math.Max(1, dur * 90000 / Stopwatch.Frequency));
                        Interlocked.Increment(ref _accessUnitsSent);
                        if (AnnexB.NalTypes(au.AnnexB).Contains(5)) Interlocked.Increment(ref _keyframesSent);
                    }
                }
            }
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) _log?.Invoke("H.264 host pipeline stopped: " + ex.Message); }
        finally
        {
            _runEnded = true;
            first?.Dispose();
            // The encoder is only ever touched by this thread once it runs, so it is released here.
            lock (_gate) { _encoder?.Dispose(); _encoder = null; }
        }
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _track.KeyframeRequested -= OnKeyframeRequested;
            try { _cts.Cancel(); } catch { }
            t = _thread;
        }
        if (t != null) { if (t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(3)); }
        else lock (_gate) { _encoder?.Dispose(); _encoder = null; } // never started
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
