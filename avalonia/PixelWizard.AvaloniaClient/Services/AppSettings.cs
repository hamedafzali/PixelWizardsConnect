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

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PixelWizardConnect");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt or missing — start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
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
