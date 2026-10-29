using PixelWizard.AvaloniaClient.Services;
using Xunit;

namespace PixelWizard.AvaloniaClient.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Save_WritesToTheLoadedPath_AndRoundTrips()
    {
        string dir  = Path.Combine(Path.GetTempPath(), "pw-settings-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "settings.json");
        try
        {
            var settings = AppSettings.Load(file);
            Assert.Empty(settings.RecentHosts);
            AppSettings.PushRecent(settings.RecentHosts, "10.0.0.5:8888");
            settings.LastRouterAddress = "router:9000";
            settings.Save();

            Assert.True(File.Exists(file));
            var reloaded = AppSettings.Load(file);
            Assert.Equal(new[] { "10.0.0.5:8888" }, reloaded.RecentHosts);
            Assert.Equal("router:9000", reloaded.LastRouterAddress);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_CorruptFile_StartsFresh_AndStillSavesThere()
    {
        string file = Path.Combine(Path.GetTempPath(), "pw-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, "{ not json");
            var settings = AppSettings.Load(file);
            Assert.Empty(settings.RecentHosts);
            settings.OnboardingSeen = true;
            settings.Save();
            Assert.True(AppSettings.Load(file).OnboardingSeen);
        }
        finally { File.Delete(file); }
    }
}
