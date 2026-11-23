namespace PixelWizard.Media.H264;

/// <summary>
/// Finds and loads the vendored FFmpeg for the app, and probes what it can do. Looks at
/// PIXELWIZARD_FFMPEG_DIR first, then an <c>ffmpeg</c> directory next to the executable. When neither loads,
/// the peer advertises no H.264 and every session stays on JPEG. (Shipping the libraries is P4.9.)
/// </summary>
public static class FfmpegBootstrap
{
    public const string EnvVar = "PIXELWIZARD_FFMPEG_DIR";
    private static readonly object Gate = new();
    private static bool _done;
    private static string? _directory;
    private static string _status = "not probed";
    private static bool _canEncode, _canDecode;

    /// <summary>Directory FFmpeg was loaded from, or null.</summary>
    public static string? Directory { get { Ensure(); return _directory; } }
    /// <summary>One line for logs: what loaded, or why H.264 is off.</summary>
    public static string Status { get { Ensure(); return _status; } }
    /// <summary>FFmpeg loaded and an H.264 encoder opens (the host advertises H264 only then).</summary>
    public static bool CanEncode { get { Ensure(); return _canEncode; } }
    /// <summary>FFmpeg loaded and the H.264 decoder opens (the viewer advertises H264 only then).</summary>
    public static bool CanDecode { get { Ensure(); return _canDecode; } }

    public static IEnumerable<string> CandidateDirectories(string? envValue, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(envValue)) yield return envValue;
        yield return Path.Combine(baseDirectory, "ffmpeg");
    }

    private static void Ensure()
    {
        lock (Gate)
        {
            if (_done) return;
            _done = true;
            var tried = new List<string>();
            foreach (var dir in CandidateDirectories(Environment.GetEnvironmentVariable(EnvVar), AppContext.BaseDirectory))
            {
                if (!System.IO.Directory.Exists(dir)) { tried.Add($"{dir}: not found"); continue; }
                if (FfmpegLoader.TryLoad(dir, out var err)) { _directory = dir; break; }
                tried.Add(err ?? dir);
            }
            if (_directory == null) { _status = "FFmpeg not available (" + string.Join("; ", tried) + "); media stays on JPEG"; return; }
            try
            {
                _canEncode = H264Encoder.TryCreate(640, 360, 15, 1_000_000, out var enc, out var why);
                enc?.Dispose();
                _canDecode = H264Decoder.TryCreate(out var dec, out var dwhy);
                dec?.Dispose();
                _status = $"FFmpeg loaded from {_directory}; encoder {(_canEncode ? "ok" : "unavailable: " + why)}; decoder {(_canDecode ? "ok" : "unavailable: " + dwhy)}";
            }
            catch (Exception ex) { _canEncode = _canDecode = false; _status = "FFmpeg probe failed: " + ex.Message; }
        }
    }
}
