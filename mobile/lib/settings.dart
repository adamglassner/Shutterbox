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
