import 'dart:async';
import 'dart:io';

import 'package:flutter/services.dart';

/// A file another app shared to "Lock with Shutterbox" (copied into Shutterbox's cache).
class SharedFile {
  SharedFile(this.path, this.name, this.mime);
  final String path, name, mime;
  File get file => File(path);
}

/// Shutterbox's own bits of Android (MainActivity, written by tool/patch_native.py).
class Native {
  static const _channel = MethodChannel('shutterbox/native');
  static final _shared = StreamController<void>.broadcast();
  static bool _listening = false;

  /// Fires when something is shared to Shutterbox while it's open.
  static Stream<void> get sharedArrived {
    if (!_listening) {
      _listening = true;
      _channel.setMethodCallHandler((call) async {
        if (call.method == 'shared') _shared.add(null);
      });
    }
    return _shared.stream;
  }

  static bool? _secure;

  /// Keeps the window out of screenshots, screen recordings and the recent-apps preview.
  static Future<void> setSecure(bool on) async {
    if (_secure == on) return;
    _secure = on;
    try {
      await _channel.invokeMethod('setSecure', on);
    } catch (_) {}
  }

  /// What was shared to Shutterbox since the last call.
  static Future<List<SharedFile>> takeShared() async {
    try {
      final list = await _channel.invokeListMethod<Map<dynamic, dynamic>>('takeShared') ?? const [];
      return [for (final m in list) SharedFile('${m['path']}', '${m['name']}', '${m['mime']}')];
    } catch (_) {
      return const [];
    }
  }
}
