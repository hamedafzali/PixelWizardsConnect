// Throwaway spike code. Ugly on purpose. Never referenced by production code.
//
// T9: real camera video, negotiated with T8's SIPSorcery peer, over a TURN
// relay reachable via the iPhone-USB-personal-hotspot LAN. Signaling reuses
// T8's crude offer.json/answer.json file drop, just fronted by a tiny dumb
// HTTP shim (signaling-server.mjs) since the phone can't read the Mac's
// filesystem directly -- this is not a real signaling protocol, just the
// same two-blob exchange over a transport that can reach a phone.
import 'dart:async';
import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:http/http.dart' as http;
import 'package:flutter_webrtc/flutter_webrtc.dart';
import 'thermal_channel.dart';

void main() => runApp(const SpikeApp());

class SpikeApp extends StatelessWidget {
  const SpikeApp({super.key});
  @override
  Widget build(BuildContext context) => const MaterialApp(home: SpikeHome());
}

class SpikeHome extends StatefulWidget {
  const SpikeHome({super.key});
  @override
  State<SpikeHome> createState() => _SpikeHomeState();
}

class _SpikeHomeState extends State<SpikeHome> {
  // Set from the on-screen text field before pressing Start -- the Mac's LAN
  // IP on the iPhone-personal-hotspot interface (checked via `ipconfig
  // getifaddr en0` on the Mac side; typically 172.20.10.2 in this setup).
  final _hostController = TextEditingController(text: '172.20.10.2');
  final _log = <String>[];
  final _localRenderer = RTCVideoRenderer();
  final _remoteRenderer = RTCVideoRenderer();
  RTCPeerConnection? _pc;
  RTCDataChannel? _dc;
  Timer? _statsTimer;
  Timer? _pingTimer;
  Timer? _thermalTimer;
  int _dcSent = 0;
  int _dcRecv = 0;
  DateTime? _startTime;

  void _addLog(String s) {
    final line = '[${DateTime.now().toIso8601String()}] $s';
    debugPrint('[spike] $line');
    setState(() {
      _log.add(line);
      if (_log.length > 200) _log.removeAt(0);
    });
  }

  @override
  void initState() {
    super.initState();
    _localRenderer.initialize();
    _remoteRenderer.initialize();
  }

  Future<void> _start() async {
    final host = _hostController.text.trim();
    _startTime = DateTime.now();
    _addLog('starting, signaling host=$host');

    final mediaConstraints = <String, dynamic>{
      'audio': false,
      'video': {
        'facingMode': 'user',
        'width': {'ideal': 1280},
        'height': {'ideal': 720},
        'frameRate': {'ideal': 30},
      },
    };
    final localStream =
        await navigator.mediaDevices.getUserMedia(mediaConstraints);
    _localRenderer.srcObject = localStream;
    _addLog('got local camera stream');

    final config = <String, dynamic>{
      'iceServers': [
        {
          'urls': 'turn:$host:3478',
          'username': 'spike',
          'credential': 'spikepass',
        }
      ],
      'iceTransportPolicy': 'relay',
    };
    final pc = await createPeerConnection(config);
    _pc = pc;

    for (final track in localStream.getTracks()) {
      await pc.addTrack(track, localStream);
    }

    pc.onIceConnectionState = (state) => _addLog('ice state -> $state');
    pc.onConnectionState = (state) => _addLog('connection state -> $state');
    pc.onIceCandidate = (c) => debugPrint('[spike] local candidate: ${c.candidate}');
    pc.onTrack = (event) {
      _addLog('ontrack ${event.track.kind}');
      if (event.track.kind == 'video') {
        _remoteRenderer.srcObject = event.streams.isNotEmpty ? event.streams[0] : null;
        setState(() {});
      }
    };
    pc.onDataChannel = (channel) {
      _addLog('data channel received (remote-initiated): ${channel.label}');
      _dc = channel;
      channel.onMessage = (msg) {
        _dcRecv++;
        try {
          channel.send(RTCDataChannelMessage('pong from flutter t=${DateTime.now().millisecondsSinceEpoch}'));
          _dcSent++;
        } catch (_) {}
      };
    };

    // Poll for the offer the .NET/SIPSorcery peer wrote out (via the HTTP
    // signaling shim fronting T8's offer.json file drop).
    String? offerSdp;
    _addLog('polling for offer at http://$host:8787/offer');
    while (offerSdp == null) {
      try {
        final resp = await http.get(Uri.parse('http://$host:8787/offer'));
        if (resp.statusCode == 200 && resp.body.isNotEmpty) offerSdp = resp.body;
      } catch (e) {
        // signaling server not up yet -- keep polling
      }
      if (offerSdp == null) await Future.delayed(const Duration(milliseconds: 500));
    }
    _addLog('got offer (${offerSdp.length} bytes)');

    await pc.setRemoteDescription(RTCSessionDescription(offerSdp, 'offer'));
    final answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);

