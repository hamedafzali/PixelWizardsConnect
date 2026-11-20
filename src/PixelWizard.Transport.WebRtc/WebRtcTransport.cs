using System.Threading.Channels;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;
using PixelWizard.Signaling;
using SIPSorcery.Net;

namespace PixelWizard.Transport.WebRtc
{
    public enum WebRtcRole
    {
        /// <summary>Offerer: creates the <c>control</c> data channel. Has already done Register + WaitForPeerAsync.</summary>
        Host,
        /// <summary>Answerer: receives the <c>control</c> data channel. Has already done JoinAsync.</summary>
        Viewer,
    }

    /// <summary>
    /// <see cref="ISessionTransport"/> over one reliable, ordered RTCDataChannel named <c>control</c>.
    /// Establishment is not on the interface: call <see cref="EstablishAsync"/> with a
    /// <see cref="SignalingClient"/> that has already registered (and seen the peer) or joined.
    ///
    /// Framing: each <see cref="NetworkMessage.Serialize"/> result is one logical message. A data channel
    /// message is a 1-byte header (0 = last chunk, 1 = more follow) plus at most
    /// <see cref="ChunkSize"/> payload bytes, so a 1 MB frame never depends on the SCTP message-size limit.
    /// The channel is ordered, so reassembly is concatenation.
    ///
    /// Ownership: the transport takes over the SignalingClient (it keeps reading it for late candidates,
    /// bye and peer-left) and disposes it when the transport goes down, also when EstablishAsync fails.
    /// </summary>
    public sealed class WebRtcTransport : ISessionTransport
    {
        public const string ControlChannelLabel = "control";
        internal const int ChunkSize = 16 * 1024;
        internal const int MaxConsecutiveHandlerFailures = 5;
        private const int MaxMessageSize = 64 * 1024 * 1024; // sanity cap on a reassembled message

        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly Channel<Func<Task>> _sendQueue = Channel.CreateUnbounded<Func<Task>>();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly List<RTCIceCandidateInit> _pendingRemote = new();
        private readonly List<RTCIceCandidateInit> _pendingLocal = new();
        private readonly List<RTCIceCandidateInit> _appliedRemote = new();
        private readonly MemoryStream _assembly = new();

        private RTCPeerConnection? _pc;
        private RTCDataChannel? _dc;
        private SignalingClient? _signaling;
        private TaskCompletionSource? _established;
        private bool _remoteSet;
        private bool _sdpSent;
        private bool _establishStarted;
        private volatile bool _open;
        private int _down;
        private string? _selectedPair;

        /// <summary>True when the SCTP sender tuning (see <see cref="SctpTuning"/>) was applied after the channel opened.</summary>
        internal bool SctpTuned { get; private set; }

        public event Action<NetworkMessage>? MessageReceived;
        public event Action? Connected;
        public event Action? Disconnected;
        public event Action<Exception>? Error;
        public event Action<Exception>? HandlerError;
        public event Action<int>? BytesReceived;
        public event Action<int>? BytesSent;

        public bool IsConnected => _open && Volatile.Read(ref _down) == 0;

        /// <summary>How long <see cref="EstablishAsync"/> waits for the channel to open. Default 60 s.</summary>
        public TimeSpan EstablishTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Nominated candidate pair as "local/remote" candidate types (e.g. "host/host", "relay/srflx"),
        /// read from the RTP channel's NominatedEntry once ICE is connected. Null before that;
        /// "unknown/unknown" if ICE connected but no pair could be read.
        /// </summary>
        public string? SelectedPairType => _selectedPair ?? (_open ? (_selectedPair = ReadPair()) : null);

