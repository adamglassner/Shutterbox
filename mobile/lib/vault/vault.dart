import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/foundation.dart';

import '../models.dart';
import 'sbox.dart';

/// vault.json: how to unwrap the key (plain JSON, the same as on Windows).
class VaultInfo {
  int version = 1;
  String kind = 'PIN';
  String salt = '';
  int iterations = Sbox.iterations;
  String wrappedKey = '';
  String recoverySalt = '';
  String recoveryWrappedKey = '';
  int autoLockMinutes = 5;

  Map<String, dynamic> toJson() => {
        'Version': version,
        'Kind': kind,
        'Salt': salt,
        'Iterations': iterations,
        'WrappedKey': wrappedKey,
        'RecoverySalt': recoverySalt,
        'RecoveryWrappedKey': recoveryWrappedKey,
        'AutoLockMinutes': autoLockMinutes,
      };

  static VaultInfo fromJson(Map<String, dynamic> j) => VaultInfo()
    ..version = (j['Version'] as num?)?.toInt() ?? 1
    ..kind = '${j['Kind'] ?? 'PIN'}'
    ..salt = '${j['Salt'] ?? ''}'
    ..iterations = (j['Iterations'] as num?)?.toInt() ?? Sbox.iterations
    ..wrappedKey = '${j['WrappedKey'] ?? ''}'
    ..recoverySalt = '${j['RecoverySalt'] ?? ''}'
    ..recoveryWrappedKey = '${j['RecoveryWrappedKey'] ?? ''}'
    ..autoLockMinutes = (j['AutoLockMinutes'] as num?)?.toInt() ?? 5;
}

/// Dates as the Windows app writes and reads them (System.Text.Json): local time, no offset needed.
String _date(DateTime d) {
  String two(int n) => n.toString().padLeft(2, '0');
  final l = d.toLocal();
  return '${l.year.toString().padLeft(4, '0')}-${two(l.month)}-${two(l.day)}T${two(l.hour)}:${two(l.minute)}:${two(l.second)}';
}

DateTime _parseDate(Object? v) {
  final s = '${v ?? ''}';
  var d = DateTime.tryParse(s);
  if (d == null) {
    // more than 6 decimals (.NET writes 7): keep 6
    final m = RegExp(r'^(.*T\d\d:\d\d:\d\d\.\d{1,6})\d*(.*)$').firstMatch(s);
    if (m != null) d = DateTime.tryParse('${m[1]}${m[2]}');
  }
  return (d ?? DateTime(2000)).toLocal();
}

/// An item in the encrypted index (VaultItem on Windows).
class VaultItem {
  VaultItem({String? id, this.name = '', this.size = 0, DateTime? taken, this.takenSource = 'file', DateTime? added})
      : id = id ?? newId(),
        taken = taken ?? DateTime.now(),
        added = added ?? DateTime.now();
  final String id;
  String name;
  int size;
  DateTime taken;
  String takenSource;
  int width = 0, height = 0;
  double durationSeconds = 0;
  String? make, model;
  double? latitude, longitude;
  int orientation = 1;
  int rotation = 0;
  DateTime added;
  bool hasThumb = false;

  /// 32 hex digits, like Guid.ToString("N").
  static String newId() => Sbox.randomBytes(16).map((b) => b.toRadixString(16).padLeft(2, '0')).join();

  Map<String, dynamic> toJson() => {
        'Id': id,
        'Name': name,
        'Size': size,
        'Taken': _date(taken),
        'TakenSource': takenSource,
        'Width': width,
        'Height': height,
        'DurationSeconds': durationSeconds,
        'Make': make,
        'Model': model,
        'Latitude': latitude,
        'Longitude': longitude,
        'Orientation': orientation,
        'Rotation': rotation,
        'Added': _date(added),
        'HasThumb': hasThumb,
      };

