using System;
using System.Threading.Tasks;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;

namespace PixelWizard.Tests.Session;

/// <summary>
/// In-memory <see cref="ISessionTransport"/> double: no sockets, just lets a test fire each
/// transport event directly and record calls made through the interface's methods. Used to
/// prove HostSession/ViewerSession forward transport events untouched (T9.1's behavioral
/// gate) without needing a real socket pair.
/// </summary>
public sealed class FakeSessionTransport : ISessionTransport
{
    public bool IsConnected { get; set; }

    public event Action<NetworkMessage>? MessageReceived;
    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<Exception>? Error;
    public event Action<Exception>? HandlerError;
    public event Action<int>? BytesReceived;
    public event Action<int>? BytesSent;

    public int DisconnectCallCount { get; private set; }
    public bool Disposed { get; private set; }
    public NetworkMessage? LastSentMessage { get; private set; }
    public (string Host, int Port, bool UseTls)? LastConnectArgs { get; private set; }
    public (int Port, bool UseTls)? LastStartServerArgs { get; private set; }

    public void RaiseMessageReceived(NetworkMessage msg) => MessageReceived?.Invoke(msg);
    public void RaiseConnected() => Connected?.Invoke();
    public void RaiseDisconnected() => Disconnected?.Invoke();
    public void RaiseError(Exception ex) => Error?.Invoke(ex);
    public void RaiseHandlerError(Exception ex) => HandlerError?.Invoke(ex);
    public void RaiseBytesReceived(int n) => BytesReceived?.Invoke(n);
    public void RaiseBytesSent(int n) => BytesSent?.Invoke(n);

    public Task ConnectAsync(string host, int port, bool useTls = true)
    {
        LastConnectArgs = (host, port, useTls);
        return Task.CompletedTask;
    }

    public Task StartServerAsync(int port, bool useTls = true)
    {
        LastStartServerArgs = (port, useTls);
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(NetworkMessage message)
    {
        LastSentMessage = message;
        return Task.CompletedTask;
    }

    public void Disconnect() => DisconnectCallCount++;

    public void Dispose() => Disposed = true;
}
