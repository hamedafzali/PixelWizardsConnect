namespace PixelWizard.Core.Models;

/// <summary>Describes a single display attached to the host machine.</summary>
public record MonitorInfo(int Index, string Name, int Width, int Height, bool IsPrimary);