  static VaultItem fromJson(Map<String, dynamic> j) => VaultItem(
        id: '${j['Id']}',
        name: '${j['Name'] ?? ''}',
        size: (j['Size'] as num?)?.toInt() ?? 0,
        taken: _parseDate(j['Taken']),
        takenSource: '${j['TakenSource'] ?? 'file'}',
        added: _parseDate(j['Added']),
      )
        ..width = (j['Width'] as num?)?.toInt() ?? 0
        ..height = (j['Height'] as num?)?.toInt() ?? 0
        ..durationSeconds = (j['DurationSeconds'] as num?)?.toDouble() ?? 0
        ..make = j['Make'] as String?
        ..model = j['Model'] as String?
        ..latitude = (j['Latitude'] as num?)?.toDouble()
        ..longitude = (j['Longitude'] as num?)?.toDouble()
        ..orientation = (j['Orientation'] as num?)?.toInt() ?? 1
        ..rotation = (j['Rotation'] as num?)?.toInt() ?? 0
        ..hasThumb = j['HasThumb'] == true;
}

/// An album inside the Locked album.
class VaultAlbum {
  VaultAlbum({String? id, required this.name, List<String>? itemIds, DateTime? created})
      : id = id ?? VaultItem.newId(),
        itemIds = itemIds ?? [],
        created = created ?? DateTime.now();
  final String id;
  String name;
  final List<String> itemIds;
  final DateTime created;

  Map<String, dynamic> toJson() => {'Id': id, 'Name': name, 'ItemIds': itemIds, 'Created': _date(created)};
  static VaultAlbum fromJson(Map<String, dynamic> j) => VaultAlbum(
        id: '${j['Id']}',
        name: '${j['Name'] ?? ''}',
        itemIds: [for (final x in (j['ItemIds'] as List? ?? const [])) '$x'],
        created: _parseDate(j['Created']),
      );
}

/// Something to put in the Locked album.
class LockSource {
  LockSource({required this.item, required this.file, this.thumbnail});
  final Item item;
  final File file;

  /// A small upright JPEG for the album's grid.
  final Future<Uint8List?> Function()? thumbnail;
}

/// The result of adding to or restoring from the Locked album.
class VaultResult {
  int added = 0, skipped = 0;
  final errors = <String>[];

  /// What's now safely in the Locked album (added and, when moving, checked).
  final secured = <Item>[];
}

/// The Locked album: copies of photos and videos encrypted so they can't be opened outside Shutterbox.
///
/// Exactly the Windows app's design and files: a random 256-bit key encrypts everything with AES-256-GCM; the key is
/// stored only wrapped by your PIN or password (PBKDF2-SHA256, 600,000 rounds) and by the recovery key, and optionally
/// by this phone's fingerprint or face unlock (see LockedKeyStore). The folder is self-contained, so an album made
/// on a PC (copied over USB, or synced) opens here, and this one opens there.
class Vault extends ChangeNotifier {
  Vault(this.folder, {this.tempFolder});

  /// The album's folder.
  String folder;

  /// Where videos are decrypted while they play.
  final String? tempFolder;

  static const portableFolderName = 'Shutterbox Locked album';

  Uint8List? _key;
  List<VaultItem> _items = [];
  List<VaultAlbum> _albums = [];
  List<Item>? _cache;
  int _busy = 0;
  bool _lockWhenDone = false;

  /// Set when you use Shutterbox, for auto-lock.
  DateTime lastActivity = DateTime.now();

  String get _infoFile => '$folder/vault.json';
  String get _indexFile => '$folder/index.sbox';
  String get _itemsDir => '$folder/items';
  String get _thumbsDir => '$folder/thumbs';
  String itemPath(String id) => '$_itemsDir/$id.sbox';
  String thumbPath(String id) => '$_thumbsDir/$id.sbox';

  bool get isSetUp => File(_infoFile).existsSync();
  bool get isUnlocked => _key != null;
  bool get isBusy => _busy > 0;
  int get count => _items.length;
  List<VaultAlbum> get albums => _key == null ? const [] : List.unmodifiable(_albums);

  static bool isVaultFolder(String folder) => File('$folder/vault.json').existsSync() && File('$folder/index.sbox').existsSync();

