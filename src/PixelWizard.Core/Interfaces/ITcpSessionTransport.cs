using System.Threading.Tasks;

namespace PixelWizard.Core.Interfaces
{
    /// <summary>
    /// A session transport established by host:port (TCP). Establishment is deliberately not
    /// part of <see cref="ISessionTransport"/>: a transport built from a signaling client
    /// (WebRTC, Phase 4) is created already connecting and has no host:port to connect to.
    /// </summary>
    public interface ITcpSessionTransport : ISessionTransport
    {
        Task ConnectAsync(string host, int port, bool useTls = true);
        /// <summary>
        /// Listens on <paramref name="port"/> and completes once one viewer has connected.
        /// A port that can't be bound faults the returned task before the method returns,
        /// raises neither <see cref="Error"/> nor <see cref="Disconnected"/>, and leaves the
        /// transport holding nothing, so it need not be disposed. Failures
        /// after that are reported through <see cref="Error"/>, as for a connected session.
        /// </summary>
        Task StartServerAsync(int port, bool useTls = true);
    }
}
