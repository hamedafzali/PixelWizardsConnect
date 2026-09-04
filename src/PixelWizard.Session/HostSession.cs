using System;
using System.Threading.Tasks;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;

namespace PixelWizard.Session;

/// <summary>
/// Host-side session: owns the transport a viewer connects to. Skeleton only (T9.1) --
/// this class does not yet do anything <c>MainViewModel.OnHostMessage</c>/<c>HandleHello</c>/
/// <c>HandleHandshake</c> don't already do; it exists so that move (T9.2b) lands as
/// "delete the inline copy, the real logic already lives here and is proven," not as a
/// single large commit that introduces the shape and the behavior at once.
///
/// Not yet constructed by <c>MainViewModel</c> -- that wiring happens in T9.2b, when there
/// is dispatch logic here for it to actually replace. Today this only forwards the
/// transport's own lifecycle events untouched, to establish the events-outward shape the
/// phase gate requires: no member of this class, or of anything it calls, may reference
/// Avalonia.Threading.Dispatcher (the project has no reference to Avalonia, so this is
/// enforced at compile time, not by convention).
/// </summary>
public sealed class HostSession : IDisposable
{
    private readonly ISessionTransport _transport;

    public HostSession(ISessionTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transport.Connected += () => Connected?.Invoke();
        _transport.Disconnected += () => Disconnected?.Invoke();
        _transport.Error += ex => Error?.Invoke(ex);
        _transport.HandlerError += ex => HandlerError?.Invoke(ex);
        _transport.BytesReceived += n => BytesReceived?.Invoke(n);
        _transport.BytesSent += n => BytesSent?.Invoke(n);
    }

    public bool IsConnected => _transport.IsConnected;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<Exception>? Error;
    public event Action<Exception>? HandlerError;
    public event Action<int>? BytesReceived;
    public event Action<int>? BytesSent;

    public Task StartServerAsync(int port, bool useTls = true) => _transport.StartServerAsync(port, useTls);
    public Task SendMessageAsync(NetworkMessage message) => _transport.SendMessageAsync(message);
    public void Disconnect() => _transport.Disconnect();

    public void Dispose() => _transport.Dispose();
}
