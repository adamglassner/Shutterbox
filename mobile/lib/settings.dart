import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';

/// Shutterbox's settings (settings.json in the app's own storage).
class Settings extends ChangeNotifier {
  Settings([this.file]);

  /// Null in tests: nothing is saved.
  final File? file;

  /// "dark" (the default, so photos stand out), "light" or "system".
  String theme = 'dark';

  /// Amber, Sky, Sage, Coral or Iris.
  String accent = 'Amber';

  /// Thumbnail size: "small", "medium" or "large".
  String density = 'medium';

  /// "day", "month" or "year".
  String groupBy = 'day';
  int slideshowSeconds = 4;

  /// The phone's folders to show; null means all of them.
  List<String>? folders;

  /// The Locked album's folder when it isn't the default.
  String? lockedFolder;

  /// For in-app updates.
  String lastUpdateCheck = '';
  String skippedVersion = '';

  bool askedForAccess = false;

  /// Identifies this phone in the Locked album's wrong-guess log.
  String deviceId = '';

  // Locked album, on this phone
  /// Lock the moment you leave Shutterbox (switch apps or turn the screen off).
  bool lockOnLeave = false;

  /// Lock when the phone is put face down.
  bool lockFaceDown = false;

  /// Hide the Locked tab (open it by long-pressing Photos, or by typing the PIN in search).
  bool hideLocked = false;

  /// Keep the Locked album out of screenshots and the recent-apps preview.
  bool protectScreen = true;

  /// Wrong guesses in a row here, and until when to wait before the next.
  int wrongInARow = 0;
  String waitUntil = '';

  Map<String, dynamic> toJson() => {
        'theme': theme,
        'accent': accent,
        'density': density,
        'groupBy': groupBy,
        'slideshowSeconds': slideshowSeconds,
        'folders': folders,
        'lockedFolder': lockedFolder,
        'lastUpdateCheck': lastUpdateCheck,
        'skippedVersion': skippedVersion,
        'askedForAccess': askedForAccess,
        'deviceId': deviceId,
        'lockOnLeave': lockOnLeave,
        'lockFaceDown': lockFaceDown,
        'hideLocked': hideLocked,
        'protectScreen': protectScreen,
        'wrongInARow': wrongInARow,
        'waitUntil': waitUntil,
      };

  void _read(Map<String, dynamic> j) {
    theme = '${j['theme'] ?? theme}';
    accent = '${j['accent'] ?? accent}';
    density = '${j['density'] ?? density}';
    groupBy = '${j['groupBy'] ?? groupBy}';
    slideshowSeconds = (j['slideshowSeconds'] as num?)?.toInt() ?? slideshowSeconds;
    folders = (j['folders'] as List?)?.map((e) => '$e').toList();
    lockedFolder = j['lockedFolder'] as String?;
    lastUpdateCheck = '${j['lastUpdateCheck'] ?? ''}';
    skippedVersion = '${j['skippedVersion'] ?? ''}';
    askedForAccess = j['askedForAccess'] == true;
    deviceId = '${j['deviceId'] ?? ''}';
    lockOnLeave = j['lockOnLeave'] == true;
    lockFaceDown = j['lockFaceDown'] == true;
    hideLocked = j['hideLocked'] == true;
    protectScreen = j['protectScreen'] != false;
    wrongInARow = (j['wrongInARow'] as num?)?.toInt() ?? 0;
    waitUntil = '${j['waitUntil'] ?? ''}';
  }

  /// How long to wait after [wrongInARow] wrong guesses: none for the first 4, then 30 seconds, doubling up to 15 minutes.
  static Duration waitAfter(int wrong) {
    if (wrong < 5) return Duration.zero;
    final s = 30 * (1 << (wrong - 5).clamp(0, 5).toInt());
    return Duration(seconds: s > 900 ? 900 : s);
  }

  static Future<Settings> load(File file) async {
    final s = Settings(file);
    try {
      if (await file.exists()) s._read(jsonDecode(await file.readAsString()) as Map<String, dynamic>);
    } catch (_) {}
    return s;
  }

  void update(void Function(Settings s) change) {
    change(this);
    notifyListeners();
    save();
  }

  Future<void> save() async {
    final f = file;
    if (f == null) return;
    try {
      await f.parent.create(recursive: true);
      final tmp = File('${f.path}.tmp');
      await tmp.writeAsString(const JsonEncoder.withIndent('  ').convert(toJson()));
      await tmp.rename(f.path);
    } catch (_) {}
  }
}
