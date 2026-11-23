import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;
import 'package:open_filex/open_filex.dart';
import 'package:path_provider/path_provider.dart';

import 'settings.dart';
import 'version.dart';

/// A newer Shutterbox for Android on GitHub.
class UpdateInfo {
  UpdateInfo(this.version, this.apkUrl, this.apkName, this.size, this.notes, this.pageUrl);
  final String version, apkUrl, apkName, notes, pageUrl;
  final int size;
}

/// Updates straight from GitHub releases (android-v… tags; the Windows app's are v…). Checks once a day, and when you
/// tap Check for updates in Settings: it downloads the new APK and hands it to Android's installer.
class Updater extends ChangeNotifier {
  Updater(this.settings, {this.client, this.current = appVersion});

  static const repo = 'adamglassner/Shutterbox';
  static const releasesPage = 'https://github.com/$repo/releases';
  static const tagPrefix = 'android-v';

  final Settings settings;
  final http.Client? client;
  final String current;

  UpdateInfo? available;
  bool checking = false;
  String? problem;
  DateTime? lastChecked;

  /// 0..1 while downloading, null otherwise.
  double? progress;

  /// "1.2.10" > "1.2.9"
  static bool isNewer(String a, String b) {
    List<int> parts(String v) => [for (final x in v.split('.')) int.tryParse(x.replaceAll(RegExp(r'[^0-9]'), '')) ?? 0];
    final x = parts(a), y = parts(b);
    for (var i = 0; i < 4; i++) {
      final xi = i < x.length ? x[i] : 0, yi = i < y.length ? y[i] : 0;
      if (xi != yi) return xi > yi;
    }
    return false;
  }

  /// The newest Android release in a GitHub releases list, when it's newer than [current].
  static UpdateInfo? pick(List<dynamic> releases, String current) {
    UpdateInfo? best;
    for (final r in releases.whereType<Map>()) {
      final tag = '${r['tag_name'] ?? ''}';
      if (!tag.startsWith(tagPrefix) || r['draft'] == true || r['prerelease'] == true) continue;
      final version = tag.substring(tagPrefix.length);
      if (!isNewer(version, current) || (best != null && !isNewer(version, best.version))) continue;
      final assets = (r['assets'] as List? ?? const []).whereType<Map>().where((a) => '${a['name']}'.toLowerCase().endsWith('.apk')).toList();
      if (assets.isEmpty) continue;
      best = UpdateInfo(version, '${assets.first['browser_download_url']}', '${assets.first['name']}', (assets.first['size'] as num?)?.toInt() ?? 0,
          '${r['body'] ?? ''}', '${r['html_url'] ?? releasesPage}');
    }
    return best;
  }

  /// Checks once a day (or now with [force]). Returns the update, if there is one.
  Future<UpdateInfo?> check({bool force = false}) async {
    final last = DateTime.tryParse(settings.lastUpdateCheck);
    if (!force && last != null && DateTime.now().difference(last).inHours < 20) return available;
    checking = true;
    problem = null;
    notifyListeners();
    final c = client ?? http.Client();
    try {
      final res = await c.get(Uri.parse('https://api.github.com/repos/$repo/releases?per_page=30'),
          headers: {'Accept': 'application/vnd.github+json', 'User-Agent': 'Shutterbox-Android/$current'}).timeout(const Duration(seconds: 20));
      if (res.statusCode != 200) throw HttpException('GitHub said ${res.statusCode}');
      available = pick(jsonDecode(res.body) as List<dynamic>, current);
      lastChecked = DateTime.now();
      settings.update((s) => s.lastUpdateCheck = DateTime.now().toIso8601String());
    } catch (e) {
      problem = e is TimeoutException ? 'GitHub didn\'t answer in time.' : 'Couldn\'t check for updates ($e).';
    } finally {
      if (client == null) c.close();
      checking = false;
      notifyListeners();
    }
    return available;
  }

  /// Hidden from the banner until a newer one comes out.
  bool get dismissed => available != null && settings.skippedVersion == available!.version;
  void dismiss() {
    if (available != null) settings.update((s) => s.skippedVersion = available!.version);
    notifyListeners();
  }

  /// Downloads the APK and opens Android's installer. Returns an error message, or null.
  Future<String?> install() async {
    final u = available;
    if (u == null) return 'No update to install.';
    progress = 0;
    problem = null;
    notifyListeners();
    final c = client ?? http.Client();
    try {
      final dir = Directory('${(await getTemporaryDirectory()).path}/updates');
      if (await dir.exists()) await dir.delete(recursive: true);
      await dir.create(recursive: true);
      final file = File('${dir.path}/${u.apkName}');
      final res = await c.send(http.Request('GET', Uri.parse(u.apkUrl))..headers['User-Agent'] = 'Shutterbox-Android/$current');
      if (res.statusCode != 200) throw HttpException('download failed (${res.statusCode})');
      final total = res.contentLength ?? u.size;
      final sink = file.openWrite();
      var got = 0;
      await for (final chunk in res.stream) {
        sink.add(chunk);
        got += chunk.length;
        if (total > 0) {
          progress = got / total;
          notifyListeners();
        }
      }
      await sink.close();
      progress = null;
      notifyListeners();
      final r = await OpenFilex.open(file.path, type: 'application/vnd.android.package-archive');
      if (r.type != ResultType.done) {
        return 'Downloaded, but Android didn\'t open the installer (${r.message}). Allow Shutterbox to install apps when Android asks, then try again.';
      }
      return null;
    } catch (e) {
      progress = null;
      problem = 'Couldn\'t download the update ($e).';
      notifyListeners();
      return problem;
    } finally {
      if (client == null) c.close();
    }
  }
}
