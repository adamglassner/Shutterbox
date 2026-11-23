import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:local_auth/local_auth.dart';

/// Fingerprint or face unlock for the Locked album (Android's version of Windows Hello).
///
/// When it's on, a copy of the album's key is kept in this phone's secure storage (encrypted by Android's keystore),
/// and handed out only after a fingerprint or face check. It's per phone and per album: an album opened from another
/// phone or PC needs it turned on again here.
class Biometrics {
  static final _auth = LocalAuthentication();
  static const _storage = FlutterSecureStorage();

  /// What this phone offers ("Fingerprint", "Face unlock"), or null when nothing is set up.
  static Future<String?> available() async {
    try {
      if (!await _auth.isDeviceSupported() || !await _auth.canCheckBiometrics) return null;
      final types = await _auth.getAvailableBiometrics();
      if (types.isEmpty) return null;
      if (types.contains(BiometricType.face) && !types.contains(BiometricType.fingerprint)) return 'Face unlock';
      if (types.contains(BiometricType.fingerprint) && !types.contains(BiometricType.face)) return 'Fingerprint';
      return 'Fingerprint or face';
    } catch (_) {
      return null;
    }
  }

  /// Asks for a fingerprint or face. True when it's you.
  static Future<bool> verify(String reason) async {
    try {
      return await _auth.authenticate(localizedReason: reason, biometricOnly: true, persistAcrossBackgrounding: true);
    } catch (_) {
      return false;
    }
  }

  static String _name(String identity) => 'locked-key-${base64Url.encode(utf8.encode(identity))}';

  static Future<bool> hasKey(String? identity) async {
    if (identity == null) return false;
    try {
      return await _storage.containsKey(key: _name(identity));
    } catch (_) {
      return false;
    }
  }

  static Future<void> saveKey(String identity, List<int> key) => _storage.write(key: _name(identity), value: base64.encode(key));

  /// The album's key after a fingerprint or face check, or null.
  static Future<List<int>?> readKey(String identity) async {
    try {
      final v = await _storage.read(key: _name(identity));
      if (v == null) return null;
      if (!await verify('Unlock your Locked album')) return null;
      return base64.decode(v);
    } catch (_) {
      return null;
    }
  }

  static Future<void> forget(String? identity) async {
    if (identity == null) return;
    try {
      await _storage.delete(key: _name(identity));
    } catch (_) {}
  }
}
