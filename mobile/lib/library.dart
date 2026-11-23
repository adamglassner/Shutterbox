import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:exif/exif.dart';
import 'package:flutter/foundation.dart';

import 'media_source.dart';
import 'models.dart';
import 'settings.dart';

/// A group of items in the timeline: a day, a month or a year.
class Group {
  Group(this.key, this.title, this.subtitle, this.items);
  final String key;
  final String title;
  final String subtitle;
  final List<Item> items;
}

/// Camera details read from a photo's EXIF.
class PhotoDetails {
  String? make, model, lens, exposure, aperture, iso, focal;
  String get camera => [make, model].whereType<String>().where((s) => s.isNotEmpty).join(' ');
  String get settings => [aperture, exposure, iso, focal].whereType<String>().where((s) => s.isNotEmpty).join('  ·  ');
}

/// Your library: the phone's photos and videos plus what Shutterbox adds to them (favorites, captions, tags, rotation,
/// albums, Trash), kept in library.json. The files themselves are never changed.
class Library extends ChangeNotifier {
  Library(this.source, this.settings, {this.file});

  final MediaSource source;
  final Settings settings;

  /// Null in tests: nothing is saved.
  final File? file;

  List<Item> _all = [];
  Map<String, ItemMeta> meta = {};
  List<Album> albums = [];
  bool _scannedOnce = false;

  bool scanning = false;
  bool loaded = false;

  /// False when Android hasn't given Shutterbox access to photos and videos.
  bool? access;
  String? problem;
  int _version = 0;

  /// Changes whenever the library does (for caching views).
  int get version => _version;

  // ------------------------------------------------------------------ loading and saving

  Future<void> load() async {
    final f = file;
    if (f == null || !await f.exists()) return;
    try {
      final j = jsonDecode(await f.readAsString()) as Map<String, dynamic>;
      meta = {for (final e in (j['meta'] as Map<String, dynamic>? ?? const {}).entries) e.key: ItemMeta.fromJson(e.value as Map<String, dynamic>)};
      albums = [for (final a in (j['albums'] as List? ?? const [])) Album.fromJson(a as Map<String, dynamic>)];
      _scannedOnce = j['scanned'] == true;
    } catch (_) {}
  }

  Timer? _saveTimer;
  void _save() {
    if (file == null) return;
    _saveTimer?.cancel();
    _saveTimer = Timer(const Duration(milliseconds: 400), saveNow);
  }

  Future<void> saveNow() async {
    final f = file;
    if (f == null) return;
    try {
      final j = {
        'version': 1,
        'scanned': _scannedOnce,
        'meta': {for (final e in meta.entries) if (!e.value.isEmpty) e.key: e.value.toJson()},
        'albums': [for (final a in albums) a.toJson()],
      };
      await f.parent.create(recursive: true);
      final tmp = File('${f.path}.tmp');
      await tmp.writeAsString(jsonEncode(j));
      await tmp.rename(f.path);
    } catch (_) {}
  }

  void changed({bool save = true}) {
    _version++;
    _views.clear();
    notifyListeners();
    if (save) _save();
  }

  /// Finds the phone's photos and videos (in the folders chosen in Settings). With [ask], asks Android for access
  /// when Shutterbox doesn't have it yet.
  Future<void> scan({bool ask = false}) async {
    if (scanning) return;
    scanning = true;
    problem = null;
    notifyListeners();
    try {
      access = await source.hasAccess() || (ask && await source.requestAccess());
      if (access != true) {
        _all = [];
        return;
      }
      final items = await source.scan(settings.folders?.toSet());
      final now = DateTime.now();
      for (final i in items) {
        final m = meta[i.id];
        if (m == null) {
          meta[i.id] = ItemMeta()..added = _scannedOnce ? now : i.taken;
        } else {
          i.rotation = m.rotation;
          if (m.make != null) i.make = m.make;
          if (m.model != null) i.model = m.model;
          if (m.added == null) m.added = _scannedOnce ? now : i.taken;
        }
        i.added = meta[i.id]!.added ?? i.taken;
      }
      // forget what's no longer on the phone (only when everything was scanned, so an unchecked folder keeps its details)
      if (settings.folders == null) {
        final ids = {for (final i in items) i.id};
        meta.removeWhere((id, m) => !ids.contains(id));
        for (final a in albums) {
          a.itemIds.removeWhere((id) => !ids.contains(id));
        }
      }
      items.sort((a, b) => b.taken.compareTo(a.taken));
      _all = items;
      _scannedOnce = true;
    } catch (e) {
      problem = 'Couldn\'t read your photos ($e).';
    } finally {
      scanning = false;
      loaded = true;
      changed();
    }
    unawaited(_readCameras());
  }

