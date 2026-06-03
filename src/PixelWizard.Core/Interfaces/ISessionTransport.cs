using System;
using System.Threading.Tasks;
using PixelWizard.Core.Protocol;

namespace PixelWizard.Core.Interfaces
{
    public interface ISessionTransport : IDisposable
    {
        bool IsConnected { get; }

        event Action<NetworkMessage>? MessageReceived;
        event Action? Connected;
        event Action? Disconnected;
        event Action<Exception>? Error;
        event Action<int>? BytesReceived;
        event Action<int>? BytesSent;

        Task ConnectAsync(string host, int port, bool useTls = true);
        Task StartServerAsync(int port, bool useTls = true);
        Task SendMessageAsync(NetworkMessage message);
        void Disconnect();
    }
}
