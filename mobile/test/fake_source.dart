import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/painting.dart';
import 'package:shutterbox/media_source.dart';
import 'package:shutterbox/models.dart';

/// Sample photos (test/fixtures/photos) standing in for the phone's gallery.
class FakeSource implements MediaSource {
  FakeSource() {
    final files = Directory('test/fixtures/photos').listSync().whereType<File>().toList()..sort((a, b) => a.path.compareTo(b.path));
    for (final f in files) {
      bytes[f.uri.pathSegments.last] = f.readAsBytesSync();
    }
  }

  final bytes = <String, Uint8List>{};
  final deleted = <String>[];
  final added = <String>[];
  final _images = <String, MemoryImage>{};

  /// A library of [count] items over the last weeks, every fifth a video.
  List<Item> sample({int count = 40, DateTime? now}) {
    final names = bytes.keys.toList();
    final t = now ?? DateTime(2026, 11, 22, 16, 30);
    final folders = ['Camera', 'Camera', 'Camera', 'Screenshots', 'WhatsApp Images'];
    return [
      for (var i = 0; i < count; i++)
        Item(
          id: 'p$i',
          name: i % 5 == 3 ? 'VID_2026${(11 - i ~/ 12).toString().padLeft(2, '0')}${(20 - i % 12).toString().padLeft(2, '0')}_1842$i.mp4' : 'IMG_${4100 + i}.jpg',
          taken: t.subtract(Duration(hours: i * 9 + (i % 3) * 2)),
          folder: folders[i % folders.length],
          folderId: {'Camera': 'f0', 'Screenshots': 'f3', 'WhatsApp Images': 'f4'}[folders[i % folders.length]]!,
          width: 4032,
          height: 3024,
          duration: i % 5 == 3 ? 14.0 + i : 0,
          make: i % 2 == 0 ? 'Google' : 'Canon',
          model: i % 2 == 0 ? 'Pixel 9 Pro' : 'EOS R6',
          source: names[i % names.length],
        ),
    ];
  }

  MemoryImage imageFor(Item item) => _images.putIfAbsent('${item.source}', () => MemoryImage(bytes['${item.source}']!));

  @override
  Future<bool> requestAccess() async => true;
  @override
  Future<bool> hasAccess() async => true;
  @override
  Future<List<DeviceFolder>> folders() async => [DeviceFolder('f0', 'Camera', 24), DeviceFolder('f3', 'Screenshots', 8), DeviceFolder('f4', 'WhatsApp Images', 8)];
  @override
  Future<List<Item>> scan(Set<String>? only) async => sample();
  @override
  ImageProvider thumbnail(Item item, int pixels) => imageFor(item);
  @override
  ImageProvider full(Item item) => imageFor(item);
  @override
  Future<File?> file(Item item) async {
    final f = File('${Directory.systemTemp.path}/sb-fake-${item.id}-${item.name}');
    if (!f.existsSync()) f.writeAsBytesSync(bytes['${item.source}']!);
    return f;
  }

  @override
  Future<Uint8List?> thumbnailJpeg(Item item) async => bytes['${item.source}'];
  @override
  Future<Uint8List?> analysisJpeg(Item item) async => bytes['${item.source}'];
  @override
  Future<List<String>> delete(List<Item> items, {bool toTrash = true}) async {
    deleted.addAll(items.map((i) => i.id));
    return [for (final i in items) i.id];
  }

  @override
  Future<bool> addToGallery(File file, String name, String relativePath, {DateTime? taken}) async {
    added.add('$relativePath/$name');
    return true;
  }

  @override
  void watch(void Function() changed) {}
  @override
  Future<(double, double)?> location(Item item) async => item.id == 'p0' ? (45.5231, -122.6765) : null;
  @override
  Future<void> clearCache() async {}
  @override
  Future<void> openSettings() async {}
}
