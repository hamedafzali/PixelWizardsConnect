// Throwaway spike code. Thin platform channel to read iOS's
// ProcessInfo.thermalState, since Dart has no cross-platform CPU/thermal API
// and "rough is fine" per the spike brief.
import 'package:flutter/services.dart';

class ThermalChannel {
  static const _channel = MethodChannel('t9_spike/thermal');

  static Future<String> getThermalState() async {
    final result = await _channel.invokeMethod<String>('getThermalState');
    return result ?? 'unknown';
  }
}
