using System.Threading.Channels;
using PixelWizard.AvaloniaClient.Services;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;

namespace PixelWizard.AvaloniaClient.Tests;

/// <summary>In-memory transport pair: what one side sends the other receives, in order. Connected is raised on Establish.</summary>
internal sealed class MemoryTransport : ISessionTransport
{
    private readonly Channel<NetworkMessage> _inbox = Channel.CreateUnbounded<NetworkMessage>();
    private MemoryTransport? _peer;
    private int _down;
    private volatile bool _open;

    public static (MemoryTransport A, MemoryTransport B) CreatePair()
    {
        var a = new MemoryTransport(); var b = new MemoryTransport();
        a._peer = b; b._peer = a;
        return (a, b);
    }

    public bool IsConnected => _open && Volatile.Read(ref _down) == 0;
    public int DisconnectCalls;
    public event Action<NetworkMessage>? MessageReceived;
    public event Action? Connected;
    public event Action? Disconnected;
#pragma warning disable CS0067
    public event Action<Exception>? Error;
    public event Action<Exception>? HandlerError;
#pragma warning restore CS0067
    public event Action<int>? BytesReceived;
    public event Action<int>? BytesSent;

    public Task EstablishAsync()
    {
        _open = true;
        _ = Task.Run(async () =>
        {
            await foreach (var m in _inbox.Reader.ReadAllAsync())
            {
                BytesReceived?.Invoke(m.Data.Length);
                try { MessageReceived?.Invoke(m); } catch { }
            }
        });
        Connected?.Invoke();
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(NetworkMessage message)
    {
        if (!IsConnected || _peer == null) return Task.CompletedTask;
        BytesSent?.Invoke(message.Data.Length);
        _peer._inbox.Writer.TryWrite(message);
        return Task.CompletedTask;
    }

    public void Disconnect()
    {
        Interlocked.Increment(ref DisconnectCalls);
        Teardown();
        _peer?.Teardown();
    }

    private void Teardown()
    {
        if (Interlocked.Exchange(ref _down, 1) != 0) return;
        _open = false;
        _inbox.Writer.TryComplete();
        Disconnected?.Invoke();
    }

    public void Dispose() => Teardown();
}

internal sealed class NoSignaling : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Fake IRelayConnector. Host side: each RegisterHostAsync hands out a new code ("ROOM01", "ROOM02", ...)
/// and a registration that waits until <see cref="Join"/> is called, then yields a host-side transport
/// whose peer is returned to the test (to build a ViewerSession over). Viewer side: JoinAsync yields
/// the viewer end of the pending pair.
/// </summary>
internal sealed class FakeRelayConnector : IRelayConnector
{
    public int RegisterCalls;
    public Exception? RegisterFails;
    public readonly List<FakeRegistration> Registrations = new();
    public string? JoinedCode;
    public Exception? JoinFails { get; set; }
    public Exception? EstablishFails { get; set; }
    /// <summary>Called in JoinAsync with the host-side transport, before it is established, so a test can build a HostSession over it.</summary>
    public Action<MemoryTransport>? OnHostTransport { get; set; }

    public Task<IRelayHostRegistration> RegisterHostAsync(string routerHost, int routerPort, CancellationToken ct)
    {
        if (RegisterFails != null) return Task.FromException<IRelayHostRegistration>(RegisterFails);
        int n = Interlocked.Increment(ref RegisterCalls);
        var reg = new FakeRegistration($"ROOM{n:00}");
        lock (Registrations) Registrations.Add(reg);
        return Task.FromResult<IRelayHostRegistration>(reg);
    }

    /// <summary>A viewer joins the latest registration; returns the viewer-side transport.</summary>
    public MemoryTransport Join(FakeRegistration reg)
    {
        var (host, viewer) = MemoryTransport.CreatePair();
        reg.Pending.TrySetResult(new RelayLink(host, _ => host.EstablishAsync(), new NoSignaling()));
        // The viewer end is established by whoever drives the viewer session.
        return viewer;
    }

    public Task<RelayLink> JoinAsync(string routerHost, int routerPort, string code, CancellationToken ct)
    {
        JoinedCode = code;
        if (JoinFails != null) return Task.FromException<RelayLink>(JoinFails);
        var (host, viewer) = MemoryTransport.CreatePair();
        OnHostTransport?.Invoke(host);
        // The host end opens together with the viewer end.
        return Task.FromResult(new RelayLink(viewer, async _ =>
        {
            if (EstablishFails != null) throw EstablishFails;
            await host.EstablishAsync(); await viewer.EstablishAsync();
        }, new NoSignaling()));
    }
}

internal sealed class FakeRegistration : IRelayHostRegistration
{
    public readonly TaskCompletionSource<RelayLink> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed;
    public FakeRegistration(string code) => Code = code;
    public string Code { get; }
    public Task<RelayLink> WaitForViewerAsync(CancellationToken ct) => Pending.Task.WaitAsync(ct);
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
