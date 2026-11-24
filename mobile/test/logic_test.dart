import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:shutterbox/library.dart';
import 'package:shutterbox/models.dart';
import 'package:shutterbox/screens/places.dart';
import 'package:shutterbox/settings.dart';
import 'package:shutterbox/updater.dart';
import 'package:shutterbox/version.dart';

import 'fake_source.dart';

void main() {
  test('dates in file names', () {
    expect(dateFromName('IMG_20261014_091230.jpg'), DateTime(2026, 10, 14, 9, 12, 30));
    expect(dateFromName('PXL_20260704_164500123.mp4'), DateTime(2026, 7, 4, 16, 45, 0));
    expect(dateFromName('IMG-20251231-WA0001.jpg'), DateTime(2025, 12, 31));
    expect(dateFromName('Screenshot_2026-01-02-03-04-05.png'), DateTime(2026, 1, 2, 3, 4, 5));
    expect(dateFromName('2026-10-14 09.12.30.jpg'), DateTime(2026, 10, 14, 9, 12, 30));
    expect(dateFromName('DSC_0042.JPG'), isNull);
    expect(dateFromName('IMG_20261399_000000.jpg'), isNull);
  });

  test('formatting', () {
    expect(formatTime(DateTime(2026, 1, 1, 14, 8)), '2:08 PM');
    expect(formatTime(DateTime(2026, 1, 1, 0, 5)), '12:05 AM');
    expect(formatDuration(65), '1:05');
    expect(formatDuration(3723), '1:02:03');
    expect(formatSize(3.4 * 1024 * 1024 ~/ 1), '3.4 MB');
    expect(formatDay(DateTime(2026, 10, 14)), 'Wednesday, Oct 14, 2026');
    expect(Item.isVideoName('Clip.MOV'), isTrue);
    expect(Item.isVideoName('photo.heic'), isFalse);
  });

  group('library', () {
    late Library lib;
    late FakeSource source;
    setUp(() {
      source = FakeSource();
      lib = Library(source, Settings());
      lib.setItems(source.sample());
    });

    test('photos and videos are kept apart', () {
      expect(lib.photos.any((i) => i.isVideo), isFalse);
      expect(lib.videos.every((i) => i.isVideo), isTrue);
      expect(lib.photos.length + lib.videos.length, lib.visible.length);
      expect(lib.videos.length, 8);
    });

    test('search: several words narrow it down', () {
      lib.addTag(lib.photos.first, 'Portland');
      lib.setCaption(lib.photos[1], 'Sunset at the coast');
      expect(lib.search('portland').single, lib.photos.first);
      expect(lib.search('coast').single, lib.photos[1]);
      expect(lib.search('canon').every((i) => i.make == 'Canon'), isTrue);
      expect(lib.search('pixel november').every((i) => i.model == 'Pixel 9 Pro' && i.taken.month == 11), isTrue);
      expect(lib.search('screenshots').every((i) => i.folder == 'Screenshots'), isTrue);
      final a = lib.createAlbum('Road trip', lib.photos.take(3));
      expect(lib.search('road trip').length, 3);
      expect(lib.search('video').every((i) => i.isVideo), isTrue);
      expect(a.name, 'Road trip');
    });

    test('favorites, rotation, trash and albums', () {
      final p = lib.photos.first;
      lib.toggleFavorite(p);
      expect(lib.favorites, [p]);
      lib.rotate([p], 90);
      lib.rotate([p], 270);
      lib.rotate([p], -90);
      expect(p.rotation, 270);
      lib.moveToTrash([p]);
      expect(lib.photos.contains(p), isFalse);
      expect(lib.trash, [p]);
      expect(lib.favorites, isEmpty);
      lib.restoreFromTrash([p]);
      expect(lib.photos.first, p);
      final a = lib.createAlbum('Family');
      final b = lib.createAlbum('family');
      expect(b.name, 'family (2)');
      expect(lib.addToAlbum(a, lib.photos.take(4)), 4);
      expect(lib.addToAlbum(a, lib.photos.take(5)), 1);
      expect(lib.coverOf(a), lib.photos.first);
      lib.setCover(a, lib.photos[2]);
      expect(lib.coverOf(a), lib.photos[2]);
      lib.removeFromAlbum(a, [lib.photos[2]]);
      expect(lib.inAlbum(a).length, 4);
      expect(lib.coverOf(a), lib.photos.first);
      lib.renameAlbum(b, 'Family');
      expect(b.name, 'Family (2)');
      lib.deleteAlbum(b);
      expect(lib.albums, [a]);
    });

    test('emptying Trash deletes from the phone', () async {
      final list = lib.photos.take(2).toList();
      lib.moveToTrash(list);
      expect(await lib.emptyTrash(), 2);
      expect(source.deleted, unorderedEquals(['p0', 'p1']));
      expect(lib.all.contains(list.first), isFalse);
    });

    test('grouping by day, month and year', () {
      final days = groupItems(lib.photos, 'day');
      expect(days.first.items.every((i) => i.taken.day == days.first.items.first.taken.day), isTrue);
      expect(days.first.subtitle, contains('photo'));
      expect(days.map((g) => g.items.length).reduce((a, b) => a + b), lib.photos.length);
      final months = groupItems(lib.photos, 'month');
      expect(months.first.title, 'November 2026');
      expect(groupItems(lib.photos, 'year').single.title, '2026');
    });

    test('folders', () {
      final f = lib.folders;
      expect(f.map((x) => x.$2), ['Camera', 'Screenshots', 'WhatsApp Images']);
      expect(lib.inFolder('f3').every((i) => i.folder == 'Screenshots'), isTrue);
    });

    test('on this day', () {
      final now = DateTime.now();
      lib.setItems([
        ...source.sample(),
        Item(id: 'old', name: 'old.jpg', taken: DateTime(now.year - 3, now.month, now.day, 9)),
        Item(id: 'other', name: 'other.jpg', taken: DateTime(now.year - 3, now.month == 1 ? 2 : 1, 1)),
      ]);
      expect(lib.onThisDay.map((i) => i.id), ['old']);
    });

    test('places group photos taken close together', () {
      final items = [
        Item(id: 'a', name: 'a.jpg', taken: DateTime(2026), latitude: 45.5231, longitude: -122.6765),
        Item(id: 'b', name: 'b.jpg', taken: DateTime(2026), latitude: 45.5260, longitude: -122.6800),
        Item(id: 'c', name: 'c.jpg', taken: DateTime(2026), latitude: 48.8566, longitude: 2.3522),
      ];
      expect(clusterItems(items, 4).map((c) => c.items.length), [2, 1]);
      expect(clusterItems(items, 18).length, 3); // close enough together only when zoomed out
      expect(clusterItems(items, 4).first.center.latitude, closeTo(45.52455, 1e-6));
    });

    test('recently added is newest first', () {
      final r = lib.recent;
      for (var i = 1; i < r.length; i++) {
        expect(r[i - 1].added.isBefore(r[i].added), isFalse);
      }
    });
  });

  group('updates from GitHub', () {
    test('versions', () {
      expect(Updater.isNewer('0.3.1', '0.3.0'), isTrue);
      expect(Updater.isNewer('0.10.0', '0.9.9'), isTrue);
      expect(Updater.isNewer('0.3.0', '0.3.0'), isFalse);
      expect(Updater.isNewer('0.2.9', '0.3.0'), isFalse);
    });

    final releases = [
      {'tag_name': 'v0.9.0', 'assets': [{'name': 'Shutterbox-0.9.0-Setup.exe', 'browser_download_url': 'x'}]},
      {'tag_name': 'android-v0.4.0', 'html_url': 'page', 'body': 'Notes', 'assets': [{'name': 'Shutterbox-Android-0.4.0.apk', 'browser_download_url': 'https://example/0.4.0.apk', 'size': 1234}]},
      {'tag_name': 'android-v0.5.0', 'prerelease': true, 'assets': [{'name': 'Shutterbox-Android-0.5.0.apk', 'browser_download_url': 'y'}]},
      {'tag_name': 'android-v0.3.5', 'assets': [{'name': 'Shutterbox-Android-0.3.5.apk', 'browser_download_url': 'z'}]},
      {'tag_name': 'android-v0.6.0', 'assets': []},
    ];

    test('picks the newest Android release with an APK, not the Windows one', () {
      final u = Updater.pick(releases, '0.3.0')!;
      expect(u.version, '0.4.0');
      expect(u.apkUrl, 'https://example/0.4.0.apk');
      expect(u.size, 1234);
      expect(Updater.pick(releases, '0.4.0'), isNull);
    });

    test('checks GitHub', () async {
      final settings = Settings();
      final updater = Updater(settings, client: MockClient((req) async {
        expect(req.url.toString(), 'https://api.github.com/repos/adamglassner/Shutterbox/releases?per_page=30');
        return http.Response(jsonEncode(releases), 200);
      }), current: '0.3.0');
      final u = await updater.check(force: true);
      expect(u?.version, '0.4.0');
      expect(settings.lastUpdateCheck, isNotEmpty);
      updater.dismiss();
      expect(updater.dismissed, isTrue);
      expect(settings.skippedVersion, '0.4.0');
    });

    test('the app version matches pubspec.yaml', () {
      final pub = RegExp(r'^version:\s*([\d.]+)', multiLine: true).firstMatch(File('pubspec.yaml').readAsStringSync())![1];
      expect(appVersion, pub);
    });
  });
}