  VaultInfo? get info {
    try {
      return isSetUp ? VaultInfo.fromJson(jsonDecode(File(_infoFile).readAsStringSync()) as Map<String, dynamic>) : null;
    } catch (_) {
      return null;
    }
  }

  /// Identifies this album (it doesn't change when the PIN does), for keeping a fingerprint copy of its key.
  String? get identity => info?.recoverySalt;

  /// The items as Items for the grid and the viewer (none while locked).
  List<Item> get items => _key == null ? const [] : (_cache ??= [for (final i in _items) toItem(i)]);

  Item toItem(VaultItem i) => Item(
        id: 'locked-${i.id}',
        vaultId: i.id,
        name: i.name,
        taken: i.taken,
        added: i.added,
        width: i.width,
        height: i.height,
        duration: i.durationSeconds,
        latitude: i.latitude,
        longitude: i.longitude,
        make: i.make,
        model: i.model,
        rotation: i.rotation,
        size: i.size,
        orientation: i.orientation,
        takenSource: i.takenSource,
        folder: 'Locked album',
      );

  void _changed() {
    _cache = null;
    notifyListeners();
  }

  // ------------------------------------------------------------------ set up, unlock, lock

  /// A PIN is 4–12 digits; a password is at least 6 characters. Null when it's fine.
  static String? validate(String secret, String kind) => kind == 'PIN'
      ? (secret.length >= 4 && secret.length <= 12 && RegExp(r'^\d+$').hasMatch(secret) ? null : 'A PIN is 4 to 12 digits.')
      : (secret.length >= 6 ? null : 'Use at least 6 characters for your password.');

  /// Sets up the Locked album and unlocks it. Returns the recovery key to show once.
  Future<String> setUp(String secret, String kind) async {
    if (isSetUp) throw StateError('The Locked album is already set up.');
    final key = Sbox.randomBytes(32);
    final recovery = Sbox.newRecoveryKey();
    final salt = Sbox.randomBytes(16), rsalt = Sbox.randomBytes(16);
    final info = VaultInfo()
      ..kind = kind
      ..salt = base64.encode(salt)
      ..recoverySalt = base64.encode(rsalt)
      ..wrappedKey = await Sbox.wrap(key, await Sbox.deriveAsync(secret, salt, Sbox.iterations))
      ..recoveryWrappedKey = await Sbox.wrap(key, await Sbox.deriveAsync(Sbox.normalizeRecovery(recovery), rsalt, Sbox.iterations));
    await Directory(_itemsDir).create(recursive: true);
    await Directory(_thumbsDir).create(recursive: true);
    _key = key;
    _items = [];
    _albums = [];
    await _saveIndex();
    await _saveInfo(info);
    lastActivity = DateTime.now();
    _changed();
    return recovery;
  }

  Future<bool> unlock(String secret) async {
    final i = info;
    if (i == null) return false;
    final key = await Sbox.unwrap(i.wrappedKey, await Sbox.deriveAsync(secret, base64.decode(i.salt), i.iterations));
    return key != null && await _open(key);
  }

  Future<bool> unlockWithRecoveryKey(String recoveryKey) async {
    final i = info;
    if (i == null) return false;
    final key = await Sbox.unwrap(i.recoveryWrappedKey, await Sbox.deriveAsync(Sbox.normalizeRecovery(recoveryKey), base64.decode(i.recoverySalt), i.iterations));
    return key != null && await _open(key);
  }

  /// With a key kept elsewhere (the phone's fingerprint store). False when it doesn't fit.
  Future<bool> unlockWithKey(List<int> key) async => key.length == 32 && await _open(Uint8List.fromList(key));

  Future<bool> _open(Uint8List key) async {
    try {
      final f = File(_indexFile);
      final parsed = await f.exists() ? parseIndex(await Sbox.decryptBytes(await f.readAsBytes(), key)) : (<VaultItem>[], <VaultAlbum>[]);
      _items = parsed.$1;
      _albums = parsed.$2;
      _key = key;
      lastActivity = DateTime.now();
      _changed();
      return true;
    } catch (_) {
      return false;
    }
  }