  /// Reads the camera of each photo in the background (for search and details).
  Future<void> _readCameras() async {
    var n = 0;
    for (final i in List.of(_all)) {
      if (i.isVideo) continue;
      final m = meta[i.id];
      if (m == null || m.exifRead) continue;
      final d = await readDetails(i);
      m.exifRead = true;
      if (d != null) {
        m.make = d.make;
        m.model = d.model;
        i.make = d.make;
        i.model = d.model;
      }
      if (++n % 100 == 0) {
        _views.clear();
        _save();
      }
      await Future<void>.delayed(Duration.zero);
    }
    if (n > 0) changed();
  }

  /// Camera details from the photo's EXIF, or null when it has none.
  Future<PhotoDetails?> readDetails(Item item) async {
    if (item.isVideo || item.locked) return null;
    try {
      final f = await source.file(item);
      if (f == null) return null;
      final raf = await f.open();
      final bytes = await raf.read(256 * 1024);
      await raf.close();
      final tags = await readExifFromBytes(bytes);
      if (tags.isEmpty) return null;
      String? t(String k) {
        final v = tags[k]?.printable.trim();
        return v == null || v.isEmpty || v == '0' ? null : v;
      }

      final d = PhotoDetails()
        ..make = t('Image Make')
        ..model = t('Image Model')
        ..lens = t('EXIF LensModel')
        ..iso = t('EXIF ISOSpeedRatings') == null ? null : 'ISO ${t('EXIF ISOSpeedRatings')}';
      final exp = t('EXIF ExposureTime');
      if (exp != null) d.exposure = '${exp}s';
      final fn = _ratio(t('EXIF FNumber'));
      if (fn != null && fn > 0) d.aperture = 'f/${fn.toStringAsFixed(fn < 10 ? 1 : 0)}';
      final fl = _ratio(t('EXIF FocalLength'));
      if (fl != null && fl > 0) d.focal = '${fl.round()} mm';
      // "Canon Canon EOS R6" → "Canon EOS R6"
      if (d.make != null && d.model != null && d.model!.toLowerCase().startsWith(d.make!.toLowerCase())) d.make = null;
      return d;
    } catch (_) {
      return null;
    }
  }

  /// "28/10" or "2.8"
  static double? _ratio(String? v) {
    if (v == null) return null;
    final parts = v.split('/');
    if (parts.length == 2) {
      final n = double.tryParse(parts[0].trim()), d = double.tryParse(parts[1].trim());
      return n == null || d == null || d == 0 ? null : n / d;
    }
    return double.tryParse(v.trim());
  }

  /// Adds items directly (tests and the sample library).
  void setItems(List<Item> items) {
    for (final i in items) {
      final m = meta.putIfAbsent(i.id, () => ItemMeta()..added = i.added);
      i.rotation = m.rotation;
    }
    _all = [...items]..sort((a, b) => b.taken.compareTo(a.taken));
    access = true;
    loaded = true;
    changed(save: false);
  }

  // ------------------------------------------------------------------ views

  final _views = <String, List<Item>>{};
  List<Item> _view(String key, List<Item> Function() make) => _views.putIfAbsent(key, make);

  List<Item> get all => _all;
  ItemMeta metaOf(Item i) => meta.putIfAbsent(i.id, () => ItemMeta());
  bool isFavorite(Item i) => meta[i.id]?.favorite ?? false;
  bool isTrashed(Item i) => meta[i.id]?.trashed != null;
  Item? byId(String id) => _all.where((i) => i.id == id).firstOrNull;

  List<Item> get visible => _view('visible', () => _all.where((i) => !isTrashed(i)).toList());
  List<Item> get photos => _view('photos', () => visible.where((i) => !i.isVideo).toList());
  List<Item> get videos => _view('videos', () => visible.where((i) => i.isVideo).toList());
  List<Item> get favorites => _view('favorites', () => visible.where(isFavorite).toList());

