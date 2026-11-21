using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace PixelWizard.AvaloniaClient.Tests;

/// <summary>[Fact] reported as SKIPPED (never silently passed) when PIXELWIZARD_ROUTER_EXE is unset.</summary>
public sealed class RouterFactAttribute : FactAttribute
{
    public const string EnvVar = "PIXELWIZARD_ROUTER_EXE";
    public RouterFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"{EnvVar} is not set; this test needs the built router executable.";
    }
}

/// <summary>Starts the Go router from PIXELWIZARD_ROUTER_EXE on a free port; kills it on dispose.</summary>
internal sealed class RouterProcess : IDisposable
{
    private readonly Process _process;
    public int Port { get; }

    public RouterProcess()
    {
        string exe = Environment.GetEnvironmentVariable(RouterFactAttribute.EnvVar)!;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["PORT"] = Port.ToString();
        psi.Environment["RATE_LIMIT_MAX"] = "1000";
        _process = Process.Start(psi)!;
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            if (_process.HasExited) throw new InvalidOperationException("router exited");
            try { if (http.GetAsync($"http://127.0.0.1:{Port}/health").GetAwaiter().GetResult().IsSuccessStatusCode) return; } catch { }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("router /health did not answer");
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
    }
}
