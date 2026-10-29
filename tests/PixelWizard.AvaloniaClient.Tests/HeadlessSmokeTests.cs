using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace PixelWizard.AvaloniaClient.Tests;

public class HeadlessSmokeTests
{
    [AvaloniaFact]
    public void RunsOnTheUiThread_WithTheRealApp()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess());
        Assert.IsType<App>(Application.Current);
    }
}
