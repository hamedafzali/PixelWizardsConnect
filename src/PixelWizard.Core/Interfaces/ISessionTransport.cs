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
        /// <summary>
        /// Listens on <paramref name="port"/> and completes once one viewer has connected.
        /// A port that can't be bound faults the returned task before the method returns,
        /// raises neither <see cref="Error"/> nor <see cref="Disconnected"/>, and leaves the
        /// transport holding nothing, so it need not be disposed. Failures
        /// after that are reported through <see cref="Error"/>, as for a connected session.
        /// </summary>
        Task StartServerAsync(int port, bool useTls = true);
        Task SendMessageAsync(NetworkMessage message);
        void Disconnect();
    }
}
