using System;
using System.Threading.Tasks;
using PixelWizard.Protocol;

namespace PixelWizard.Core.Interfaces
{
    public interface ISessionTransport : IDisposable
    {
        bool IsConnected { get; }

        event Action<NetworkMessage>? MessageReceived;
        event Action? Connected;
        event Action? Disconnected;

        /// <summary>
        /// The transport itself failed (framing, socket, deserialization) — stream sync is
        /// lost or the connection is gone. The session is disconnected by the time this fires.
        /// </summary>
        event Action<Exception>? Error;

        /// <summary>
        /// A <see cref="MessageReceived"/> subscriber threw while handling an otherwise
        /// well-formed message. The connection is unaffected and the receive loop continues —
        /// see <see cref="PixelWizard.Transport.Tcp.RepeatedHandlerFailureException"/> for what
        /// happens if a handler keeps failing on every message instead of just this one.
        /// </summary>
        event Action<Exception>? HandlerError;

        event Action<int>? BytesReceived;
        event Action<int>? BytesSent;

        Task ConnectAsync(string host, int port, bool useTls = true);
        Task StartServerAsync(int port, bool useTls = true);
        Task SendMessageAsync(NetworkMessage message);
        void Disconnect();
    }
}
