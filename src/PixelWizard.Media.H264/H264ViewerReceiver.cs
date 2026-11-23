using System.Diagnostics;
using System.Threading.Channels;
using PixelWizard.Core.Interfaces;

namespace PixelWizard.Media.H264;

/// <summary>
/// Viewer side of the video track: access units arrive on the transport's thread, are queued, and decoded on
/// one background thread; each decoded BGRA picture goes to <paramref name="onFrame"/> on that thread. The
/// caller presents it, and a UI app must hand it to the UI thread itself: this class never touches the UI.
/// An undecodable unit is dropped and a keyframe is requested (PLI), at most once per
/// <see cref="PliInterval"/>; so is a unit lost to a full queue.
/// </summary>
public sealed class H264ViewerReceiver : IDisposable
{
    public static readonly TimeSpan PliInterval = TimeSpan.FromMilliseconds(500);
    private const int QueueCapacity = 16;

    private readonly IVideoTrackTransport _track;
    private readonly H264Decoder _decoder;
    private readonly Action<DecodedFrame> _onFrame;
    private readonly Action<string>? _log;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastPliMs = long.MinValue / 2;
    private volatile bool _lostUnit;
    private bool _everDecoded;
    private long _framesDecoded, _pliRequests, _unitsReceived;
    private int _disposed;

    private H264ViewerReceiver(IVideoTrackTransport track, H264Decoder decoder, Action<DecodedFrame> onFrame, Action<string>? log)
    {
        _track = track; _decoder = decoder; _onFrame = onFrame; _log = log;
        _track.VideoAccessUnitReceived += OnUnit;
        _thread = new Thread(Run) { IsBackground = true, Name = "h264-viewer-decode" };
        _thread.Start();
    }

    public static bool TryCreate(IVideoTrackTransport track, Action<DecodedFrame> onFrame, out H264ViewerReceiver? receiver,
        out string reason, Action<string>? log = null)
    {
        receiver = null;
        if (!H264Decoder.TryCreate(out var dec, out reason)) return false;
        receiver = new H264ViewerReceiver(track, dec!, onFrame, log);
        return true;
    }

    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);
    public long KeyframeRequests => Interlocked.Read(ref _pliRequests);
    public long UnitsReceived => Interlocked.Read(ref _unitsReceived);

    private void OnUnit(byte[] annexB, uint _)
    {
        Interlocked.Increment(ref _unitsReceived);
        // Never block the transport thread. A unit that does not fit breaks the reference chain: ask for a keyframe.
        if (!_queue.Writer.TryWrite(annexB)) _lostUnit = true;
    }

    private void Run()
    {
        var token = _cts.Token;
        try
        {
            while (_queue.Reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
            {
                while (_queue.Reader.TryRead(out var au))
                {
                    if (token.IsCancellationRequested) return;
                    bool bad = _lostUnit;
                    _lostUnit = false;
                    long errorsBefore = _decoder.ErrorCount;
                    IReadOnlyList<DecodedFrame> frames = Array.Empty<DecodedFrame>();
                    try { frames = _decoder.Decode(au); }
                    catch (Exception ex) { bad = true; _log?.Invoke("H.264 decode failed: " + ex.Message); }
                    if (_decoder.ErrorCount != errorsBefore) bad = true;
                    // Before the first picture, a unit that is not an IDR cannot start the stream.
                    if (!_everDecoded && frames.Count == 0 && !AnnexB.NalTypes(au).Contains(5)) bad = true;
                    if (bad) RequestKeyframe();
                    foreach (var f in frames)
                    {
                        _everDecoded = true;
                        Interlocked.Increment(ref _framesDecoded);
                        try { _onFrame(f); } catch (Exception ex) { _log?.Invoke("H.264 frame handler failed: " + ex.Message); }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log?.Invoke("H.264 viewer receiver stopped: " + ex.Message); }
        finally { _decoder.Dispose(); } // only this thread ever touches the decoder
    }

    private void RequestKeyframe()
    {
        long now = _clock.ElapsedMilliseconds;
        if (now - _lastPliMs < (long)PliInterval.TotalMilliseconds) return;
        _lastPliMs = now;
        Interlocked.Increment(ref _pliRequests);
        _track.RequestKeyframe();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _track.VideoAccessUnitReceived -= OnUnit;
        _queue.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        if (_thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(3));
        // The decoder is released by the decode thread itself when it exits.
    }
}
