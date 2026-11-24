import 'dart:async';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:photo_manager/photo_manager.dart';

import 'bytes_image.dart';

import 'models.dart';

/// Where the library's photos and videos come from: the phone's gallery (MediaStore), or sample pictures in tests.
abstract class MediaSource {
  /// Asks for access to photos and videos. True when Shutterbox has it.
  Future<bool> requestAccess();

  Future<bool> hasAccess();

  /// The phone's photo and video folders.
  Future<List<DeviceFolder>> folders();

  /// Everything in [only] (all folders when null).
  Future<List<Item>> scan(Set<String>? only);

  ImageProvider thumbnail(Item item, int pixels);

  /// For the viewer: big enough to zoom into.
  ImageProvider full(Item item);

  /// The original file (to share, open in another app, play, read details or lock).
  Future<File?> file(Item item);

  /// A small upright JPEG (for the Locked album's grid).
  Future<Uint8List?> thumbnailJpeg(Item item);

  /// An upright JPEG at most 1280 pixels on its long side, for finding faces and things in it.
  Future<Uint8List?> analysisJpeg(Item item);

  /// Deletes for good, or to Android's own trash where it has one. Returns the ids that are gone.
  Future<List<String>> delete(List<Item> items, {bool toTrash = true});

  /// Adds a file to the gallery in [relativePath] (DCIM/Shutterbox/2026/2026-10-14). False when it couldn't.
  Future<bool> addToGallery(File file, String name, String relativePath, {DateTime? taken});

  /// Called when the gallery changes (photos taken, deleted…).
  void watch(VoidCallback changed);

  /// GPS position, when the photo has one.
  Future<(double, double)?> location(Item item);

  /// Drops cached thumbnails.
  Future<void> clearCache();

  /// Android's settings page for Shutterbox (to allow access after saying no).
  Future<void> openSettings();
}

/// The phone's gallery, through photo_manager.
class DeviceSource implements MediaSource {
  @override
  Future<bool> requestAccess() async {
    final ps = await PhotoManager.requestPermissionExtend();
    return ps.hasAccess;
  }

  @override
  Future<bool> hasAccess() async {
    try {
      final ps = await PhotoManager.getPermissionState(requestOption: const PermissionRequestOption());
      return ps.hasAccess;
    } catch (_) {
      return false;
    }
  }

  Future<List<AssetPathEntity>> _paths() => PhotoManager.getAssetPathList(type: RequestType.common, hasAll: false);

  @override
  Future<List<DeviceFolder>> folders() async {
    final out = <DeviceFolder>[];
    for (final p in await _paths()) {
      out.add(DeviceFolder(p.id, p.name, await p.assetCountAsync));
    }
    out.sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
    return out;
  }

  @override
  Future<List<Item>> scan(Set<String>? only) async {
    final items = <Item>[];
    final seen = <String>{};
    for (final p in await _paths()) {
      if (only != null && !only.contains(p.id)) continue;
      final count = await p.assetCountAsync;
      for (var start = 0; start < count; start += 500) {
        final list = await p.getAssetListRange(start: start, end: min(start + 500, count));
        for (final e in list) {
          if (e.type != AssetType.image && e.type != AssetType.video) continue;
          if (!seen.add(e.id)) continue;
          items.add(_toItem(e, p));
        }
      }
    }
    return items;
  }

  Item _toItem(AssetEntity e, AssetPathEntity p) {
    final name = (e.title == null || e.title!.isEmpty) ? '${e.id}.${e.type == AssetType.video ? 'mp4' : 'jpg'}' : e.title!;
    var taken = e.createDateTime;
    var source = 'file';
    if (taken.year < 1980) {
      final fromName = dateFromName(name);
      if (fromName != null) {
        taken = fromName;
        source = 'name';
      } else {
        taken = e.modifiedDateTime;
      }
    }
    final lat = e.latLng?.latitude, lng = e.latLng?.longitude;
    return Item(
      id: e.id,
      name: name,
      taken: taken,
      takenSource: source,
      folder: p.name,
      folderId: p.id,
      width: e.orientatedWidth,
      height: e.orientatedHeight,
      duration: e.duration.toDouble(),
      isVideo: e.type == AssetType.video,
      latitude: (lat == null || lat == 0) ? null : lat,
      longitude: (lng == null || lng == 0) ? null : lng,
      orientation: e.orientation,
      source: e,
    );
  }

  AssetEntity _e(Item i) => i.source as AssetEntity;

  @override
  ImageProvider thumbnail(Item item, int pixels) => BytesImage('thumb:${item.id}:$pixels',
      () => _e(item).thumbnailDataWithSize(ThumbnailSize.square(pixels), format: ThumbnailFormat.jpeg, quality: 88));

  @override
  ImageProvider full(Item item) => BytesImage('full:${item.id}',
      () => _e(item).thumbnailDataWithSize(const ThumbnailSize(2560, 2560), format: ThumbnailFormat.jpeg, quality: 92));

  @override
  Future<File?> file(Item item) async {
    final e = _e(item);
    return await e.originFile ?? await e.file;
  }

  @override
  Future<Uint8List?> thumbnailJpeg(Item item) =>
      _e(item).thumbnailDataWithSize(const ThumbnailSize(400, 400), format: ThumbnailFormat.jpeg, quality: 85);

  @override
  Future<Uint8List?> analysisJpeg(Item item) =>
      _e(item).thumbnailDataWithSize(const ThumbnailSize(1280, 1280), format: ThumbnailFormat.jpeg, quality: 90);

  @override
  Future<List<String>> delete(List<Item> items, {bool toTrash = true}) async {
    if (items.isEmpty) return const [];
    if (toTrash) {
      try {
        final gone = await PhotoManager.editor.android.moveToTrash([for (final i in items) _e(i)]);
        return gone;
      } catch (_) {
        // older Android: delete
      }
    }
    return PhotoManager.editor.deleteWithIds([for (final i in items) i.id]);
  }

  @override
  Future<bool> addToGallery(File file, String name, String relativePath, {DateTime? taken}) async {
    try {
      if (Item.isVideoName(name)) {
        await PhotoManager.editor.saveVideo(file, title: name, relativePath: relativePath, creationDate: taken);
      } else {
        await PhotoManager.editor.saveImageWithPath(file.path, title: name, relativePath: relativePath, creationDate: taken);
      }
      return true;
    } catch (_) {
      return false;
    }
  }

  Timer? _debounce;

  @override
  void watch(VoidCallback changed) {
    PhotoManager.addChangeCallback((MethodCall call) {
      _debounce?.cancel();
      _debounce = Timer(const Duration(seconds: 2), changed);
    });
    PhotoManager.startChangeNotify();
  }

  @override
  Future<(double, double)?> location(Item item) async {
    if (item.latitude != null && item.longitude != null) return (item.latitude!, item.longitude!);
    try {
      final ll = await _e(item).latlngAsync();
      if (ll == null || (ll.latitude == 0 && ll.longitude == 0)) return null;
      return (ll.latitude, ll.longitude);
    } catch (_) {
      return null;
    }
  }

  @override
  Future<void> clearCache() async {
    try {
      await PhotoManager.clearFileCache();
    } catch (_) {}
  }

  @override
  Future<void> openSettings() => PhotoManager.openSetting();
}