  /// Reads the decrypted index; accepts version 1 (a plain list of items) too.
  static (List<VaultItem>, List<VaultAlbum>) parseIndex(List<int> json) {
    final data = jsonDecode(utf8.decode(json).trimLeft());
    if (data is List) return ([for (final i in data) VaultItem.fromJson(i as Map<String, dynamic>)], <VaultAlbum>[]);
    final m = data as Map<String, dynamic>;
    final items = [for (final i in (m['Items'] as List? ?? const [])) VaultItem.fromJson(i as Map<String, dynamic>)];
    final albums = [for (final a in (m['Albums'] as List? ?? const [])) VaultAlbum.fromJson(a as Map<String, dynamic>)];
    final ids = {for (final i in items) i.id};
    for (final a in albums) {
      a.itemIds.removeWhere((id) => !ids.contains(id));
    }
    return (items, albums);
  }

  /// Forgets the key: nothing in the Locked album can be seen until it's unlocked again.
  void lock() {
    if (_key == null) return;
    if (isBusy) {
      _lockWhenDone = true;
      return;
    }
    _key!.fillRange(0, _key!.length, 0);
    _key = null;
    _items = [];
    _albums = [];
    cleanTemp();
    _changed();
  }

  /// A copy of the open key (to keep for fingerprint unlock), or null when locked.
  Uint8List? copyKey() => _key == null ? null : Uint8List.fromList(_key!);

  Future<void> changeSecret(String secret, String kind) async {
    final i = info ?? (throw StateError('The Locked album isn\'t set up.'));
    final key = _key ?? (throw StateError('Unlock first.'));
    final salt = Sbox.randomBytes(16);
    i
      ..kind = kind
      ..salt = base64.encode(salt)
      ..iterations = Sbox.iterations
      ..wrappedKey = await Sbox.wrap(key, await Sbox.deriveAsync(secret, salt, Sbox.iterations));
    await _saveInfo(i);
    notifyListeners();
  }

  Future<void> setAutoLock(int minutes) async {
    final i = info;
    if (i == null) return;
    i.autoLockMinutes = minutes;
    await _saveInfo(i);
    notifyListeners();
  }

  /// Deletes the Locked album and everything in it, for good.
  Future<void> deleteAll() async {
    if (_key == null) throw StateError('Unlock first.');
    lock();
    try {
      final d = Directory(folder);
      if (await d.exists()) await d.delete(recursive: true);
    } catch (_) {}
    _changed();
  }

  /// Moves the album into a "Shutterbox Locked album" folder inside [parent] (a synced folder, an SD card…).
  /// Everything is copied and checked before the old copy is deleted. Returns null when done, or why it couldn't.
  Future<String?> moveTo(String parent, {void Function(int done, int total)? progress}) async {
    if (!isSetUp) return 'There\'s no Locked album to move yet.';
    if (isBusy) return 'Wait until Shutterbox finishes adding or restoring.';
    final from = Directory(folder).absolute.path;
    final to = '${Directory(parent).absolute.path}/$portableFolderName';
    if (_same(from, to)) return 'The Locked album is already there.';
    if (_under(to, from)) return 'Choose a folder outside the Locked album.';
    if (isVaultFolder(to)) return 'There\'s already a Locked album in $to. Open it instead, or choose another folder.';
    _busy++;
    try {
      final files = await Directory(from).list(recursive: true).where((e) => e is File && !e.path.endsWith('.tmp')).cast<File>().toList();
      var done = 0;
      for (final f in files) {
        final rel = f.path.substring(from.length + 1);
        final dest = File('$to/$rel');
        await dest.parent.create(recursive: true);
        await f.copy(dest.path);
        if (await dest.length() != await f.length()) throw FileSystemException('$rel didn\'t copy completely');
        progress?.call(++done, files.length);
      }
      folder = to;
      try {
        await Directory(from).delete(recursive: true);
      } catch (_) {}
      return null;
    } catch (e) {
      try {
        if (isVaultFolder(from) && await Directory(to).exists()) await Directory(to).delete(recursive: true);
      } catch (_) {}
      return 'Couldn\'t move it: $e';
    } finally {
      _busy--;
      _changed();
    }
  }