        /// <summary>
        /// Builds the peer connection and completes when the control channel is open. Faults, and cleans up
        /// (raising Disconnected once), on ICE failure, signaling error, peer-left/bye, timeout or cancellation.
        /// </summary>
        /// <param name="turn">From peer-joined (host) or joined (viewer); null when the router issues none.</param>
        /// <param name="forceRelay">Sets iceTransportPolicy to relay.</param>
        public async Task EstablishAsync(SignalingClient signaling, TurnInfo? turn, WebRtcRole role,
            bool forceRelay = false, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(signaling);
            lock (_gate)
            {
                if (_establishStarted) throw new InvalidOperationException("EstablishAsync was already called.");
                _establishStarted = true;
            }
            _signaling = signaling;
            _established = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            linked.CancelAfter(EstablishTimeout);
            using var reg = linked.Token.Register(() => _established.TrySetCanceled(linked.Token));
            try
            {
                var servers = new List<RTCIceServer>();
                if (turn != null)
                    servers.Add(new RTCIceServer { urls = string.Join(",", turn.Urls), username = turn.Username, credential = turn.Credential });
                var pc = new RTCPeerConnection(new RTCConfiguration
                {
                    iceServers = servers,
                    iceTransportPolicy = forceRelay ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all,
                });
                _pc = pc;
                if (Volatile.Read(ref _down) != 0) throw new OperationCanceledException();

                // Every signaling send goes through one ordered queue: SDP first, then candidates in the
                // order they fire, then end-of-candidates. A candidate can never overtake the SDP.
                _ = Task.Run(SendLoopAsync);

                pc.onicecandidate += c =>
                {
                    if (c == null) return;
                    var init = new RTCIceCandidateInit { candidate = c.ToString(), sdpMid = c.sdpMid ?? "0", sdpMLineIndex = c.sdpMLineIndex };
                    lock (_gate)
                    {
                        if (!_sdpSent) { _pendingLocal.Add(init); return; }
                    }
                    QueueCandidate(init);
                };
                pc.onicegatheringstatechange += s =>
                {
                    if (s == RTCIceGatheringState.complete)
                        Enqueue(() => signaling.SendEndOfCandidatesAsync(_lifetime.Token));
                };
                pc.oniceconnectionstatechange += s =>
                {
                    if (s == RTCIceConnectionState.connected) _selectedPair = ReadPair();
                    if (s == RTCIceConnectionState.failed || s == RTCIceConnectionState.closed)
                        Fail(new IOException($"ICE {s}"));
                };
                pc.onconnectionstatechange += s =>
                {
                    if (s == RTCPeerConnectionState.failed || s == RTCPeerConnectionState.closed)
                        Fail(new IOException($"Peer connection {s}"));
                };

                if (role == WebRtcRole.Host)
                {
                    Wire(await pc.createDataChannel(ControlChannelLabel).ConfigureAwait(false));
                    var offer = pc.createOffer();
                    await pc.setLocalDescription(offer).ConfigureAwait(false);
                    await signaling.SendOfferAsync(offer.sdp, linked.Token).ConfigureAwait(false);
                    AfterSdpSent();
                }
                else
                {
                    pc.ondatachannel += dc =>
                    {
                        if (dc.label == ControlChannelLabel) Wire(dc);
                    };
                }

                _ = Task.Run(() => SignalingLoopAsync(role));
                _ = Task.Run(ReceiveLoopAsync);

                await _established.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                bool callerCancelled = ct.IsCancellationRequested;
                bool timedOut = ex is OperationCanceledException && !callerCancelled && !_lifetime.IsCancellationRequested
                    && Volatile.Read(ref _down) == 0;
                Teardown(sendBye: false);
                if (timedOut)
                    throw new TimeoutException($"WebRTC control channel did not open within {EstablishTimeout.TotalSeconds:0.#} s.");
                throw;
            }
        }

