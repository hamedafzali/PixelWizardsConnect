using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PixelWizard.AvaloniaClient.Services;

/// <summary>Small JSON settings store in the user's app-data folder.</summary>
public sealed class AppSettings
{
    public bool OnboardingSeen { get; set; }
    public List<string> RecentHosts   { get; set; } = new();
    public List<string> RecentRouters { get; set; } = new();
    public string LastRouterAddress   { get; set; } = "";

    internal static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PixelWizardConnect");
    private static readonly string DefaultFilePath = Path.Combine(Dir, "settings.json");

    private string _filePath = DefaultFilePath;

    public static AppSettings Load() => Load(DefaultFilePath);

    /// <summary>Loads from, and later saves to, <paramref name="filePath"/> (tests use a temp file).</summary>
    public static AppSettings Load(string filePath)
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(filePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch { settings = new AppSettings(); /* corrupt or missing — start fresh */ }
        settings._filePath = filePath;
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    /// <summary>Adds value to the front of a recents list, de-duped, capped at 8.</summary>
    public static void PushRecent(List<string> list, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        list.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, value);
        while (list.Count > 8) list.RemoveAt(list.Count - 1);
    }
}