    // Mirror T8's browser-side approach: wait for ICE gathering to finish (or
    // time out) before reading back localDescription, to see whether
    // flutter_webrtc's libwebrtc -- like Chrome's, unlike SIPSorcery's --
    // actually merges gathered candidates into localDescription on its own.
    final gatherComplete = Completer<void>();
    pc.onIceGatheringState = (state) {
      if (state == RTCIceGatheringState.RTCIceGatheringStateComplete && !gatherComplete.isCompleted) {
        gatherComplete.complete();
      }
    };
    await gatherComplete.future.timeout(const Duration(seconds: 4), onTimeout: () {});

    final localDesc = await pc.getLocalDescription();
    final answerSdp = localDesc?.sdp ?? answer.sdp ?? '';
    final candidateLines = 'a=candidate'.allMatches(answerSdp).length;
    _addLog('answer localDescription has $candidateLines a=candidate line(s) '
        '(finding #4 check: does flutter_webrtc merge gathered candidates '
        'into localDescription like Chrome did, unlike SIPSorcery?)');

    await http.post(Uri.parse('http://$host:8787/answer'), body: answerSdp);
    _addLog('posted answer');

    _pingTimer = Timer.periodic(const Duration(milliseconds: 500), (_) {
      final dc = _dc;
      if (dc != null && dc.state == RTCDataChannelState.RTCDataChannelOpen) {
        dc.send(RTCDataChannelMessage('ping ${_dcSent++} t=${DateTime.now().millisecondsSinceEpoch}'));
      }
    });

    _statsTimer = Timer.periodic(const Duration(seconds: 5), (_) async {
      await _logStats(pc);
    });

    _thermalTimer = Timer.periodic(const Duration(seconds: 30), (_) async {
      try {
        final state = await ThermalChannel.getThermalState();
        final elapsed = DateTime.now().difference(_startTime!).inSeconds;
        _addLog('t=${elapsed}s thermalState=$state');
      } catch (e) {
        _addLog('thermal channel error: $e');
      }
    });

    setState(() {});
  }

  Future<void> _logStats(RTCPeerConnection pc) async {
    final stats = await pc.getStats();
    for (final r in stats) {
      if (r.type == 'outbound-rtp' && r.values['kind'] == 'video') {
        _addLog('[stats] outbound video: '
            'frameWidth=${r.values['frameWidth']} '
            'frameHeight=${r.values['frameHeight']} '
            'framesPerSecond=${r.values['framesPerSecond']} '
            'framesSent=${r.values['framesSent']} '
            'qualityLimitationReason=${r.values['qualityLimitationReason']}');
      }
      if (r.type == 'inbound-rtp' && r.values['kind'] == 'video') {
        _addLog('[stats] inbound video: '
            'jitterBufferDelay=${r.values['jitterBufferDelay']} '
            'jitterBufferEmittedCount=${r.values['jitterBufferEmittedCount']} '
            'framesDecoded=${r.values['framesDecoded']} '
            'framesPerSecond=${r.values['framesPerSecond']}');
      }
      if (r.type == 'codec') {
        _addLog('[stats] codec: mimeType=${r.values['mimeType']} '
            'sdpFmtpLine=${r.values['sdpFmtpLine']} '
            'payloadType=${r.values['payloadType']}');
      }
    }
    _addLog('[stats] data channel: sent=$_dcSent recv=$_dcRecv');
  }

  @override
  void dispose() {
    _statsTimer?.cancel();
    _pingTimer?.cancel();
    _thermalTimer?.cancel();
    _pc?.close();
    _localRenderer.dispose();
    _remoteRenderer.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('T9 WebRTC spike')),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.all(8),
            child: Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _hostController,
                    decoration: const InputDecoration(labelText: 'Mac LAN IP'),
                  ),
                ),
                ElevatedButton(onPressed: _start, child: const Text('Start')),
              ],
            ),
          ),
          SizedBox(
            height: 150,
            child: Row(children: [
              Expanded(child: RTCVideoView(_localRenderer, mirror: true)),
              Expanded(child: RTCVideoView(_remoteRenderer)),
            ]),
          ),
          Expanded(
            child: ListView.builder(
              itemCount: _log.length,
              itemBuilder: (_, i) => Text(_log[i], style: const TextStyle(fontSize: 10)),
            ),
          ),
        ],
      ),
    );
  }
}
