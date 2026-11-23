using System.Threading.Channels;
using PixelWizard.Core.Interfaces;
using PixelWizard.Protocol;
using PixelWizard.Signaling;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace PixelWizard.Transport.WebRtc
{
    public enum WebRtcRole
    {
        /// <summary>Offerer: creates the <c>control</c> and <c>media-jpeg</c> data channels. Has already done Register + WaitForPeerAsync.</summary>
        Host,
        /// <summary>Answerer: receives the <c>control</c> and <c>media-jpeg</c> data channels. Has already done JoinAsync.</summary>
        Viewer,
    }

    /// <summary>
    /// <see cref="ISessionTransport"/> over two reliable, ordered RTCDataChannels: <c>control</c> carries every
    /// Control-plane message and <c>media-jpeg</c> every Media-plane message (decided by
    /// <see cref="MessageTypeExtensions.GetPlane"/>), so a large JPEG frame never sits in front of a Ping,
    /// chat line or input event in the same channel queue. Each channel has its own reassembly buffer, send
    /// lock and high-water mark; both feed one ordered receive loop, so <see cref="MessageReceived"/> is a
    /// single stream (ordering is guaranteed within a plane, not across the two).
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
    public sealed class WebRtcTransport : ISessionTransport, IVideoTrackTransport
    {
        public const string ControlChannelLabel = "control";
        public const string MediaChannelLabel = "media-jpeg";
        /// <summary>The H.264 fmtp both sides must carry (PHASE4-PLAN finding 2), or setRemoteDescription fails with VideoIncompatible.</summary>
        public const string H264Fmtp = "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f";
        internal const int H264PayloadType = 96;
        internal const int ChunkSize = 16 * 1024;
        internal const int MaxConsecutiveHandlerFailures = 5;
        /// <summary>
        /// Sender backpressure: before each chunk, SendMessageAsync waits while the channel's buffered amount
        /// exceeds this. SIPSorcery 10.0.16 queues send() unboundedly and its onbufferedamountlow event is
        /// commented out, so the wait polls bufferedAmount every <see cref="BackpressurePollMs"/> ms.
        /// </summary>
        internal const int HighWaterMark = 1024 * 1024;
        /// <summary>
        /// The media channel's own, smaller mark: the capture loop awaits each send, so this is the most
        /// frame data that can be queued ahead of the next frame (about 80 ms at the ~3.3 MB/s SIPSorcery reaches).
        /// </summary>
        internal const int MediaHighWaterMark = 256 * 1024;
        internal const int BackpressurePollMs = 5;
        private const int MaxMessageSize = 64 * 1024 * 1024; // sanity cap on a reassembled message

        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly Channel<Func<Task>> _sendQueue = Channel.CreateUnbounded<Func<Task>>();
        private readonly List<RTCIceCandidateInit> _pendingRemote = new();
        private readonly List<RTCIceCandidateInit> _pendingLocal = new();
        private readonly List<RTCIceCandidateInit> _appliedRemote = new();
        private readonly Lane _control = new(ControlChannelLabel, HighWaterMark);
        private readonly Lane _media = new(MediaChannelLabel, MediaHighWaterMark);

        private RTCPeerConnection? _pc;
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

        /// <summary>Test hook: the largest control channel buffered amount seen right after a chunk was queued.</summary>
        internal long MaxBufferedObserved => Interlocked.Read(ref _control.MaxBuffered);
        /// <summary>Test hook: the largest media channel buffered amount seen right after a chunk was queued.</summary>
        internal long MaxMediaBufferedObserved => Interlocked.Read(ref _media.MaxBuffered);
        /// <summary>Test hooks: wire bytes (chunk header included) sent and received on the media channel.</summary>
        internal long MediaBytesSent => Interlocked.Read(ref _media.BytesSent);
        internal long MediaBytesReceived => Interlocked.Read(ref _media.BytesReceived);
        internal long ControlBytesSent => Interlocked.Read(ref _control.BytesSent);
        internal long ControlBytesReceived => Interlocked.Read(ref _control.BytesReceived);

        /// <summary>Per data channel state: the channel, its send lock, reassembly buffer, mark and counters.</summary>
        private sealed class Lane
        {
            public readonly string Label;
            public readonly int HighWater;
            public readonly SemaphoreSlim SendLock = new(1, 1);
            public readonly MemoryStream Assembly = new();
            public RTCDataChannel? Dc;
            public volatile bool Open;
            public long MaxBuffered, BytesSent, BytesReceived;
            public Lane(string label, int highWater) { Label = label; HighWater = highWater; }
        }

        public event Action<NetworkMessage>? MessageReceived;
        public event Action? Connected;
        public event Action? Disconnected;
        public event Action<Exception>? Error;
        public event Action<Exception>? HandlerError;
        public event Action<int>? BytesReceived;
        public event Action<int>? BytesSent;

        /// <summary>
        /// Adds an H.264 video track to the offer (host: send only) or the answer (viewer: receive only). Set
        /// before <see cref="EstablishAsync"/>. A peer that leaves it false never takes part in video, and the
        /// session stays on media-jpeg. Nothing is written to the track until the session layer sends on it.
        /// </summary>
        public bool EnableVideoTrack { get; set; }

        private volatile bool _videoNegotiated;
        private long _videoPacketsSent;
        public bool VideoTrackNegotiated => _videoNegotiated;
        /// <summary>Test hooks: access units handed to the track and RTP-level counts.</summary>
        internal long VideoAccessUnitsSent => Interlocked.Read(ref _videoPacketsSent);
        internal long VideoAccessUnitsReceived => Interlocked.Read(ref _videoReceived);
        private long _videoReceived;

        public event Action<byte[], uint>? VideoAccessUnitReceived;
        public event Action? KeyframeRequested;

        public void SendVideoAccessUnit(byte[] annexB, uint durationRtpUnits)
        {
            var pc = _pc;
            if (pc == null || !_videoNegotiated || !IsConnected || annexB.Length == 0) return;
            try
            {
                pc.SendVideo(durationRtpUnits, annexB);
                Interlocked.Increment(ref _videoPacketsSent);
            }
            catch (Exception ex) { Fail(ex); }
        }

        public void RequestKeyframe()
        {
            var pc = _pc;
            var vs = pc?.VideoStream;
            if (pc == null || vs?.LocalTrack == null || vs.RemoteTrack == null || !_videoNegotiated || !IsConnected) return;
            try { vs.SendRtcpFeedback(new RTCPFeedback(vs.LocalTrack.Ssrc, vs.RemoteTrack.Ssrc, PSFBFeedbackTypesEnum.PLI)); }
            catch (Exception ex) { HandlerError?.Invoke(ex); }
        }

        // Called once the remote description is applied. The track is negotiated only when the remote side kept
        // the video m-line alive: a peer that did not add a track answers it a=inactive (or port 0).
        private void EvaluateVideoNegotiation(RTCPeerConnection pc)
        {
            if (!EnableVideoTrack) return;
            var video = pc.remoteDescription?.sdp?.Media.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.video);
            _videoNegotiated = video != null && video.Port != 0
                && video.MediaStreamStatus != MediaStreamStatusEnum.Inactive
                && video.MediaFormats.Values.Any(f => f.Name().Equals("H264", StringComparison.OrdinalIgnoreCase));
        }

        private static MediaStreamTrack NewVideoTrack(MediaStreamStatusEnum status) =>
            new(SDPMediaTypesEnum.video, false, new List<SDPAudioVideoMediaFormat>
            {
                new(new VideoFormat(VideoCodecsEnum.H264, H264PayloadType, 90000, H264Fmtp)),
            }, status);

        private void WireVideo(RTCPeerConnection pc)
        {
            pc.OnVideoFrameReceived += (_, timestamp, frame, format) =>
            {
                if (format.Codec != VideoCodecsEnum.H264 || frame.Length == 0) return;
                Interlocked.Increment(ref _videoReceived);
                try { VideoAccessUnitReceived?.Invoke(frame, timestamp); } catch (Exception ex) { HandlerError?.Invoke(ex); }
            };
            pc.OnReceiveReport += (_, media, report) =>
            {
                if (media != SDPMediaTypesEnum.video || report.Feedback == null) return;
                var h = report.Feedback.Header;
                if (h.PacketType == RTCPReportTypesEnum.PSFB
                    && (h.PayloadFeedbackMessageType == PSFBFeedbackTypesEnum.PLI || h.PayloadFeedbackMessageType == PSFBFeedbackTypesEnum.FIR))
                    try { KeyframeRequested?.Invoke(); } catch (Exception ex) { HandlerError?.Invoke(ex); }
            };
        }

        public bool IsConnected => _open && Volatile.Read(ref _down) == 0;

        /// <summary>How long <see cref="EstablishAsync"/> waits for the channel to open. Default 60 s.</summary>
        public TimeSpan EstablishTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Nominated candidate pair as "local/remote" candidate types (e.g. "host/host", "relay/srflx"),
        /// read from the RTP channel's NominatedEntry once ICE is connected. Null until a pair can be read:
        /// SIPSorcery may report ICE connected (and the channel may open) before NominatedEntry is set, so
        /// an unreadable pair is never cached and a later read tries again.
        /// A remote "prflx" (peer-reflexive) candidate is reported as the type of the signalled candidate with
        /// the same address and port: when the peer's connectivity check outruns its trickled candidate,
        /// SIPSorcery learns the address from the check and types it prflx, which would hide a relay or host
        /// pair. A prflx that no signalled candidate explains yet is reported as is and not cached.
        /// </summary>
        public string? SelectedPairType
        {
            get
            {
                if (_selectedPair != null) return _selectedPair;
                var pair = ReadPair(out bool final);
                if (final) _selectedPair = pair;
                return pair;
            }
        }

        /// <summary>
        /// Builds the peer connection and completes when both the control and media channels are open. Faults, and cleans up
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
                if (EnableVideoTrack)
                {
                    WireVideo(pc);
                    // The host only sends and the viewer only receives; both directions still carry RTCP (the viewer's PLI).
                    pc.addTrack(NewVideoTrack(role == WebRtcRole.Host ? MediaStreamStatusEnum.SendOnly : MediaStreamStatusEnum.RecvOnly));
                }

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
                    if (s == RTCIceConnectionState.connected) _ = SelectedPairType;
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
                    // Both channels exist before the offer, so one SDP negotiates both.
                    Wire(_control, await pc.createDataChannel(ControlChannelLabel).ConfigureAwait(false));
                    Wire(_media, await pc.createDataChannel(MediaChannelLabel).ConfigureAwait(false));
                    var offer = pc.createOffer();
                    await pc.setLocalDescription(offer).ConfigureAwait(false);
                    await signaling.SendOfferAsync(offer.sdp, linked.Token).ConfigureAwait(false);
                    AfterSdpSent();
                }
                else
                {
                    pc.ondatachannel += dc =>
                    {
                        if (dc.label == ControlChannelLabel) Wire(_control, dc);
                        else if (dc.label == MediaChannelLabel) Wire(_media, dc);
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
                    throw new TimeoutException($"WebRTC data channels did not open within {EstablishTimeout.TotalSeconds:0.#} s.");
                throw;
            }
        }

        public async Task SendMessageAsync(NetworkMessage message)
        {
            var lane = message.Type.GetPlane() == MessagePlane.Media ? _media : _control;
            if (!IsConnected || lane.Dc == null) return;
            var dc = lane.Dc;
            await lane.SendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsConnected) return;
                byte[] data = message.Serialize();
                int sent = 0, offset = 0;
                do
                {
                    int n = Math.Min(ChunkSize, data.Length - offset);
                    bool last = offset + n >= data.Length;
                    // Backpressure: never let this channel's send queue grow past its high-water mark plus one chunk.
                    while (dc.bufferedAmount > (ulong)lane.HighWater)
                    {
                        if (!IsConnected) return;
                        await Task.Delay(BackpressurePollMs, _lifetime.Token).ConfigureAwait(false);
                    }
                    if (!IsConnected) return;
                    var chunk = new byte[1 + n];
                    chunk[0] = last ? (byte)0 : (byte)1;
                    Buffer.BlockCopy(data, offset, chunk, 1, n);
                    dc.send(chunk);
                    long buffered = (long)dc.bufferedAmount;
                    long seen;
                    while (buffered > (seen = Interlocked.Read(ref lane.MaxBuffered))
                           && Interlocked.CompareExchange(ref lane.MaxBuffered, buffered, seen) != seen) { }
                    sent += chunk.Length;
                    offset += n;
                } while (offset < data.Length);
                Interlocked.Add(ref lane.BytesSent, sent);
                BytesSent?.Invoke(sent);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Fail(ex);
            }
            finally
            {
                lane.SendLock.Release();
            }
        }

        /// <summary>Test hook: closes only the media data channel, to prove either channel closing ends the transport.</summary>
        internal void CloseMediaChannelForTest() => _media.Dc?.close();

        /// <summary>Local close: tells the peer (bye over signaling), closes the connection, raises Disconnected once.</summary>
        public void Disconnect() => Teardown(sendBye: true);

        public void Dispose()
        {
            Teardown(sendBye: false);
        }

        // ---- internals ----

        private void Wire(Lane lane, RTCDataChannel dc)
        {
            lane.Dc = dc;
            dc.onopen += () => OnOpen(lane);
            dc.onclose += () => Fail(new IOException($"{lane.Label} channel closed"), raiseError: false);
            dc.onerror += e => Fail(new IOException($"{lane.Label} channel error: {e}"));
            dc.onmessage += (_, _, data) => OnChannelData(lane, data);
            if (dc.readyState == RTCDataChannelState.open) OnOpen(lane);
        }

        // The transport is open (and Connected raised, EstablishAsync completed) once BOTH channels are open.
        private void OnOpen(Lane lane)
        {
            bool bothOpen;
            bool firstOpen;
            lock (_gate)
            {
                if (lane.Open || Volatile.Read(ref _down) != 0) return;
                firstOpen = !_control.Open && !_media.Open;
                lane.Open = true;
                bothOpen = _control.Open && _media.Open;
                if (bothOpen) _open = true;
            }
            // The tuning is per SCTP association, not per channel: both channels share one sender, so it
            // is applied once, as soon as the association carries its first open channel.
            if (firstOpen && _pc != null) SctpTuned = SctpTuning.TryApply(_pc);
            if (!bothOpen) return;
            _ = SelectedPairType;
            try { Connected?.Invoke(); } catch (Exception ex) { HandlerError?.Invoke(ex); }
            _established?.TrySetResult();
        }

        // Runs on the SCTP thread: reassemble only, then hand the whole message to the ordered receive loop.
        private void OnChannelData(Lane lane, byte[] data)
        {
            if (data.Length < 1) return;
            lock (lane.Assembly)
            {
                if (lane.Assembly.Length + data.Length - 1 > MaxMessageSize)
                {
                    lane.Assembly.SetLength(0);
                    Fail(new InvalidDataException("Reassembled message exceeds the size limit."));
                    return;
                }
                lane.Assembly.Write(data, 1, data.Length - 1);
                Interlocked.Add(ref lane.BytesReceived, data.Length);
                BytesReceived?.Invoke(data.Length);
                if (data[0] != 0) return;
                var whole = lane.Assembly.ToArray();
                lane.Assembly.SetLength(0);
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
                            EvaluateVideoNegotiation(pc);
                            var answer = pc.createAnswer();
                            await pc.setLocalDescription(answer).ConfigureAwait(false);
                            await signaling.SendAnswerAsync(answer.sdp, _lifetime.Token).ConfigureAwait(false);
                            AfterSdpSent();
                            break;
                        case AnswerMessage a when role == WebRtcRole.Host:
                            var ares = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = a.Sdp });
                            if (ares != SetDescriptionResultEnum.OK) throw new InvalidOperationException($"setRemoteDescription: {ares}");
                            FlushRemote();
                            EvaluateVideoNegotiation(pc);
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
                if (!_open) Fail(new IOException("Signaling ended before the data channels opened."));
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

        private string? ReadPair(out bool final)
        {
            final = false;
            var entry = _pc?.GetRtpChannel()?.NominatedEntry;
            if (entry == null) return null;
            var remote = entry.RemoteCandidate;
            var remoteType = remote.type;
            final = true;
            if (remoteType == RTCIceCandidateType.prflx)
            {
                var signalled = SignalledType(remote.address, remote.port);
                if (signalled != null) remoteType = signalled.Value;
                else final = false;
            }
            return $"{entry.LocalCandidate.type}/{remoteType}";
        }

        private RTCIceCandidateType? SignalledType(string address, ushort port)
        {
            foreach (var init in AppliedRemoteCandidates)
            {
                try
                {
                    var line = init.candidate ?? "";
                    var c = RTCIceCandidate.Parse(line.StartsWith("candidate:") ? line : "candidate:" + line);
                    if (c.port == port && string.Equals(c.address, address, StringComparison.OrdinalIgnoreCase)) return c.type;
                }
                catch (Exception) { } // an unparsable candidate cannot explain the pair
            }
            return null;
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