  /// Added in the last 30 days, newest first.
  List<Item> get recent => _view('recent', () {
        final since = DateTime.now().subtract(const Duration(days: 30));
        return visible.where((i) => i.added.isAfter(since)).toList()..sort((a, b) => b.added.compareTo(a.added));
      });

  /// In Trash, most recently deleted first.
  List<Item> get trash => _view('trash', () => _all.where(isTrashed).toList()..sort((a, b) => meta[b.id]!.trashed!.compareTo(meta[a.id]!.trashed!)));

  List<Item> inAlbum(Album a) => _view('album:${a.id}', () {
        final ids = a.itemIds.toSet();
        return visible.where((i) => ids.contains(i.id)).toList();
      });

  List<Item> inFolder(String folderId) => _view('folder:$folderId', () => visible.where((i) => i.folderId == folderId).toList());

  List<Album> albumsOf(Item i) => albums.where((a) => a.itemIds.contains(i.id)).toList();

  Item? coverOf(Album a) {
    final items = inAlbum(a);
    if (items.isEmpty) return null;
    return items.where((i) => i.id == a.coverId).firstOrNull ?? items.first;
  }

  /// The folders that have photos or videos: (id, name, items).
  List<(String, String, List<Item>)> get folders {
    final map = <String, List<Item>>{};
    final names = <String, String>{};
    for (final i in visible) {
      map.putIfAbsent(i.folderId, () => []).add(i);
      names[i.folderId] = i.folder;
    }
    final out = [for (final e in map.entries) (e.key, names[e.key] ?? '', e.value)];
    out.sort((a, b) => a.$2.toLowerCase().compareTo(b.$2.toLowerCase()));
    return out;
  }

  /// Several words narrow it down ("canon portland"). Matches names, folders, tags, captions, cameras, albums,
  /// months and years.
  List<Item> search(String query, [List<Item>? within]) {
    final words = query.toLowerCase().split(RegExp(r'\s+')).where((w) => w.isNotEmpty).toList();
    final list = within ?? visible;
    if (words.isEmpty) return list;
    return list.where((i) {
      final text = searchText(i);
      return words.every(text.contains);
    }).toList();
  }

  String searchText(Item i) {
    final m = meta[i.id];
    final month = monthNames[i.taken.month - 1];
    return [
      i.name,
      i.folder,
      i.camera,
      if (m != null) ...[m.caption, ...m.tags],
      for (final a in albumsOf(i)) a.name,
      month,
      '${i.taken.year}',
      i.isVideo ? 'video' : 'photo',
    ].join(' ').toLowerCase();
  }

  // ------------------------------------------------------------------ editing

  void setFavorite(Iterable<Item> items, bool favorite) {
    for (final i in items) {
      metaOf(i).favorite = favorite;
    }
    changed();
  }

  void toggleFavorite(Item i) => setFavorite([i], !isFavorite(i));

  void setCaption(Item i, String caption) {
    metaOf(i).caption = caption.trim();
    changed();
  }

  void addTag(Item i, String tag) {
    final t = tag.trim();
    final m = metaOf(i);
    if (t.isEmpty || m.tags.any((x) => x.toLowerCase() == t.toLowerCase())) return;
    m.tags.add(t);
    changed();
  }

  void removeTag(Item i, String tag) {
    metaOf(i).tags.remove(tag);
    changed();
  }

  /// Rotates in Shutterbox only (remembered in the library; the file isn't changed).
  void rotate(Iterable<Item> items, int degrees) {
    for (final i in items) {
      final m = metaOf(i);
      m.rotation = ((m.rotation + degrees) % 360 + 360) % 360;
      i.rotation = m.rotation;
    }
    changed();
  }

  void moveToTrash(Iterable<Item> items) {
    final now = DateTime.now();
    for (final i in items) {
      metaOf(i).trashed = now;
    }
    changed();
  }

  void restoreFromTrash(Iterable<Item> items) {
    for (final i in items) {
      metaOf(i).trashed = null;
    }
    changed();
  }

