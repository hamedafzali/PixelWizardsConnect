using System;
using System.Threading.Tasks;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;

namespace PixelWizard.Session;

/// <summary>
/// Viewer-side session: owns the transport to a host. Skeleton only (T9.1) -- see
/// <see cref="HostSession"/> for the rationale and the compile-time no-Dispatcher
/// enforcement; the same applies here. <c>MainViewModel.OnViewerMessage</c>'s dispatch
/// logic moves into this class in T9.2a.
///
/// Not yet constructed by <c>MainViewModel</c> -- today this only forwards the transport's
/// own lifecycle events untouched.
/// </summary>
public sealed class ViewerSession : IDisposable
{
    private readonly ISessionTransport _transport;

    public ViewerSession(ISessionTransport transport)
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

    public Task ConnectAsync(string host, int port, bool useTls = true) => _transport.ConnectAsync(host, port, useTls);
    public Task SendMessageAsync(NetworkMessage message) => _transport.SendMessageAsync(message);
    public void Disconnect() => _transport.Disconnect();

    public void Dispose() => _transport.Dispose();
}