        public async Task SendMessageAsync(NetworkMessage message)
        {
            if (!IsConnected || _dc == null) return;
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsConnected) return;
                byte[] data = message.Serialize();
                int sent = 0, offset = 0;
                do
                {
                    int n = Math.Min(ChunkSize, data.Length - offset);
                    bool last = offset + n >= data.Length;
                    var chunk = new byte[1 + n];
                    chunk[0] = last ? (byte)0 : (byte)1;
                    Buffer.BlockCopy(data, offset, chunk, 1, n);
                    _dc.send(chunk);
                    sent += chunk.Length;
                    offset += n;
                } while (offset < data.Length);
                BytesSent?.Invoke(sent);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>Local close: tells the peer (bye over signaling), closes the connection, raises Disconnected once.</summary>
        public void Disconnect() => Teardown(sendBye: true);

        public void Dispose()
        {
            Teardown(sendBye: false);
        }

        // ---- internals ----

        private void Wire(RTCDataChannel dc)
        {
            _dc = dc;
            dc.onopen += OnOpen;
            dc.onclose += () => Fail(new IOException("Control channel closed"), raiseError: false);
            dc.onerror += e => Fail(new IOException($"Control channel error: {e}"));
            dc.onmessage += (_, _, data) => OnChannelData(data);
            if (dc.readyState == RTCDataChannelState.open) OnOpen();
        }

        private void OnOpen()
        {
            lock (_gate)
            {
                if (_open || Volatile.Read(ref _down) != 0) return;
                _open = true;
            }
            _selectedPair ??= ReadPair();
            if (_pc != null) SctpTuned = SctpTuning.TryApply(_pc);
            try { Connected?.Invoke(); } catch (Exception ex) { HandlerError?.Invoke(ex); }
            _established?.TrySetResult();
        }

