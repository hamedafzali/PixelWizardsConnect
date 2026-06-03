using System;

namespace PixelWizard.Core.Models
{
    public sealed record StreamingSettings(string Name, int Fps, long JpegQuality, TimeSpan FullRefreshInterval)
    {
        public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(Math.Max(33, 1000 / Fps));

        public static StreamingSettings LowBandwidth { get; } = new("Low bandwidth", 5, 45, TimeSpan.FromSeconds(8));
        public static StreamingSettings Balanced { get; } = new("Balanced", 12, 65, TimeSpan.FromSeconds(10));
        public static StreamingSettings HighQuality { get; } = new("High quality", 8, 85, TimeSpan.FromSeconds(12));
        public static StreamingSettings Fast { get; } = new("Fast", 20, 55, TimeSpan.FromSeconds(6));
        public static StreamingSettings Localhost { get; } = new("Localhost", 24, 90, TimeSpan.FromSeconds(4));

        public static StreamingSettings FromPresetIndex(int index) => index switch
        {
            0 => LowBandwidth,
            2 => HighQuality,
            3 => Fast,
            4 => Localhost,
            _ => Balanced
        };
    }
}
