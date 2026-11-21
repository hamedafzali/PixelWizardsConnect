using FFmpeg.AutoGen;
using SIPSorceryMedia.FFmpeg;
using Xunit;
using Xunit.Abstractions;

namespace PixelWizard.FFmpeg.Tests;

/// <summary>Skips the test unless PIXELWIZARD_FFMPEG_DIR is set to the built FFmpeg libraries.</summary>
public sealed class FactIfFfmpegAttribute : FactAttribute
{
    public const string EnvVar = "PIXELWIZARD_FFMPEG_DIR";

    public FactIfFfmpegAttribute()
    {
        var dir = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            Skip = $"{EnvVar} is not set to a directory of FFmpeg libraries";
    }
}

/// <summary>
/// What the vendored FFmpeg exposes and whether it works: the H.264 encoders it contains, whether
/// one opens, and whether frames survive encode then decode. Uses FFmpegInit (the loader the app
/// will use) so a library SIPSorceryMedia.FFmpeg needs at load time but the build lacks fails here.
/// </summary>
public sealed unsafe class FfmpegProbeTests
{
    private const int Width = 1280, Height = 720, FrameCount = 12;
    private static readonly object InitLock = new();
    private static bool _initialised;

    private readonly ITestOutputHelper _out;
    public FfmpegProbeTests(ITestOutputHelper output) => _out = output;