        // Runs on the SCTP thread: reassemble only, then hand the whole message to the ordered receive loop.
        private void OnChannelData(byte[] data)
        {
            if (data.Length < 1) return;
            lock (_assembly)
            {
                if (_assembly.Length + data.Length - 1 > MaxMessageSize)
                {
                    _assembly.SetLength(0);
                    Fail(new InvalidDataException("Reassembled message exceeds the size limit."));
                    return;
                }
                _assembly.Write(data, 1, data.Length - 1);
                BytesReceived?.Invoke(data.Length);
                if (data[0] != 0) return;
                var whole = _assembly.ToArray();
                _assembly.SetLength(0);
                _inbound.Writer.TryWrite(whole);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            int failures = 0;
            try
            {
                await foreach (var buf in _inbound.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    NetworkMessage message;
                    try { message = NetworkMessage.Deserialize(buf); }
                    catch (Exception ex) { Fail(ex); return; }
                    try
                    {
                        MessageReceived?.Invoke(message);
                        failures = 0;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        HandlerError?.Invoke(ex);
                        if (failures >= MaxConsecutiveHandlerFailures)
                        {
                            Fail(new InvalidOperationException(
                                $"A message handler failed on {failures} consecutive messages; treating the session as stuck and disconnecting.", ex));
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task SignalingLoopAsync(WebRtcRole role)
        {
            var pc = _pc!;
            var signaling = _signaling!;
            try
            {
                await foreach (var msg in signaling.ReadMessagesAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    switch (msg)
                    {
                        case OfferMessage o when role == WebRtcRole.Viewer:
                            var res = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = o.Sdp });
                            if (res != SetDescriptionResultEnum.OK) throw new InvalidOperationException($"setRemoteDescription: {res}");
                            FlushRemote();
                            var answer = pc.createAnswer();
                            await pc.setLocalDescription(answer).ConfigureAwait(false);
                            await signaling.SendAnswerAsync(answer.sdp, _lifetime.Token).ConfigureAwait(false);
                            AfterSdpSent();
                            break;
                        case AnswerMessage a when role == WebRtcRole.Host:
                            var ares = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = a.Sdp });
                            if (ares != SetDescriptionResultEnum.OK) throw new InvalidOperationException($"setRemoteDescription: {ares}");
                            FlushRemote();
                            break;
                        case CandidateMessage c:
                            AddRemoteCandidate(new RTCIceCandidateInit
                            {
                                candidate = c.Candidate, sdpMid = c.SdpMid, sdpMLineIndex = (ushort)(c.SdpMLineIndex ?? 0),
                            });
                            break;
                        case ByeMessage:
                        case PeerLeftMessage:
                            Fail(new IOException("The peer left."), raiseError: false);
                            return;
                    }
                }
                // Socket closed cleanly without peer-left. Before the channel is open that is a failed
                // establishment; afterwards the data channel may still be fine, so nothing to do.
                if (!_open) Fail(new IOException("Signaling ended before the control channel opened."));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex); }
        }

        private void AddRemoteCandidate(RTCIceCandidateInit init)
        {
            bool ready;
            lock (_gate)
            {
                ready = _remoteSet;
                if (!ready) _pendingRemote.Add(init);
            }
            if (ready) ApplyRemote(init);
        }

        private void FlushRemote()
        {
            List<RTCIceCandidateInit> batch;
            lock (_gate) { _remoteSet = true; batch = new(_pendingRemote); _pendingRemote.Clear(); }
            foreach (var c in batch) ApplyRemote(c);
        }

        // A repeated candidate must neither throw nor disturb the connection.
        private void ApplyRemote(RTCIceCandidateInit c)
        {
            lock (_gate) _appliedRemote.Add(c);
            try { _pc?.addIceCandidate(c); }
            catch (Exception) { /* tolerated: bad or duplicate candidate */ }
        }

        /// <summary>Test hook: feeds a remote candidate through the same path as one arriving over signaling.</summary>
        internal void InjectRemoteCandidate(RTCIceCandidateInit c) => AddRemoteCandidate(c);
        internal IReadOnlyList<RTCIceCandidateInit> AppliedRemoteCandidates { get { lock (_gate) return _appliedRemote.ToList(); } }

        private void AfterSdpSent()
        {
            lock (_gate)
            {
                _sdpSent = true;
                foreach (var c in _pendingLocal) QueueCandidate(c);
                _pendingLocal.Clear();
            }
        }

        private void QueueCandidate(RTCIceCandidateInit c) =>
            Enqueue(() => _signaling!.SendCandidateAsync(c.candidate, c.sdpMid, c.sdpMLineIndex, _lifetime.Token));

        private void Enqueue(Func<Task> send) => _sendQueue.Writer.TryWrite(send);

        private async Task SendLoopAsync()
        {
            try
            {
                await foreach (var send in _sendQueue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    try { await send().ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Fail(ex); }
                }
            }
            catch (Exception) { }
        }

        private string ReadPair()
        {
            var entry = _pc?.GetRtpChannel()?.NominatedEntry;
            if (entry == null) return "unknown/unknown";
            return $"{entry.LocalCandidate.type}/{entry.RemoteCandidate.type}";
        }

        // A fault: fails a pending establishment, otherwise reports Error (unless it is just the peer
        // going away), then tears down.
        private void Fail(Exception ex, bool raiseError = true)
        {
            if (Volatile.Read(ref _down) != 0) return;
            if (_established is { Task.IsCompleted: false } est) est.TrySetException(ex);
            else if (raiseError && _open) { try { Error?.Invoke(ex); } catch { } }
            Teardown(sendBye: false);
        }

        private void Teardown(bool sendBye)
        {
            if (Interlocked.Exchange(ref _down, 1) != 0) return;
            _open = false;
            var signaling = _signaling;
            _established?.TrySetCanceled();
            _sendQueue.Writer.TryComplete();
            _inbound.Writer.TryComplete();
            try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
            try { _pc?.close(); } catch { }
            try { Disconnected?.Invoke(); } catch { }
            if (signaling != null)
            {
                // Bounded and off the caller's thread: the close handshake can take seconds.
                _ = Task.Run(async () =>
                {
                    if (sendBye)
                    {
                        using var t = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        try { await signaling.SendByeAsync("disconnect", t.Token).ConfigureAwait(false); } catch { }
                    }
                    try { await signaling.DisposeAsync().ConfigureAwait(false); } catch { }
                });
            }
        }
    }
}
