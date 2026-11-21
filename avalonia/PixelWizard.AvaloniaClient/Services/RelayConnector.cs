using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using PixelWizard.Core.Interfaces;
using PixelWizard.Signaling;
using PixelWizard.Transport.WebRtc;

namespace PixelWizard.AvaloniaClient.Services;

/// <summary>
/// A transport that exists but is not established yet, so the caller can wrap it in a session (and
/// subscribe to it) first: the viewer session sends Hello from the transport's Connected event.
/// <see cref="EstablishAsync"/> runs the WebRTC negotiation and completes once the data channels are
/// open. <see cref="Signaling"/> is owned by the transport once established; disposing it again is
/// harmless, and a caller that never establishes must dispose it.
/// </summary>
public sealed record RelayLink(ISessionTransport Transport, Func<CancellationToken, Task> EstablishAsync, IAsyncDisposable Signaling);

/// <summary>A host registered with the router and waiting for a viewer.</summary>
public interface IRelayHostRegistration : IAsyncDisposable
{
    /// <summary>The room code to give the viewer.</summary>
    string Code { get; }

    /// <summary>
    /// Waits for a viewer to join. If the signaling socket drops first, the registration is resumed
    /// within the router's window (docs/SIGNALING.md, Reconnecting), keeping the same code.
    /// </summary>
    Task<RelayLink> WaitForViewerAsync(CancellationToken ct);
}

/// <summary>How the app pairs through the router (<c>/signal</c>) and gets a transport to the peer (D7).</summary>
public interface IRelayConnector
{
    /// <summary>Host: connects to the router's <c>/signal</c> and registers. <paramref name="routerHost"/> and
    /// <paramref name="routerPort"/> come from <see cref="RouterEndpoint.Parse"/>; an https:// host gives wss.</summary>
    Task<IRelayHostRegistration> RegisterHostAsync(string routerHost, int routerPort, CancellationToken ct);

    /// <summary>Viewer: joins the room for <paramref name="code"/>.</summary>
    Task<RelayLink> JoinAsync(string routerHost, int routerPort, string code, CancellationToken ct);
}

/// <summary>The real connector: <see cref="SignalingClient"/> plus <see cref="WebRtcTransport"/>.</summary>
public sealed class WebRtcRelayConnector : IRelayConnector
{
    public async Task<IRelayHostRegistration> RegisterHostAsync(string routerHost, int routerPort, CancellationToken ct)
    {
        var signaling = new SignalingClient(routerHost, routerPort);
        var registration = await signaling.RegisterAsync(ct).ConfigureAwait(false);
        return new HostRegistration(routerHost, routerPort, signaling, registration);
    }

    public async Task<RelayLink> JoinAsync(string routerHost, int routerPort, string code, CancellationToken ct)
    {
        var signaling = new SignalingClient(routerHost, routerPort);
        var joined = await signaling.JoinAsync(code, ct).ConfigureAwait(false);
        var transport = new WebRtcTransport();
        return new RelayLink(transport,
            c => transport.EstablishAsync(signaling, joined.Turn, WebRtcRole.Viewer, ct: c), signaling);
    }

    internal sealed class HostRegistration : IRelayHostRegistration
    {
        // The router keeps an unpaired registration 60 s after its socket drops. Backoff 2, 4, 8, 16 s
        // fits a handful of attempts in that window, well under the router's resume rate limit.
        private static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(55);

        private readonly string _host;
        private readonly int _port;
        private readonly string _resumeToken;
        private SignalingClient _signaling;

        public HostRegistration(string host, int port, SignalingClient signaling, SignalingRegistration registration)
        {
            _host = host; _port = port; _signaling = signaling;
            Code = registration.Code;
            _resumeToken = registration.ResumeToken;
        }

        public string Code { get; }

        /// <summary>Test hook: closes the current signaling socket the way a network drop would.</summary>
        internal Task DropSocketForTest() => _signaling.DisposeAsync().AsTask();

        public async Task<RelayLink> WaitForViewerAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + ResumeWindow;
            var delay = TimeSpan.FromSeconds(1);
            PeerJoinedMessage peer;
            while (true)
            {
                try { peer = await _signaling.WaitForPeerAsync(ct).ConfigureAwait(false); break; }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsDroppedSocket(ex))
                {
                    await ResumeAsync(deadline, delay, ct).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 16));
                }
            }
            var signaling = _signaling;
            var transport = new WebRtcTransport();
            return new RelayLink(transport,
                c => transport.EstablishAsync(signaling, peer.Turn, WebRtcRole.Host, ct: c), signaling);
        }

        // A closed or broken socket. A router error (SignalingErrorException) or a malformed frame is not a drop.
        private static bool IsDroppedSocket(Exception ex) =>
            ex is InvalidOperationException or WebSocketException or IOException;

        private async Task ResumeAsync(DateTime deadline, TimeSpan delay, CancellationToken ct)
        {
            await _signaling.DisposeAsync().ConfigureAwait(false);
            while (true)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                var next = new SignalingClient(_host, _port);
                try
                {
                    await next.ResumeAsync(_resumeToken, ct).ConfigureAwait(false);
                    _signaling = next;
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsDroppedSocket(ex) && DateTime.UtcNow + delay < deadline)
                {
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 16));
                }
            }
        }

        public ValueTask DisposeAsync() => _signaling.DisposeAsync();
    }
}
