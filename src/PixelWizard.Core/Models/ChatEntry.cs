using System;

namespace PixelWizard.Core.Models;

/// <summary>One message in the in-session chat log.</summary>
public record ChatEntry(DateTime Timestamp, bool IsHost, string Text)
{
    public string Display =>
        $"[{Timestamp:HH:mm}] {(IsHost ? "Host" : "Viewer")}: {Text}";
}
