using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using PixelWizard.Core;

namespace PixelWizard.Media.H264;

/// <summary>
/// H.264 encoder driving libavcodec directly. Tries the OS/hardware encoders in order and uses the
/// first that opens (the vendored LGPL build has no x264). Output is Annex-B with SPS/PPS in-band
/// on every keyframe, constrained baseline, no B-frames, low-latency settings.
/// Not thread-safe: call from one thread at a time.
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private SwsContext* _sws;
    private readonly AVPixelFormat _pixFmt;
    private readonly int _width, _height;
    private long _nextPts;
    private bool _keyframePending = true; // the first frame is always an IDR
    private bool _disposed;
    private const int SwsBilinear = 2;

    public string EncoderName { get; }
    public int Width => _width;
    public int Height => _height;
    /// <summary>Which options were applied or ignored, per encoder tried (for logging).</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    private H264Encoder(AVCodecContext* ctx, string name, AVPixelFormat pixFmt, int w, int h, List<string> diag)
    {
        _ctx = ctx; EncoderName = name; _pixFmt = pixFmt; _width = w; _height = h; Diagnostics = diag;
        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)pixFmt; _frame->width = w; _frame->height = h;
        if (ffmpeg.av_frame_get_buffer(_frame, 0) < 0) { Dispose(); throw new InvalidOperationException("av_frame_get_buffer failed"); }
    }

    public static string[] CandidateEncoders() =>
        OperatingSystem.IsWindows() ? new[] { "h264_nvenc", "h264_amf", "h264_mf" }
        : OperatingSystem.IsMacOS() ? new[] { "h264_videotoolbox" }
        : Array.Empty<string>();

    /// <summary>
    /// Opens the first working encoder. Returns false with a reason when none opens, so the caller
    /// can fall back to media-jpeg (D5). <paramref name="gopSize"/> is the periodic IDR interval in frames.
    /// </summary>
    public static bool TryCreate(int width, int height, int fps, int bitrate, out H264Encoder? encoder,
        out string reason, int gopSize = 0, IEnumerable<string>? candidates = null)
    {
        encoder = null;
        if (!FfmpegLoader.IsLoaded) { reason = "FFmpeg is not loaded"; return false; }
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0 || fps <= 0 || bitrate <= 0)
        { reason = $"invalid parameters {width}x{height}@{fps} {bitrate}bps"; return false; }
        if (gopSize <= 0) gopSize = fps * 2;

        var failures = new List<string>();
        foreach (var name in candidates ?? CandidateEncoders())
        {
            try
            {
                var e = TryOpen(name, width, height, fps, bitrate, gopSize, out var why);
                if (e != null) { encoder = e; reason = ""; return true; }
                failures.Add($"{name}: {why}");
            }
            catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
        }
        reason = failures.Count == 0 ? "no H.264 encoder is known for this OS" : string.Join("; ", failures);
        return false;
    }

    private static H264Encoder? TryOpen(string name, int w, int h, int fps, int bitrate, int gop, out string why)
    {
        var diag = new List<string>();
        var codec = ffmpeg.avcodec_find_encoder_by_name(name);
        if (codec == null) { why = "not in this build"; return null; }
        var fmt = PickPixelFormat(codec, out var fmtNote);
        if (fmt == AVPixelFormat.AV_PIX_FMT_NONE) { why = fmtNote; return null; }

        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (ctx == null) { why = "avcodec_alloc_context3 failed"; return null; }
        try
        {
            ctx->width = w; ctx->height = h; ctx->pix_fmt = fmt;
            ctx->time_base = new AVRational { num = 1, den = fps };
            ctx->framerate = new AVRational { num = fps, den = 1 };
            ctx->bit_rate = bitrate; ctx->rc_max_rate = bitrate; ctx->rc_buffer_size = Math.Max(bitrate / fps * 2, 1);
            ctx->gop_size = gop; ctx->max_b_frames = 0;
            ctx->flags &= ~ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER; // parameter sets in-band, not extradata only
            ctx->thread_count = 1;
            diag.Add($"{name}: pix_fmt={fmt}");

            var priv = (IntPtr)ctx->priv_data;
            void Opt(string key, string value)
            {
                var r = ffmpeg.av_opt_set((void*)priv, key, value, 0);
                diag.Add($"{name}: {key}={value} {(r >= 0 ? "applied" : "ignored (" + r + ")")}");
            }
            // Constrained baseline where the encoder lets us ask for it. level 3.1 matches 42e01f.
            Opt("profile", name == "h264_amf" ? "constrained_baseline" : "baseline");
            Opt("level", "3.1");
            switch (name)
            {
                case "h264_videotoolbox":
                    Opt("realtime", "1");
                    Opt("allow_sw", "0");
                    Opt("prio_speed", "1");
                    break;
                case "h264_nvenc":
                    Opt("preset", "p1"); Opt("tune", "ll"); Opt("zerolatency", "1");
                    Opt("rc", "cbr"); Opt("repeat_spspps", "1"); Opt("forced-idr", "1");
                    break;
                case "h264_amf":
                    Opt("usage", "ultralowlatency"); Opt("rc", "cbr"); Opt("header_insertion_mode", "idr");
                    Opt("forced_idr", "1");
                    break;
                case "h264_mf":
                    Opt("rate_control", "cbr"); Opt("scenario", "display_remoting");
                    break;
            }

            var r = ffmpeg.avcodec_open2(ctx, codec, null);
            if (r < 0) { why = $"avcodec_open2 failed ({Err(r)})"; return null; }
            var enc = new H264Encoder(ctx, name, fmt, w, h, diag);
            ctx = null; // ownership moved
            why = "";
            return enc;
        }
        finally
        {
            if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
        }
    }

    private static AVPixelFormat PickPixelFormat(AVCodec* codec, out string note)
    {
        note = "";
        const AVCodecConfig cfg = AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT;
        void* list = null; int n = 0;
        var r = ffmpeg.avcodec_get_supported_config(null, codec, cfg, 0, &list, &n);
        if (r < 0 || list == null) return AVPixelFormat.AV_PIX_FMT_NV12; // no restriction advertised
        var fmts = (AVPixelFormat*)list;
        var has = new HashSet<AVPixelFormat>();
        for (var i = 0; i < n; i++) has.Add(fmts[i]);
        if (has.Contains(AVPixelFormat.AV_PIX_FMT_NV12)) return AVPixelFormat.AV_PIX_FMT_NV12;
        if (has.Contains(AVPixelFormat.AV_PIX_FMT_YUV420P)) return AVPixelFormat.AV_PIX_FMT_YUV420P;
        note = "accepts neither NV12 nor YUV420P: " + string.Join(",", has);
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    /// <summary>Makes the next encoded frame an IDR.</summary>
    public void RequestKeyframe() => _keyframePending = true;

    public IReadOnlyList<EncodedAccessUnit> Encode(RawFrame frame, bool forceKeyframe = false)
    {
        if (frame.Width != _width || frame.Height != _height)
            throw new ArgumentException($"Frame is {frame.Width}x{frame.Height}, encoder is {_width}x{_height}.");
        return Encode(frame.Pixels, frame.Stride, forceKeyframe);
    }

    /// <summary>Encodes one BGRA picture of the encoder's size. May return zero or more access units.</summary>
    public IReadOnlyList<EncodedAccessUnit> Encode(ReadOnlySpan<byte> bgra, int stride, bool forceKeyframe = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stride < _width * 4 || bgra.Length < (_height - 1) * stride + _width * 4)
            throw new ArgumentException("BGRA buffer too small for the encoder size.");

        _sws = ffmpeg.sws_getCachedContext(_sws, _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
            _width, _height, _pixFmt, SwsBilinear, null, null, null);
        if (_sws == null) throw new InvalidOperationException("sws_getCachedContext failed");

        var r = ffmpeg.av_frame_make_writable(_frame);
        if (r < 0) throw new InvalidOperationException("av_frame_make_writable failed: " + Err(r));

        fixed (byte* src = bgra)
        {
            var srcData = new byte*[] { src, null, null, null };
            var srcStride = new[] { stride, 0, 0, 0 };
            ffmpeg.sws_scale(_sws, srcData, srcStride, 0, _height, _frame->data, _frame->linesize);
        }

        _frame->pts = _nextPts++;
        if (_keyframePending || forceKeyframe)
        {
            _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            _frame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
            _keyframePending = false;
        }
        else
        {
            _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            _frame->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;
        }

        r = ffmpeg.avcodec_send_frame(_ctx, _frame);
        if (r < 0) throw new InvalidOperationException("avcodec_send_frame failed: " + Err(r));
        return Drain();
    }

    private List<EncodedAccessUnit> Drain()
    {
        var list = new List<EncodedAccessUnit>();
        while (ffmpeg.avcodec_receive_packet(_ctx, _pkt) == 0)
        {
            var bytes = new byte[_pkt->size];
            Marshal.Copy((IntPtr)_pkt->data, bytes, 0, _pkt->size);
            var key = (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            var pts = _pkt->pts;
            ffmpeg.av_packet_unref(_pkt);
            // Safety net: a keyframe must carry SPS/PPS in-band. If the encoder only put them in extradata, prepend.
            if (key && !AnnexB.NalTypes(bytes).Contains(7) && _ctx->extradata_size > 0)
            {
                var extra = new byte[_ctx->extradata_size];
                Marshal.Copy((IntPtr)_ctx->extradata, extra, 0, extra.Length);
                if (AnnexB.NalTypes(extra).Contains(7))
                {
                    var merged = new byte[extra.Length + bytes.Length];
                    extra.CopyTo(merged, 0); bytes.CopyTo(merged, extra.Length);
                    bytes = merged;
                }
            }
            list.Add(new EncodedAccessUnit(bytes, key, pts));
        }
        return list;
    }

    private static string Err(int code)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? code.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        var f = _frame; if (f != null) ffmpeg.av_frame_free(&f); _frame = null;
        var p = _pkt; if (p != null) ffmpeg.av_packet_free(&p); _pkt = null;
        var c = _ctx; if (c != null) ffmpeg.avcodec_free_context(&c); _ctx = null;
    }
}
