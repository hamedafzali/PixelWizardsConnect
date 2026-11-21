using FFmpeg.AutoGen;

namespace PixelWizard.Media.H264;

/// <summary>Points FFmpeg.AutoGen at the vendored shared libraries, once per process.</summary>
public static class FfmpegLoader
{
    private static readonly object Gate = new();
    private static bool _attempted;
    private static string? _error;
    private static string? _dir;

    /// <summary>True when the libraries in <paramref name="directory"/> loaded. Later calls return the first result.</summary>
    public static bool TryLoad(string directory, out string? error)
    {
        lock (Gate)
        {
            if (!_attempted)
            {
                _attempted = true;
                _dir = directory;
                try
                {
                    ffmpeg.RootPath = directory;
                    DynamicallyLoadedBindings.Initialize();
                    _ = ffmpeg.avcodec_version(); // forces the avcodec/avutil binding to resolve
                }
                catch (Exception ex)
                {
                    _error = $"FFmpeg libraries in '{directory}' failed to load: {ex.Message}";
                }
            }
            else if (_dir != directory && _error == null)
            {
                // The bindings are process-wide: a different directory cannot be loaded afterwards.
            }
            error = _error;
            return _error == null;
        }
    }

    public static bool IsLoaded { get { lock (Gate) return _attempted && _error == null; } }
}
