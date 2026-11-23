using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace PixelWizard.Media.H264;

/// <summary>
/// Software H.264 decoder: Annex-B access units in, BGRA frames out. A change of resolution
/// (new SPS) reallocates the scaler. Not thread-safe.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private SwsContext* _sws;
    private bool _disposed;

    private long _errors;

    private H264Decoder() { }

    /// <summary>Access units the decoder rejected as corrupt or undecodable (they are dropped).</summary>
    public long ErrorCount => Interlocked.Read(ref _errors);

    public static bool TryCreate(out H264Decoder? decoder, out string reason)
    {
        decoder = null;
        if (!FfmpegLoader.IsLoaded) { reason = "FFmpeg is not loaded"; return false; }
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null) { reason = "no h264 decoder in this FFmpeg build"; return false; }
        var d = new H264Decoder();
        d._ctx = ffmpeg.avcodec_alloc_context3(codec);
        d._pkt = ffmpeg.av_packet_alloc();
        d._frame = ffmpeg.av_frame_alloc();
        if (d._ctx == null || d._pkt == null || d._frame == null || ffmpeg.avcodec_open2(d._ctx, codec, null) < 0)
        {
            d.Dispose();
            reason = "h264 decoder failed to open";
            return false;
        }
        reason = "";
        decoder = d;
        return true;
    }

    /// <summary>Decodes one access unit; returns the frames it completed (usually one, none while waiting for a keyframe).</summary>
    public IReadOnlyList<DecodedFrame> Decode(ReadOnlySpan<byte> annexB)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new List<DecodedFrame>();
        if (annexB.IsEmpty) return result;

        if (ffmpeg.av_new_packet(_pkt, annexB.Length) < 0) throw new OutOfMemoryException("av_new_packet failed");
        annexB.CopyTo(new Span<byte>(_pkt->data, annexB.Length));
        var r = ffmpeg.avcodec_send_packet(_ctx, _pkt);
        ffmpeg.av_packet_unref(_pkt);
        if (r < 0 && r != ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            Interlocked.Increment(ref _errors);
            return result; // corrupt or undecodable data: drop it, the next keyframe recovers
        }

        while (ffmpeg.avcodec_receive_frame(_ctx, _frame) == 0)
        {
            int w = _frame->width, h = _frame->height;
            _sws = ffmpeg.sws_getCachedContext(_sws, w, h, (AVPixelFormat)_frame->format,
                w, h, AVPixelFormat.AV_PIX_FMT_BGRA, 2 /* SWS_BILINEAR */, null, null, null);
            if (_sws == null) throw new InvalidOperationException("sws_getCachedContext failed");
            var stride = w * 4;
            var bgra = new byte[stride * h];
            fixed (byte* dst = bgra)
            {
                var dstData = new byte*[] { dst, null, null, null };
                var dstStride = new[] { stride, 0, 0, 0 };
                ffmpeg.sws_scale(_sws, _frame->data, _frame->linesize, 0, h, dstData, dstStride);
            }
            result.Add(new DecodedFrame(bgra, w, h, stride));
            ffmpeg.av_frame_unref(_frame);
        }
        return result;
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
