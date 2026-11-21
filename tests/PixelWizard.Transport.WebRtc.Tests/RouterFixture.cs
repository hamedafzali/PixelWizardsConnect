using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace PixelWizard.Transport.WebRtc.Tests;

/// <summary>[Fact] that is reported as SKIPPED (never silently passed) when PIXELWIZARD_ROUTER_EXE is unset.</summary>
public sealed class RouterFactAttribute : FactAttribute
{
    public const string EnvVar = "PIXELWIZARD_ROUTER_EXE";

    public RouterFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"{EnvVar} is not set; these tests need the built router executable.";
    }
}

/// <summary>Starts the Go router from PIXELWIZARD_ROUTER_EXE on a free port, waits for /health, kills it on dispose.</summary>
public sealed class RouterFixture : IDisposable
{
    private readonly Process? _process;

    public int Port { get; }

    public RouterFixture()
    {
        string? exe = Environment.GetEnvironmentVariable(RouterFactAttribute.EnvVar);
        if (string.IsNullOrEmpty(exe)) return; // every test is skipped
        Port = FreePort();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["PORT"] = Port.ToString();
        // Every test pairs two clients through the router; the default of 10 registrations per minute per IP
        // is below the number of tests in this suite.
        psi.Environment["RATE_LIMIT_MAX"] = "1000";
        _process = Process.Start(psi) ?? throw new InvalidOperationException("router did not start");
        // Drain so a full pipe can never block the router.
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        WaitForHealth();
    }

    private void WaitForHealth()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_process!.HasExited) throw new InvalidOperationException($"router exited with {_process.ExitCode}");
            try
            {
                if (http.GetAsync($"http://127.0.0.1:{Port}/health").GetAwaiter().GetResult().IsSuccessStatusCode) return;
            }
            catch (Exception) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException("router /health did not answer within 30 s");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        if (_process == null) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
    }
}

[CollectionDefinition("router")]
public sealed class RouterCollection : ICollectionFixture<RouterFixture> { }