  /// Uses a Locked album folder made on another phone or PC. Returns the folder to use, or throws why not.
  static String findAlbumFolder(String chosen) {
    if (isVaultFolder('$chosen/$portableFolderName')) return '$chosen/$portableFolderName';
    if (isVaultFolder(chosen)) return chosen;
    // also look one level down ("Shutterbox Locked album (2)", a folder copied with another name)
    try {
      for (final d in Directory(chosen).listSync().whereType<Directory>()) {
        if (isVaultFolder(d.path)) return d.path;
      }
    } catch (_) {}
    throw const FileSystemException('That folder doesn\'t have a Shutterbox Locked album in it. Choose the "Shutterbox Locked album" folder.');
  }

  Future<String?> openFolder(String chosen) async {
    final String found;
    try {
      found = findAlbumFolder(chosen);
    } on FileSystemException catch (e) {
      return e.message;
    }
    if (isBusy) return 'Wait until Shutterbox finishes adding or restoring.';
    lock();
    folder = found;
    _changed();
    return null;
  }

  static bool _same(String a, String b) => a.replaceAll(RegExp(r'/+$'), '') == b.replaceAll(RegExp(r'/+$'), '');
  static bool _under(String child, String parent) => child.startsWith('${parent.replaceAll(RegExp(r'/+$'), '')}/');

  // ------------------------------------------------------------------ adding

  /// Copies photos and videos into the Locked album, encrypted. Items already in it (same name and size) are skipped,
  /// but still go into [albumId]. With [verify], each encrypted copy is decrypted again and compared (SHA-256) before
  /// it counts as secured: moving deletes the original only then.
  Future<VaultResult> add(List<LockSource> sources, {String? albumId, bool verify = false, void Function(int done, int total)? progress}) async {
    final key0 = _key ?? (throw StateError('Unlock the Locked album first.'));
    final key = Uint8List.fromList(key0);
    final album = albumId == null ? null : _albums.where((a) => a.id == albumId).firstOrNull;
    final r = VaultResult();
    _busy++;
    try {
      await Directory(_itemsDir).create(recursive: true);
      await Directory(_thumbsDir).create(recursive: true);
      final have = {for (final i in _items) '${i.name.toLowerCase()}|${i.size}'};
      var done = 0;
      for (final s in sources) {
        progress?.call(done, sources.length);
        final p = s.item;
        if (p.locked || !await s.file.exists()) {
          r.skipped++;
          done++;
          continue;
        }
        final size = await s.file.length();
        if (!have.add('${p.name.toLowerCase()}|$size')) {
          final existing = _items.where((i) => i.size == size && i.name.toLowerCase() == p.name.toLowerCase()).firstOrNull;
          if (album != null && existing != null && !album.itemIds.contains(existing.id)) {
            album.itemIds.add(existing.id);
            await _saveIndex(key);
          }
          r.skipped++;
          done++;
          continue;
        }
        final item = VaultItem(name: p.name, size: size, taken: p.taken, takenSource: p.takenSource)
          ..width = p.width
          ..height = p.height
          ..durationSeconds = p.duration
          ..make = p.make
          ..model = p.model
          ..latitude = p.latitude
          ..longitude = p.longitude
          ..orientation = p.orientation
          ..rotation = p.rotation;
        final dest = File(itemPath(item.id));
        final tmp = File('${dest.path}.tmp');
        try {
          final original = await Sbox.encryptFile(s.file, tmp, key);
          await tmp.rename(dest.path);
          if (verify) {
            final back = await Sbox.decryptFile(dest, null, key);
            if (!listEquals(original, back) || !listEquals(original, await Sbox.sha256File(s.file))) {
              throw const FileSystemException('the encrypted copy didn\'t check out, so nothing was changed');
            }
          }
          final thumb = await s.thumbnail?.call();
          if (thumb != null && thumb.isNotEmpty) {
            await File(thumbPath(item.id)).writeAsBytes(await Sbox.encryptBytes(thumb, key));
            item.hasThumb = true;
          }
          if (_key != null) {
            _items.add(item);
            album?.itemIds.add(item.id);
            await _saveIndex(key);
          }
          r.added++;
          r.secured.add(p);
        } catch (e) {
          try {
            if (await tmp.exists()) await tmp.delete();
            if (await dest.exists()) await dest.delete();
          } catch (_) {}
          r.errors.add('${p.name}: $e');
        }
        done++;
      }
      progress?.call(done, sources.length);
    } finally {
      key.fillRange(0, key.length, 0);
      _busy--;
      _changed();
      if (_lockWhenDone && !isBusy) {
        _lockWhenDone = false;
        lock();
      }
    }
    return r;
  }