    private static void EnsureInitialised()
    {
        lock (InitLock)
        {
            if (_initialised) return;
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING,
                Environment.GetEnvironmentVariable(FactIfFfmpegAttribute.EnvVar));
            _initialised = true;
        }
    }

    private static List<string> H264EncoderNames()
    {
        var names = new List<string>();
        void* opaque = null;
        AVCodec* codec;
        while ((codec = ffmpeg.av_codec_iterate(&opaque)) != null)
            if (ffmpeg.av_codec_is_encoder(codec) != 0 && codec->id == AVCodecID.AV_CODEC_ID_H264)
                names.Add(ffmpeg.avcodec_get_name(codec->id) is { } _ ? Marshal(codec->name) : "?");
        return names;
    }

    private static string Marshal(byte* p) => System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)p) ?? "";

    [FactIfFfmpeg]
    public void Libraries_load_and_report_a_version()
    {
        EnsureInitialised();
        var version = ffmpeg.av_version_info();
        _out.WriteLine($"FFmpeg {version}; avcodec {ffmpeg.avcodec_version()}, avutil {ffmpeg.avutil_version()}");
        Assert.NotEmpty(version);
    }

    [FactIfFfmpeg]
    public void Build_is_lgpl_and_lists_its_h264_encoders()
    {
        EnsureInitialised();
        var config = ffmpeg.avcodec_configuration();
        _out.WriteLine("configuration: " + config);
        Assert.Contains("--disable-gpl", config);
        Assert.DoesNotContain("--enable-gpl", config);
        Assert.DoesNotContain("--enable-nonfree", config);
        Assert.DoesNotContain("--enable-libx264", config);
        Assert.Contains("LGPL", ffmpeg.avcodec_license());

        var encoders = H264EncoderNames();
        _out.WriteLine("H.264 encoders: " + string.Join(", ", encoders));
        Assert.NotEmpty(encoders);
        Assert.DoesNotContain("libx264", encoders);
        Assert.True(ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264) != null);
    }

    [FactIfFfmpeg]
    public void At_least_one_h264_encoder_opens_and_frames_round_trip()
    {
        EnsureInitialised();
        var opened = new List<string>();
        var failures = new List<string>();
        foreach (var name in H264EncoderNames())
        {
            foreach (var fmt in new[] { AVPixelFormat.AV_PIX_FMT_NV12, AVPixelFormat.AV_PIX_FMT_YUV420P })
            {
                var attempt = $"{name}/{fmt}";
                try
                {
                    var (frames, avgLuma) = EncodeDecode(name, fmt);
                    _out.WriteLine($"{attempt}: opened, {frames} frames decoded, mean luma {avgLuma:F1}");
                    Assert.True(frames > 0, $"{attempt} decoded no frames");
                    Assert.InRange(avgLuma, SourceLuma - 25, SourceLuma + 25);
                    opened.Add(attempt);
                    break;
                }
                catch (ApplicationException ex)
                {
                    failures.Add($"{attempt}: {ex.Message}");
                    _out.WriteLine($"{attempt}: {ex.Message}");
                }
            }
        }
        Assert.True(opened.Count > 0, "No H.264 encoder opened. " + string.Join("; ", failures));
        _out.WriteLine("Opened: " + string.Join(", ", opened));
    }

    // Deliberately no FFmpegVideoEncoder test. On macOS it picks AV_PIX_FMT_VIDEOTOOLBOX (the first
    // pixel format h264_videotoolbox lists) but feeds it software frames, and FFmpeg's assertion
    // (videotoolboxenc.c: frame->format == AV_PIX_FMT_VIDEOTOOLBOX) aborts the whole process, with or
    // without a hardware device type or SetCodec. The raw avcodec path above works; P4.8 must drive
    // the encoder itself (or fix the pixel format) rather than rely on FFmpegVideoEncoder as-is.

    private const double SourceLuma = 120;

    // Encodes FrameCount synthetic frames and decodes the stream. Throws ApplicationException when
    // the encoder will not open or reject frames (so the caller can try the next one).
    private static (int frames, double meanLuma) EncodeDecode(string encoderName, AVPixelFormat fmt)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        if (codec == null) throw new ApplicationException("encoder not found");
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        ctx->width = Width; ctx->height = Height; ctx->pix_fmt = fmt;
        ctx->time_base = new AVRational { num = 1, den = 30 };
        ctx->framerate = new AVRational { num = 30, den = 1 };
        ctx->bit_rate = 4_000_000; ctx->gop_size = 30; ctx->max_b_frames = 0;
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        var packets = new List<byte[]>();
        try
        {
            if (ffmpeg.avcodec_open2(ctx, codec, null) < 0) throw new ApplicationException("avcodec_open2 failed");
            frame->format = (int)fmt; frame->width = Width; frame->height = Height;
            if (ffmpeg.av_frame_get_buffer(frame, 0) < 0) throw new ApplicationException("av_frame_get_buffer failed");

            for (var i = 0; i < FrameCount; i++)
            {
                ffmpeg.av_frame_make_writable(frame);
                Fill(frame, fmt);
                frame->pts = i;
                if (ffmpeg.avcodec_send_frame(ctx, frame) < 0) throw new ApplicationException("send_frame failed");
                Drain(ctx, pkt, packets);
            }
            ffmpeg.avcodec_send_frame(ctx, null);
            Drain(ctx, pkt, packets);
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&ctx);
        }
        if (packets.Count == 0) throw new ApplicationException("encoder produced no packets");
        return Decode(packets);
    }

    private static void Fill(AVFrame* f, AVPixelFormat fmt)
    {
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                f->data[0][y * f->linesize[0] + x] = (byte)(SourceLuma + (x * 20 / Width));
        if (fmt == AVPixelFormat.AV_PIX_FMT_NV12)
            for (var y = 0; y < Height / 2; y++)
                for (var x = 0; x < Width; x += 2)
                { f->data[1][y * f->linesize[1] + x] = 128; f->data[1][y * f->linesize[1] + x + 1] = 128; }
        else
            for (var y = 0; y < Height / 2; y++)
                for (var x = 0; x < Width / 2; x++)
                { f->data[1][y * f->linesize[1] + x] = 128; f->data[2][y * f->linesize[2] + x] = 128; }
    }

    private static void Drain(AVCodecContext* ctx, AVPacket* pkt, List<byte[]> into)
    {
        while (ffmpeg.avcodec_receive_packet(ctx, pkt) == 0)
        {
            var bytes = new byte[pkt->size];
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)pkt->data, bytes, 0, pkt->size);
            into.Add(bytes);
            ffmpeg.av_packet_unref(pkt);
        }
    }

    private static void Receive(AVCodecContext* ctx, AVFrame* frame, ref int frames, ref double luma)
    {
        while (ffmpeg.avcodec_receive_frame(ctx, frame) == 0)
        {
            Assert.Equal(Width, frame->width);
            Assert.Equal(Height, frame->height);
            long sum = 0;
            for (var y = 0; y < Height; y += 8)
                for (var x = 0; x < Width; x += 8) sum += frame->data[0][y * frame->linesize[0] + x];
            luma = sum / (double)((Height / 8) * (Width / 8));
            frames++;
        }
    }

    private static (int frames, double meanLuma) Decode(List<byte[]> packets)
    {
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        var frames = 0;
        double luma = 0;
        try
        {
            if (ffmpeg.avcodec_open2(ctx, codec, null) < 0) throw new ApplicationException("h264 decoder failed to open");
            foreach (var p in packets)
            {
                fixed (byte* src = p)
                {
                    // av_packet_from_data wants av_malloc'd, padded memory; copy into a fresh packet buffer.
                    ffmpeg.av_new_packet(pkt, p.Length);
                    Buffer.MemoryCopy(src, pkt->data, p.Length, p.Length);
                }
                ffmpeg.avcodec_send_packet(ctx, pkt);
                ffmpeg.av_packet_unref(pkt);
                Receive(ctx, frame, ref frames, ref luma);
            }
            ffmpeg.avcodec_send_packet(ctx, null);
            Receive(ctx, frame, ref frames, ref luma);
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&ctx);
        }
        return (frames, luma);
    }
}