  /// Deletes from the phone (to Android's own trash where it has one). Returns how many are gone.
  Future<int> deleteFromPhone(List<Item> items, {bool toTrash = true}) async {
    final gone = (await source.delete(items, toTrash: toTrash)).toSet();
    _all.removeWhere((i) => gone.contains(i.id));
    for (final id in gone) {
      meta.remove(id);
    }
    for (final a in albums) {
      a.itemIds.removeWhere(gone.contains);
    }
    changed();
    return gone.length;
  }

  Future<int> emptyTrash() => deleteFromPhone(trash);

  // ------------------------------------------------------------------ albums

  String _uniqueName(String name, [Album? except]) {
    name = name.trim().isEmpty ? 'New album' : name.trim();
    var n = name;
    for (var i = 2; albums.any((a) => a != except && a.name.toLowerCase() == n.toLowerCase()); i++) {
      n = '$name ($i)';
    }
    return n;
  }

  Album createAlbum(String name, [Iterable<Item> items = const []]) {
    final a = Album(id: '${DateTime.now().microsecondsSinceEpoch}${Random().nextInt(1000)}', name: _uniqueName(name), itemIds: [for (final i in items) i.id]);
    albums.add(a);
    changed();
    return a;
  }

  void renameAlbum(Album a, String name) {
    a.name = _uniqueName(name, a);
    changed();
  }

  /// Deletes the album only; its photos stay in your library.
  void deleteAlbum(Album a) {
    albums.remove(a);
    changed();
  }

  int addToAlbum(Album a, Iterable<Item> items) {
    var n = 0;
    for (final i in items.where((x) => !x.locked)) {
      if (!a.itemIds.contains(i.id)) {
        a.itemIds.add(i.id);
        n++;
      }
    }
    changed();
    return n;
  }

  void removeFromAlbum(Album a, Iterable<Item> items) {
    final ids = {for (final i in items) i.id};
    a.itemIds.removeWhere(ids.contains);
    if (ids.contains(a.coverId)) a.coverId = null;
    changed();
  }

  void setCover(Album a, Item i) {
    a.coverId = i.id;
    changed();
  }
}

// ------------------------------------------------------------------ grouping

/// Groups a newest-first list into days, months or years. A day's subtitle is the folder (or folders) it came from.
List<Group> groupItems(List<Item> items, String by, {bool newestFirst = true}) {
  final groups = <Group>[];
  String keyOf(DateTime d) => switch (by) {
        'year' => '${d.year}',
        'month' => '${d.year}-${d.month}',
        _ => '${d.year}-${d.month}-${d.day}',
      };
  String titleOf(DateTime d) {
    final now = DateTime.now();
    return switch (by) {
      'year' => '${d.year}',
      'month' => '${monthNames[d.month - 1]} ${d.year}',
      _ => d.year == now.year && d.month == now.month && d.day == now.day
          ? 'Today'
          : (() {
              final y = now.subtract(const Duration(days: 1));
              return d.year == y.year && d.month == y.month && d.day == y.day ? 'Yesterday' : formatDay(d);
            })(),
    };
  }

  List<Item>? current;
  String? currentKey;
  for (final i in items) {
    final k = keyOf(i.taken);
    if (k != currentKey) {
      current = [];
      currentKey = k;
      groups.add(Group(k, titleOf(i.taken), '', current));
    }
    current!.add(i);
  }
  return [
    for (final g in groups) Group(g.key, g.title, _subtitle(g.items, by), g.items),
  ];
}

String _subtitle(List<Item> items, String by) {
  final counts = <String, int>{};
  for (final i in items) {
    if (i.folder.isNotEmpty) counts[i.folder] = (counts[i.folder] ?? 0) + 1;
  }
  final photos = items.where((i) => !i.isVideo).length, videos = items.length - photos;
  final what = [
    if (photos > 0) '$photos photo${photos == 1 ? '' : 's'}',
    if (videos > 0) '$videos video${videos == 1 ? '' : 's'}',
  ].join(', ');
  if (by != 'day' || counts.isEmpty) return what;
  final folders = counts.entries.toList()..sort((a, b) => b.value.compareTo(a.value));
  final where = folders.length == 1 ? folders.first.key : '${folders.first.key} +${folders.length - 1}';
  return '$where  ·  $what';
}