  // ------------------------------------------------------------------ albums inside it

  VaultAlbum? album(String? id) => id == null || _key == null ? null : _albums.where((a) => a.id == id).firstOrNull;

  String _uniqueName(String name, [VaultAlbum? except]) {
    name = name.trim().isEmpty ? 'New album' : name.trim();
    var n = name;
    for (var i = 2; _albums.any((a) => a != except && a.name.toLowerCase() == n.toLowerCase()); i++) {
      n = '$name ($i)';
    }
    return n;
  }

  Future<VaultAlbum> createAlbum(String name) async {
    if (_key == null) throw StateError('Unlock first.');
    final a = VaultAlbum(name: _uniqueName(name));
    _albums.add(a);
    await _saveIndex();
    _changed();
    return a;
  }

  Future<void> renameAlbum(VaultAlbum a, String name) async {
    if (_key == null) return;
    a.name = _uniqueName(name, a);
    await _saveIndex();
    _changed();
  }

  /// Deletes the album only; its items stay in the Locked album.
  Future<void> deleteAlbum(VaultAlbum a) async {
    if (_key == null) return;
    _albums.remove(a);
    await _saveIndex();
    _changed();
  }

  Future<int> addToAlbum(VaultAlbum a, Iterable<Item> items) async {
    if (_key == null) return 0;
    var n = 0;
    for (final p in items.where((x) => x.locked)) {
      if (!a.itemIds.contains(p.vaultId)) {
        a.itemIds.add(p.vaultId!);
        n++;
      }
    }
    await _saveIndex();
    _changed();
    return n;
  }

  Future<void> removeFromAlbum(VaultAlbum a, Iterable<Item> items) async {
    if (_key == null) return;
    final ids = {for (final p in items) p.vaultId};
    a.itemIds.removeWhere(ids.contains);
    await _saveIndex();
    _changed();
  }

  List<Item> itemsIn(VaultAlbum a) {
    final ids = a.itemIds.toSet();
    return items.where((p) => ids.contains(p.vaultId)).toList();
  }

  List<VaultAlbum> albumsOf(Item p) => p.locked ? _albums.where((a) => a.itemIds.contains(p.vaultId)).toList() : const [];

  // ------------------------------------------------------------------ reading

  /// The whole decrypted file, in memory (photos).
  Future<Uint8List> readBytes(String id) async {
    final key = _key ?? (throw StateError('The Locked album is locked.'));
    return Sbox.decryptBytes(await File(itemPath(id)).readAsBytes(), key);
  }

  Future<Uint8List?> readThumb(String id) async {
    final key = _key;
    final f = File(thumbPath(id));
    if (key == null || !await f.exists()) return null;
    try {
      return await Sbox.decryptBytes(await f.readAsBytes(), key);
    } catch (_) {
      return null;
    }
  }

  /// Decrypts a video to a temporary file so it can play. Deleted when you move on, lock or close Shutterbox.
  Future<File> decryptForPlayback(Item p, {void Function(double)? progress}) async {
    final key = _key ?? (throw StateError('The Locked album is locked.'));
    final dir = Directory(tempFolder ?? '${Directory.systemTemp.path}/LockedPlayback');
    await dir.create(recursive: true);
    final ext = Item.extension(p.name);
    final out = File('${dir.path}/${VaultItem.newId()}${ext.isEmpty ? '' : '.$ext'}');
    await Sbox.decryptFile(File(itemPath(p.vaultId!)), out, key, progress: progress);
    return out;
  }

  void cleanTemp() {
    try {
      final dir = Directory(tempFolder ?? '${Directory.systemTemp.path}/LockedPlayback');
      if (!dir.existsSync()) return;
      for (final f in dir.listSync().whereType<File>()) {
        try {
          f.deleteSync();
        } catch (_) {}
      }
    } catch (_) {}
  }

  /// Decrypts items to ordinary files (in [folder]) and takes them out of the Locked album. [saved] gets each file
  /// once it's written (to add it to the phone's gallery); it returns false when that failed.
  Future<VaultResult> restore(List<Item> list, String folder, {Future<bool> Function(File file, Item item)? saved, void Function(int, int)? progress}) async {
    final key0 = _key ?? (throw StateError('Unlock first.'));
    final key = Uint8List.fromList(key0);
    final r = VaultResult();
    _busy++;
    try {
      await Directory(folder).create(recursive: true);
      var done = 0;
      for (final p in list.where((x) => x.locked)) {
        progress?.call(done++, list.length);
        try {
          final name = p.name.isEmpty ? '${p.vaultId}.jpg' : p.name;
          final dot = name.lastIndexOf('.');
          final stem = dot > 0 ? name.substring(0, dot) : name, ext = dot > 0 ? name.substring(dot) : '';
          var target = File('$folder/$name');
          for (var i = 2; await target.exists(); i++) {
            target = File('$folder/$stem ($i)$ext');
          }
          await Sbox.decryptFile(File(itemPath(p.vaultId!)), target, key);
          try {
            await target.setLastModified(p.taken);
          } catch (_) {}
          if (saved != null && !await saved(target, p)) throw const FileSystemException('couldn\'t add it to your gallery');
          await _removeItem(p.vaultId!, key);
          r.added++;
          r.secured.add(p);
        } catch (e) {
          r.errors.add('${p.name}: $e');
        }
      }
      progress?.call(done, list.length);
    } finally {
      key.fillRange(0, key.length, 0);
      _busy--;
      _changed();
      if (_lockWhenDone && !isBusy) {
        _lockWhenDone = false;
        lock();
      }
    }
    return r;
  }

  /// Deletes items from the Locked album for good.
  Future<void> remove(Iterable<Item> list) async {
    final key = _key ?? (throw StateError('Unlock first.'));
    for (final p in list.where((x) => x.locked).toList()) {
      await _removeItem(p.vaultId!, key);
    }
    _changed();
  }

  Future<void> rotate(Item p, int degrees) async {
    final i = _items.where((x) => x.id == p.vaultId).firstOrNull;
    if (i == null || _key == null) return;
    i.rotation = ((i.rotation + degrees) % 360 + 360) % 360;
    await _saveIndex();
    _changed();
  }

  Future<void> _removeItem(String id, Uint8List key) async {
    _items.removeWhere((i) => i.id == id);
    for (final a in _albums) {
      a.itemIds.remove(id);
    }
    await _saveIndex(key);
    for (final p in [itemPath(id), thumbPath(id)]) {
      try {
        final f = File(p);
        if (await f.exists()) await f.delete();
      } catch (_) {}
    }
  }

  Future<void> _saveIndex([Uint8List? key]) async {
    key ??= _key;
    if (key == null) return;
    final json = utf8.encode(jsonEncode({'Version': 2, 'Items': [for (final i in _items) i.toJson()], 'Albums': [for (final a in _albums) a.toJson()]}));
    final bytes = await Sbox.encryptBytes(json, key);
    final tmp = File('$_indexFile.tmp');
    await tmp.writeAsBytes(bytes, flush: true);
    await tmp.rename(_indexFile);
  }

  Future<void> _saveInfo(VaultInfo i) async {
    await Directory(folder).create(recursive: true);
    await File(_infoFile).writeAsString(const JsonEncoder.withIndent('  ').convert(i.toJson()));
  }
}
