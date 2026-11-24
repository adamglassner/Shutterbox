import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/foundation.dart';

import '../models.dart';
import 'intruder.dart';
import 'sbox.dart';

/// vault.json: how to unwrap the key (plain JSON, the same as on Windows). See docs/locked-album-format.md.
class VaultInfo {
  int version = 1;
  String kind = 'PIN';
  String salt = '';
  int iterations = Sbox.iterations;
  String wrappedKey = '';
  String recoverySalt = '';
  String recoveryWrappedKey = '';
  int autoLockMinutes = 5;

  /// Never changes (since 0.4.0); albums from 0.3.0 get one, the first recovery salt.
  String? id;

  /// Other properties a newer version wrote: kept as they are.
  Map<String, dynamic> extra = {};

  Map<String, dynamic> toJson() => {
        ...extra,
        'Version': version,
        'Kind': kind,
        'Salt': salt,
        'Iterations': iterations,
        'WrappedKey': wrappedKey,
        'RecoverySalt': recoverySalt,
        'RecoveryWrappedKey': recoveryWrappedKey,
        'AutoLockMinutes': autoLockMinutes,
        if (id != null) 'Id': id,
      };

  static const _known = {'Version', 'Kind', 'Salt', 'Iterations', 'WrappedKey', 'RecoverySalt', 'RecoveryWrappedKey', 'AutoLockMinutes', 'Id'};

  static VaultInfo fromJson(Map<String, dynamic> j) => VaultInfo()
    ..version = (j['Version'] as num?)?.toInt() ?? 1
    ..kind = '${j['Kind'] ?? 'PIN'}'
    ..salt = '${j['Salt'] ?? ''}'
    ..iterations = (j['Iterations'] as num?)?.toInt() ?? Sbox.iterations
    ..wrappedKey = '${j['WrappedKey'] ?? ''}'
    ..recoverySalt = '${j['RecoverySalt'] ?? ''}'
    ..recoveryWrappedKey = '${j['RecoveryWrappedKey'] ?? ''}'
    ..autoLockMinutes = (j['AutoLockMinutes'] as num?)?.toInt() ?? 5
    ..id = j['Id'] as String?
    ..extra = {for (final e in j.entries) if (!_known.contains(e.key)) e.key: e.value};
}

String _two(int n) => n.toString().padLeft(2, '0');

/// Dates as the Windows app writes and reads them (System.Text.Json): local time, no offset needed.
String _date(DateTime d) {
  final l = d.toLocal();
  return '${l.year.toString().padLeft(4, '0')}-${_two(l.month)}-${_two(l.day)}T${_two(l.hour)}:${_two(l.minute)}:${_two(l.second)}';
}

/// UTC, with Z.
String _utc(DateTime d) {
  final u = d.toUtc();
  return '${u.year.toString().padLeft(4, '0')}-${_two(u.month)}-${_two(u.day)}T${_two(u.hour)}:${_two(u.minute)}:${_two(u.second)}.${u.millisecond.toString().padLeft(3, '0')}Z';
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

/// An item: in the old index (VaultItem on Windows) and, since 0.4.0, its own record file with more details.
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

  // since 0.4.0
  String caption = '';
  List<String> tags = [];
  bool favorite = false;
  List<String> albumIds = [];
  DateTime modified = DateTime.fromMillisecondsSinceEpoch(0, isUtc: true);
  bool deleted = false;

  /// Not saved: the protected album it's in (null: the main album).
  String? protectedIn;

  /// 32 hex digits, like Guid.ToString("N").
  static String newId() => Sbox.randomBytes(16).map((b) => b.toRadixString(16).padLeft(2, '0')).join();

  /// As in index.sbox.
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

  /// As in records/<id>.sbox.
  Map<String, dynamic> toRecord() => {
        ...toJson(),
        'Caption': caption,
        'Tags': tags,
        'Favorite': favorite,
        'AlbumIds': albumIds,
        'Modified': _utc(modified),
        'Deleted': deleted,
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
        ..hasThumb = j['HasThumb'] == true
        ..caption = '${j['Caption'] ?? ''}'
        ..tags = [for (final t in (j['Tags'] as List? ?? const [])) '$t']
        ..favorite = j['Favorite'] == true
        ..albumIds = [for (final a in (j['AlbumIds'] as List? ?? const [])) '$a']
        ..modified = j['Modified'] == null ? DateTime.fromMillisecondsSinceEpoch(0, isUtc: true) : _parseDate(j['Modified']).toUtc()
        ..deleted = j['Deleted'] == true;
}

/// How an album with its own PIN or password unwraps its key.
class AlbumProtection {
  String kind = 'PIN';
  String salt = '';
  int iterations = Sbox.iterations;
  String wrappedKey = '';

  Map<String, dynamic> toJson() => {'Kind': kind, 'Salt': salt, 'Iterations': iterations, 'WrappedKey': wrappedKey};
  static AlbumProtection fromJson(Map<String, dynamic> j) => AlbumProtection()
    ..kind = '${j['Kind'] ?? 'PIN'}'
    ..salt = '${j['Salt'] ?? ''}'
    ..iterations = (j['Iterations'] as num?)?.toInt() ?? Sbox.iterations
    ..wrappedKey = '${j['WrappedKey'] ?? ''}';
}

/// An album inside the Locked album.
class VaultAlbum {
  VaultAlbum({String? id, required this.name, List<String>? itemIds, DateTime? created})
      : id = id ?? VaultItem.newId(),
        itemIds = itemIds ?? [],
        created = created ?? DateTime.now();
  final String id;
  String name;

  /// Only from an old index (0.3.0); since 0.4.0 each item lists its albums.
  final List<String> itemIds;
  final DateTime created;
  DateTime modified = DateTime.fromMillisecondsSinceEpoch(0, isUtc: true);
  bool deleted = false;
  String? coverId;

  /// Set when it has its own PIN or password.
  AlbumProtection? protection;

  // not saved: an open protected album
  Uint8List? _key;
  Map<String, VaultItem> _records = {};

  bool get isProtected => protection != null;
  bool get isOpen => _key != null;

  /// As in index.sbox (version 2).
  Map<String, dynamic> toJson() => {'Id': id, 'Name': name, 'ItemIds': itemIds, 'Created': _date(created)};

  /// As in albums/<id>.sbox.
  Map<String, dynamic> toRecord() => {
        'Id': id,
        'Name': name,
        'Created': _date(created),
        'Modified': _utc(modified),
        'Deleted': deleted,
        'CoverId': coverId,
        'Protected': protection?.toJson(),
      };

  static VaultAlbum fromJson(Map<String, dynamic> j) => VaultAlbum(
        id: '${j['Id']}',
        name: '${j['Name'] ?? ''}',
        itemIds: [for (final x in (j['ItemIds'] as List? ?? const [])) '$x'],
        created: _parseDate(j['Created']),
      )
        ..modified = j['Modified'] == null ? DateTime.fromMillisecondsSinceEpoch(0, isUtc: true) : _parseDate(j['Modified']).toUtc()
        ..deleted = j['Deleted'] == true
        ..coverId = j['CoverId'] as String?
        ..protection = j['Protected'] is Map ? AlbumProtection.fromJson((j['Protected'] as Map).cast<String, dynamic>()) : null;
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

/// A wrong PIN, password or recovery key, on some device.
class FailedAttempt {
  FailedAttempt(this.time, this.what, this.device);
  final DateTime time;
  final String what, device;
}

/// A photo taken after a wrong guess.
class IntruderPhoto {
  IntruderPhoto(this.file, this.time);
  final File file;
  final DateTime? time;
}

/// What "Check the album" found.
class CheckResult {
  int checked = 0;
  final problems = <String>[];
}

/// The Locked album: copies of photos and videos encrypted so they can't be opened outside Shutterbox.
///
/// The Windows app's design and files (docs/locked-album-format.md): a random 256-bit key encrypts everything with
/// AES-256-GCM; the key is stored only wrapped by your PIN or password (PBKDF2-SHA256, 600,000 rounds) and by the
/// recovery key, and optionally by this phone's fingerprint or face unlock. Each item's details are in their own
/// record file, so a PC and a phone can share the album through a cloud folder without overwriting each other.
class Vault extends ChangeNotifier {
  Vault(this.folder, {this.tempFolder, this.deviceId = 'device', this.deviceName = 'This device'});

  /// The album's folder.
  String folder;

  /// Where videos are decrypted while they play, and photos while they're shared.
  final String? tempFolder;

  /// For the wrong-guess log.
  final String deviceId, deviceName;

  static const portableFolderName = 'Shutterbox Locked album';

  Uint8List? _key;
  Map<String, VaultItem> _records = {};
  Map<String, VaultAlbum> _albums = {};
  List<Item>? _cache;
  int _busy = 0;
  bool _lockWhenDone = false;

  /// Set when you use Shutterbox, for auto-lock.
  DateTime lastActivity = DateTime.now();

  /// The failed attempts shown after the last unlock (since the one before it).
  List<FailedAttempt> lastUnlockWarnings = const [];

  String get _infoFile => '$folder/vault.json';
  String get _indexFile => '$folder/index.sbox';
  String _base(String? protectedIn) => protectedIn == null ? folder : '$folder/protected/$protectedIn';
  String itemPath(String id, [String? protectedIn]) => '${_base(protectedIn)}/items/$id.sbox';
  String thumbPath(String id, [String? protectedIn]) => '${_base(protectedIn)}/thumbs/$id.sbox';
  String _recordPath(String id, [String? protectedIn]) => '${_base(protectedIn)}/records/$id.sbox';

  bool get isSetUp => File(_infoFile).existsSync();
  bool get isUnlocked => _key != null;
  bool get isBusy => _busy > 0;
  int get count => items.length;

  /// The albums inside it, by name (none while locked).
  List<VaultAlbum> get albums {
    if (_key == null) return const [];
    return _albums.values.where((a) => !a.deleted).toList()..sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
  }

  static bool isVaultFolder(String folder) => File('$folder/vault.json').existsSync() && File('$folder/index.sbox').existsSync();

  VaultInfo? get info {
    try {
      return isSetUp ? VaultInfo.fromJson(jsonDecode(File(_infoFile).readAsStringSync()) as Map<String, dynamic>) : null;
    } catch (_) {
      return null;
    }
  }

  /// Identifies this album (it doesn't change when the PIN or the recovery key does), for the fingerprint copy of its key.
  String? get identity {
    final i = info;
    return i?.id ?? i?.recoverySalt;
  }

  List<VaultItem> get _live => _records.values.where((r) => !r.deleted).toList();

  /// The main album's items as Items for the grid and the viewer (none while locked; protected albums' are apart).
  List<Item> get items => _key == null ? const [] : (_cache ??= [for (final i in _live) toItem(i)]);

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
        caption: i.caption,
        tags: List.of(i.tags),
        favorite: i.favorite,
        protectedIn: i.protectedIn,
      );

  VaultItem? _record(Item p) {
    if (p.vaultId == null) return null;
    if (p.protectedIn != null) return _albums[p.protectedIn]?._records[p.vaultId];
    return _records[p.vaultId];
  }

  Uint8List _keyFor(String? protectedIn) {
    final k = protectedIn == null ? _key : _albums[protectedIn]?._key;
    if (k == null) throw StateError(protectedIn == null ? 'The Locked album is locked.' : 'That album is locked.');
    return k;
  }

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
      ..id = VaultItem.newId()
      ..salt = base64.encode(salt)
      ..recoverySalt = base64.encode(rsalt)
      ..wrappedKey = await Sbox.wrap(key, await Sbox.deriveAsync(secret, salt, Sbox.iterations))
      ..recoveryWrappedKey = await Sbox.wrap(key, await Sbox.deriveAsync(Sbox.normalizeRecovery(recovery), rsalt, Sbox.iterations));
    for (final d in ['items', 'thumbs', 'records', 'albums']) {
      await Directory('$folder/$d').create(recursive: true);
    }
    _key = key;
    _records = {};
    _albums = {};
    await _saveIndex();
    await _saveInfo(info);
    lastActivity = DateTime.now();
    await _noteUnlock();
    _changed();
    return recovery;
  }

  /// Unlocks with the PIN or password. A wrong one is logged (see [failedAttempts]) unless [log] is false.
  Future<bool> unlock(String secret, {bool log = true}) async {
    final i = info;
    if (i == null) return false;
    final key = await Sbox.unwrap(i.wrappedKey, await Sbox.deriveAsync(secret, base64.decode(i.salt), i.iterations));
    final ok = key != null && await _open(key);
    if (!ok && log) await recordFailure(i.kind == 'Password' ? 'Password' : 'PIN');
    return ok;
  }

  Future<bool> unlockWithRecoveryKey(String recoveryKey) async {
    final i = info;
    if (i == null) return false;
    final key = await Sbox.unwrap(i.recoveryWrappedKey, await Sbox.deriveAsync(Sbox.normalizeRecovery(recoveryKey), base64.decode(i.recoverySalt), i.iterations));
    final ok = key != null && await _open(key);
    if (!ok) await recordFailure('Recovery key');
    return ok;
  }

  /// With a key kept elsewhere (the phone's fingerprint store). False when it doesn't fit.
  Future<bool> unlockWithKey(List<int> key) async => key.length == 32 && await _open(Uint8List.fromList(key));

  Future<bool> _open(Uint8List key) async {
    try {
      final loaded = await _load(key);
      _records = loaded.$1;
      _albums = loaded.$2;
      _key = key;
      lastActivity = DateTime.now();
      await _ensureId();
      if (loaded.$3) await _tidy();
      lastUnlockWarnings = await failedSinceLastUnlock();
      await _noteUnlock();
      _changed();
      return true;
    } catch (_) {
      return false;
    }
  }

  /// Reads what other devices changed (through a cloud folder) while it's unlocked.
  Future<void> reload() async {
    final key = _key;
    if (key == null || isBusy) return;
    try {
      final loaded = await _load(key);
      final open = {for (final a in _albums.values) if (a.isOpen) a.id: a._key!};
      _records = loaded.$1;
      _albums = loaded.$2;
      for (final e in open.entries) {
        final a = _albums[e.key];
        if (a != null && a.isProtected) {
          a._key = e.value;
          a._records = await _readRecords('${_base(a.id)}/records', e.value, a.id);
        }
      }
      if (loaded.$3) await _tidy();
      _changed();
    } catch (_) {}
  }

  /// Records and albums, merged with what's only in the old index. The bool says files need tidying.
  Future<(Map<String, VaultItem>, Map<String, VaultAlbum>, bool)> _load(Uint8List key) async {
    final records = await _readRecords('$folder/records', key, null);
    final albums = <String, VaultAlbum>{};
    var untidy = _strays;
    final albumDir = Directory('$folder/albums');
    if (await albumDir.exists()) {
      await for (final f in albumDir.list()) {
        if (f is! File || !f.path.endsWith('.sbox')) continue;
        try {
          final a = VaultAlbum.fromJson(jsonDecode(utf8.decode(await Sbox.decryptBytes(await f.readAsBytes(), key))) as Map<String, dynamic>);
          if (f.uri.pathSegments.last != '${a.id}.sbox') untidy = true;
          final have = albums[a.id];
          if (have == null || a.modified.isAfter(have.modified)) albums[a.id] = a;
        } catch (_) {}
      }
    }
    // the old index: items added by Shutterbox 0.2.0 or 0.3.0
    final index = File(_indexFile);
    if (await index.exists()) {
      final parsed = parseIndex(await Sbox.decryptBytes(await index.readAsBytes(), key));
      for (final a in parsed.$2) {
        if (!albums.containsKey(a.id)) {
          albums[a.id] = a..modified = DateTime.now().toUtc();
          untidy = true;
        }
      }
      for (final i in parsed.$1) {
        if (records.containsKey(i.id) || !File(itemPath(i.id)).existsSync()) continue;
        i.albumIds = [for (final a in parsed.$2) if (a.itemIds.contains(i.id)) a.id];
        i.modified = DateTime.now().toUtc();
        records[i.id] = i;
        untidy = true;
      }
    }
    return (records, albums, untidy);
  }

  bool _strays = false;

  /// Reads a records folder. Conflict copies from sync apps count too: the latest Modified wins.
  Future<Map<String, VaultItem>> _readRecords(String dir, Uint8List key, String? protectedIn) async {
    final out = <String, VaultItem>{};
    _strays = false;
    final d = Directory(dir);
    if (!await d.exists()) return out;
    await for (final f in d.list()) {
      if (f is! File || !f.path.endsWith('.sbox')) continue;
      try {
        final r = VaultItem.fromJson(jsonDecode(utf8.decode(await Sbox.decryptBytes(await f.readAsBytes(), key))) as Map<String, dynamic>)
          ..protectedIn = protectedIn;
        if (f.uri.pathSegments.last != '${r.id}.sbox') _strays = true;
        final have = out[r.id];
        if (have == null || r.modified.isAfter(have.modified)) out[r.id] = r;
      } catch (_) {}
    }
    return out;
  }

  /// Writes the merged records and albums under their own names and removes conflict copies.
  Future<void> _tidy() async {
    final key = _key;
    if (key == null) return;
    for (final r in _records.values) {
      await _writeRecord(r, touch: false);
    }
    for (final a in _albums.values) {
      await _writeAlbum(a, touch: false);
    }
    for (final dir in ['$folder/records', '$folder/albums']) {
      final d = Directory(dir);
      if (!await d.exists()) continue;
      await for (final f in d.list()) {
        final name = f.uri.pathSegments.last;
        if (f is File && name.endsWith('.sbox') && !RegExp(r'^[0-9a-f]{32}\.sbox$').hasMatch(name)) {
          try {
            await f.delete();
          } catch (_) {}
        }
      }
    }
    await _saveIndex();
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

  /// Forgets the key (and any open protected album's): nothing can be seen until it's unlocked again.
  void lock() {
    if (_key == null) return;
    if (isBusy) {
      _lockWhenDone = true;
      return;
    }
    _key!.fillRange(0, _key!.length, 0);
    _key = null;
    for (final a in _albums.values) {
      a._key?.fillRange(0, a._key!.length, 0);
      a._key = null;
      a._records = {};
    }
    _records = {};
    _albums = {};
    cleanTemp();
    _changed();
  }

  /// A copy of the open key (to keep for fingerprint unlock), or null when locked.
  Uint8List? copyKey() => _key == null ? null : Uint8List.fromList(_key!);

  Future<void> _ensureId() async {
    final i = info;
    if (i != null && i.id == null) {
      i.id = i.recoverySalt.isEmpty ? VaultItem.newId() : i.recoverySalt;
      await _saveInfo(i);
    }
  }

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

  /// Replaces the recovery key (the old one stops working). Returns the new one to show once.
  Future<String> newRecoveryKey() async {
    final i = info ?? (throw StateError('The Locked album isn\'t set up.'));
    final key = _key ?? (throw StateError('Unlock first.'));
    i.id ??= i.recoverySalt;
    final recovery = Sbox.newRecoveryKey();
    final rsalt = Sbox.randomBytes(16);
    i
      ..recoverySalt = base64.encode(rsalt)
      ..recoveryWrappedKey = await Sbox.wrap(key, await Sbox.deriveAsync(Sbox.normalizeRecovery(recovery), rsalt, Sbox.iterations));
    await _saveInfo(i);
    notifyListeners();
    return recovery;
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
  /// it counts as secured: moving deletes the original only then. A protected album (open) gets them encrypted with
  /// its own key.
  Future<VaultResult> add(List<LockSource> sources, {String? albumId, bool verify = false, void Function(int done, int total)? progress}) async {
    if (_key == null) throw StateError('Unlock the Locked album first.');
    final album = albumId == null ? null : _albums[albumId];
    final protectedIn = album != null && album.isProtected ? album.id : null;
    final key = Uint8List.fromList(_keyFor(protectedIn));
    final target = protectedIn == null ? _records : album!._records;
    final r = VaultResult();
    _busy++;
    try {
      for (final d in ['items', 'thumbs', 'records']) {
        await Directory('${_base(protectedIn)}/$d').create(recursive: true);
      }
      final have = {for (final i in target.values.where((x) => !x.deleted)) '${i.name.toLowerCase()}|${i.size}'};
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
          final existing = target.values.where((i) => !i.deleted && i.size == size && i.name.toLowerCase() == p.name.toLowerCase()).firstOrNull;
          if (album != null && protectedIn == null && existing != null && !existing.albumIds.contains(album.id)) {
            existing.albumIds.add(album.id);
            await _writeRecord(existing);
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
          ..rotation = p.rotation
          ..caption = p.caption
          ..tags = List.of(p.tags)
          ..favorite = p.favorite
          ..protectedIn = protectedIn;
        if (album != null && protectedIn == null) item.albumIds.add(album.id);
        final dest = File(itemPath(item.id, protectedIn));
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
            await File(thumbPath(item.id, protectedIn)).writeAsBytes(await Sbox.encryptBytes(thumb, key));
            item.hasThumb = true;
          }
          if (_key != null) {
            target[item.id] = item;
            await _writeRecord(item);
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
      if (_key != null) await _saveIndex();
    } finally {
      key.fillRange(0, key.length, 0);
      _busy--;
      _changed();
      _maybeLock();
    }
    return r;
  }

  void _maybeLock() {
    if (_lockWhenDone && !isBusy) {
      _lockWhenDone = false;
      lock();
    }
  }

  // ------------------------------------------------------------------ details

  /// Caption, tags and favorite, kept encrypted with the item.
  Future<void> setDetails(Item p, {String? caption, List<String>? tags, bool? favorite}) async {
    final r = _record(p);
    if (r == null) return;
    if (caption != null) r.caption = caption.trim();
    if (tags != null) r.tags = tags;
    if (favorite != null) r.favorite = favorite;
    p.caption = r.caption;
    p.tags = List.of(r.tags);
    p.favorite = r.favorite;
    await _writeRecord(r);
    _changed();
  }

  /// Search inside the Locked album: names, captions, tags, cameras, albums, months and years.
  List<Item> search(String query, List<Item> within) {
    final words = query.toLowerCase().split(RegExp(r'\s+')).where((w) => w.isNotEmpty).toList();
    if (words.isEmpty) return within;
    return within.where((i) {
      final text = [
        i.name,
        i.caption,
        ...i.tags,
        i.camera,
        for (final a in albumsOf(i)) a.name,
        monthNames[i.taken.month - 1],
        '${i.taken.year}',
        if (i.favorite) 'favorite',
        i.isVideo ? 'video' : 'photo',
      ].join(' ').toLowerCase();
      return words.every(text.contains);
    }).toList();
  }

  // ------------------------------------------------------------------ albums inside it

  VaultAlbum? album(String? id) => id == null || _key == null ? null : _albums[id];

  String _uniqueName(String name, [VaultAlbum? except]) {
    name = name.trim().isEmpty ? 'New album' : name.trim();
    var n = name;
    for (var i = 2; albums.any((a) => a != except && a.name.toLowerCase() == n.toLowerCase()); i++) {
      n = '$name ($i)';
    }
    return n;
  }

  Future<VaultAlbum> createAlbum(String name) async {
    if (_key == null) throw StateError('Unlock first.');
    final a = VaultAlbum(name: _uniqueName(name));
    _albums[a.id] = a;
    await _writeAlbum(a);
    await _saveIndex();
    _changed();
    return a;
  }

  Future<void> renameAlbum(VaultAlbum a, String name) async {
    if (_key == null) return;
    a.name = _uniqueName(name, a);
    await _writeAlbum(a);
    await _saveIndex();
    _changed();
  }

  /// Deletes the album. A normal album's items stay in the Locked album; a protected album's items are moved back
  /// into it first (it must be open).
  Future<void> deleteAlbum(VaultAlbum a) async {
    if (_key == null) return;
    if (a.isProtected) {
      if (!a.isOpen) throw StateError('Open the album first.');
      await unprotectAlbum(a);
    }
    a.deleted = true;
    for (final r in _records.values.where((r) => r.albumIds.contains(a.id))) {
      r.albumIds.remove(a.id);
      await _writeRecord(r);
    }
    await _writeAlbum(a);
    await _saveIndex();
    _changed();
  }

  Future<void> setCover(VaultAlbum a, Item p) async {
    a.coverId = p.vaultId;
    await _writeAlbum(a);
    _changed();
  }

  /// Adds items to an album. Into a protected (open) album, they're moved: re-encrypted with its key.
  Future<int> addToAlbum(VaultAlbum a, Iterable<Item> items) async {
    if (_key == null) return 0;
    final list = items.where((x) => x.locked).toList();
    if (a.isProtected) return _moveInto(a, list);
    var n = 0;
    for (final p in list) {
      final r = _record(p);
      if (r == null || r.protectedIn != null || r.albumIds.contains(a.id)) continue;
      r.albumIds.add(a.id);
      await _writeRecord(r);
      n++;
    }
    await _saveIndex();
    _changed();
    return n;
  }

  Future<void> removeFromAlbum(VaultAlbum a, Iterable<Item> items) async {
    if (_key == null) return;
    final list = items.toList();
    if (a.isProtected) {
      await _moveOut(a, list.where((p) => p.protectedIn == a.id).toList());
    } else {
      for (final p in list) {
        final r = _record(p);
        if (r != null && r.albumIds.remove(a.id)) await _writeRecord(r);
      }
    }
    await _saveIndex();
    _changed();
  }

  List<Item> itemsIn(VaultAlbum a) {
    if (a.isProtected) return a.isOpen ? [for (final r in a._records.values.where((r) => !r.deleted)) toItem(r)] : const [];
    return items.where((p) => _records[p.vaultId]?.albumIds.contains(a.id) ?? false).toList();
  }

  List<VaultAlbum> albumsOf(Item p) {
    if (!p.locked) return const [];
    if (p.protectedIn != null) return [if (_albums[p.protectedIn] != null) _albums[p.protectedIn]!];
    final ids = _records[p.vaultId]?.albumIds ?? const [];
    return albums.where((a) => ids.contains(a.id)).toList();
  }

  Item? coverOf(VaultAlbum a) {
    final list = itemsIn(a);
    if (list.isEmpty) return null;
    return list.where((p) => p.vaultId == a.coverId).firstOrNull ?? list.first;
  }

  // ------------------------------------------------------------------ albums with their own PIN

  /// Gives an album its own PIN or password: its items are re-encrypted with a key only that PIN opens.
  Future<void> protectAlbum(VaultAlbum a, String secret, String kind) async {
    if (_key == null) throw StateError('Unlock first.');
    if (a.isProtected) throw StateError('It already has its own ${a.protection!.kind == 'PIN' ? 'PIN' : 'password'}.');
    final key = Sbox.randomBytes(32), salt = Sbox.randomBytes(16);
    final members = itemsIn(a);
    a.protection = AlbumProtection()
      ..kind = kind
      ..salt = base64.encode(salt)
      ..wrappedKey = await Sbox.wrap(key, await Sbox.deriveAsync(secret, salt, Sbox.iterations));
    a._key = key;
    a._records = {};
    await _writeAlbum(a);
    await _moveInto(a, members);
    await _saveIndex();
    _changed();
  }

  /// Opens a protected album with its PIN or password.
  Future<bool> unlockAlbum(VaultAlbum a, String secret) async {
    final pr = a.protection;
    if (pr == null || _key == null) return false;
    final key = await Sbox.unwrap(pr.wrappedKey, await Sbox.deriveAsync(secret, base64.decode(pr.salt), pr.iterations));
    if (key == null) {
      await recordFailure('Album PIN');
      return false;
    }
    a._key = key;
    a._records = await _readRecords('${_base(a.id)}/records', key, a.id);
    lastActivity = DateTime.now();
    _changed();
    return true;
  }

  void lockAlbum(VaultAlbum a) {
    a._key?.fillRange(0, a._key!.length, 0);
    a._key = null;
    a._records = {};
    _changed();
  }

  /// Takes the album's own PIN off: its items go back into the Locked album (in this album).
  Future<void> unprotectAlbum(VaultAlbum a) async {
    if (!a.isProtected) return;
    if (!a.isOpen) throw StateError('Open the album first.');
    await _moveOut(a, itemsIn(a), keepInAlbum: true);
    a.protection = null;
    lockAlbum(a);
    await _writeAlbum(a);
    try {
      await Directory(_base(a.id)).delete(recursive: true);
    } catch (_) {}
    await _saveIndex();
    _changed();
  }

  Future<void> changeAlbumSecret(VaultAlbum a, String secret, String kind) async {
    final key = a._key ?? (throw StateError('Open the album first.'));
    final salt = Sbox.randomBytes(16);
    a.protection = AlbumProtection()
      ..kind = kind
      ..salt = base64.encode(salt)
      ..wrappedKey = await Sbox.wrap(key, await Sbox.deriveAsync(secret, salt, Sbox.iterations));
    await _writeAlbum(a);
    _changed();
  }

  /// Re-encrypts main-album items with a protected album's key and moves them there.
  Future<int> _moveInto(VaultAlbum a, List<Item> list) async {
    final akey = a._key ?? (throw StateError('Open the album first.'));
    final key = _key!;
    var n = 0;
    _busy++;
    try {
      for (final d in ['items', 'thumbs', 'records']) {
        await Directory('${_base(a.id)}/$d').create(recursive: true);
      }
      for (final p in list) {
        final r = _records[p.vaultId];
        if (r == null || r.deleted) continue;
        await _reencrypt(r.id, key, null, akey, a.id, r.hasThumb);
        final moved = VaultItem.fromJson(r.toRecord())
          ..protectedIn = a.id
          ..albumIds = [];
        a._records[moved.id] = moved;
        await _writeRecord(moved);
        await _tombstone(r);
        n++;
      }
    } finally {
      _busy--;
    }
    await _saveIndex();
    _changed();
    return n;
  }

  /// Moves items out of a protected album back into the Locked album.
  Future<void> _moveOut(VaultAlbum a, List<Item> list, {bool keepInAlbum = false}) async {
    final akey = a._key ?? (throw StateError('Open the album first.'));
    final key = _key!;
    _busy++;
    try {
      for (final p in list) {
        final r = a._records[p.vaultId];
        if (r == null || r.deleted) continue;
        await _reencrypt(r.id, akey, a.id, key, null, r.hasThumb);
        final moved = VaultItem.fromJson(r.toRecord())
          ..protectedIn = null
          ..albumIds = keepInAlbum ? [a.id] : [];
        _records[moved.id] = moved;
        await _writeRecord(moved);
        await _tombstone(r);
      }
    } finally {
      _busy--;
    }
  }

  Future<void> _reencrypt(String id, Uint8List fromKey, String? fromAlbum, Uint8List toKey, String? toAlbum, bool thumb) async {
    final tmp = File('${tempFolder ?? Directory.systemTemp.path}/move-$id');
    await tmp.parent.create(recursive: true);
    try {
      await Sbox.decryptFile(File(itemPath(id, fromAlbum)), tmp, fromKey);
      final out = File('${itemPath(id, toAlbum)}.tmp');
      await Sbox.encryptFile(tmp, out, toKey);
      await out.rename(itemPath(id, toAlbum));
      if (thumb && File(thumbPath(id, fromAlbum)).existsSync()) {
        final t = await Sbox.decryptBytes(await File(thumbPath(id, fromAlbum)).readAsBytes(), fromKey);
        await File(thumbPath(id, toAlbum)).writeAsBytes(await Sbox.encryptBytes(t, toKey));
      }
    } finally {
      try {
        await tmp.delete();
      } catch (_) {}
    }
  }

  /// Marks an item deleted (so devices that haven't caught up don't bring it back) and deletes its files.
  Future<void> _tombstone(VaultItem r) async {
    r.deleted = true;
    await _writeRecord(r);
    for (final p in [itemPath(r.id, r.protectedIn), thumbPath(r.id, r.protectedIn)]) {
      try {
        final f = File(p);
        if (await f.exists()) await f.delete();
      } catch (_) {}
    }
  }

  // ------------------------------------------------------------------ reading

  /// The whole decrypted file, in memory (photos).
  Future<Uint8List> readBytes(String id, [String? protectedIn]) async =>
      Sbox.decryptBytes(await File(itemPath(id, protectedIn)).readAsBytes(), _keyFor(protectedIn));

  Future<Uint8List?> readThumb(String id, [String? protectedIn]) async {
    final f = File(thumbPath(id, protectedIn));
    try {
      if (!await f.exists()) return null;
      return await Sbox.decryptBytes(await f.readAsBytes(), _keyFor(protectedIn));
    } catch (_) {
      return null;
    }
  }

  String get _tempDir => tempFolder ?? '${Directory.systemTemp.path}/LockedPlayback';

  /// Decrypts a video to a temporary file so it can play. Deleted when you move on, lock or close Shutterbox.
  Future<File> decryptForPlayback(Item p, {void Function(double)? progress}) async {
    final key = _keyFor(p.protectedIn);
    final dir = Directory(_tempDir);
    await dir.create(recursive: true);
    final ext = Item.extension(p.name);
    final out = File('${dir.path}/${VaultItem.newId()}${ext.isEmpty ? '' : '.$ext'}');
    await Sbox.decryptFile(File(itemPath(p.vaultId!, p.protectedIn)), out, key, progress: progress);
    return out;
  }

  /// Decrypts items under their own names to share for a moment. The copies are deleted on [cleanTemp]
  /// (when it locks, and when Shutterbox starts).
  Future<List<File>> decryptToShare(List<Item> list) async {
    final dir = Directory('$_tempDir/share-${VaultItem.newId().substring(0, 8)}');
    await dir.create(recursive: true);
    final out = <File>[];
    for (final p in list.where((x) => x.locked)) {
      var f = File('${dir.path}/${p.name.isEmpty ? '${p.vaultId}.jpg' : p.name}');
      if (await f.exists()) f = File('${dir.path}/${p.vaultId}-${p.name}');
      await Sbox.decryptFile(File(itemPath(p.vaultId!, p.protectedIn)), f, _keyFor(p.protectedIn));
      out.add(f);
    }
    return out;
  }

  void cleanTemp() {
    try {
      final dir = Directory(_tempDir);
      if (dir.existsSync()) dir.deleteSync(recursive: true);
    } catch (_) {}
  }

  /// Decrypts items to ordinary files (in [folder]) and takes them out of the Locked album. [saved] gets each file
  /// once it's written (to add it to the phone's gallery); it returns false when that failed.
  Future<VaultResult> restore(List<Item> list, String folder, {Future<bool> Function(File file, Item item)? saved, void Function(int, int)? progress}) async {
    if (_key == null) throw StateError('Unlock first.');
    final r = VaultResult();
    _busy++;
    try {
      await Directory(folder).create(recursive: true);
      var done = 0;
      for (final p in list.where((x) => x.locked)) {
        progress?.call(done++, list.length);
        try {
          final key = _keyFor(p.protectedIn);
          final name = p.name.isEmpty ? '${p.vaultId}.jpg' : p.name;
          final dot = name.lastIndexOf('.');
          final stem = dot > 0 ? name.substring(0, dot) : name, ext = dot > 0 ? name.substring(dot) : '';
          var target = File('$folder/$name');
          for (var i = 2; await target.exists(); i++) {
            target = File('$folder/$stem ($i)$ext');
          }
          await Sbox.decryptFile(File(itemPath(p.vaultId!, p.protectedIn)), target, key);
          try {
            await target.setLastModified(p.taken);
          } catch (_) {}
          if (saved != null && !await saved(target, p)) throw const FileSystemException('couldn\'t add it to your gallery');
          final rec = _record(p);
          if (rec != null) await _tombstone(rec);
          r.added++;
          r.secured.add(p);
        } catch (e) {
          r.errors.add('${p.name}: $e');
        }
      }
      progress?.call(done, list.length);
      await _saveIndex();
    } finally {
      _busy--;
      _changed();
      _maybeLock();
    }
    return r;
  }

  /// Deletes items from the Locked album for good.
  Future<void> remove(Iterable<Item> list) async {
    if (_key == null) throw StateError('Unlock first.');
    for (final p in list.where((x) => x.locked).toList()) {
      final r = _record(p);
      if (r != null) await _tombstone(r);
    }
    await _saveIndex();
    _changed();
  }

  Future<void> rotate(Item p, int degrees) async {
    final r = _record(p);
    if (r == null || _key == null) return;
    r.rotation = ((r.rotation + degrees) % 360 + 360) % 360;
    p.rotation = r.rotation;
    await _writeRecord(r);
    await _saveIndex();
    _changed();
  }

  /// Decrypts every file to make sure it's all there and undamaged (after copying or syncing the folder).
  Future<CheckResult> check({void Function(int done, int total)? progress}) async {
    final res = CheckResult();
    if (_key == null) throw StateError('Unlock first.');
    final all = [
      ..._live,
      for (final a in _albums.values)
        if (a.isOpen) ...a._records.values.where((r) => !r.deleted),
    ];
    _busy++;
    try {
      var done = 0;
      for (final r in all) {
        progress?.call(done++, all.length);
        final f = File(itemPath(r.id, r.protectedIn));
        try {
          if (!await f.exists()) {
            res.problems.add('${r.name}: its file is missing (still syncing?)');
            continue;
          }
          final h = await Sbox.decryptFile(f, null, _keyFor(r.protectedIn));
          if (h.length != 32) throw const FormatException('bad');
          if (r.hasThumb) await Sbox.decryptBytes(await File(thumbPath(r.id, r.protectedIn)).readAsBytes(), _keyFor(r.protectedIn));
          res.checked++;
        } catch (e) {
          res.problems.add('${r.name}: ${e is FormatException ? e.message : e}');
        }
      }
      progress?.call(done, all.length);
    } finally {
      _busy--;
      _changed();
    }
    return res;
  }

  // ------------------------------------------------------------------ wrong guesses

  File get _attemptsFile => File('$folder/attempts/$deviceId.json');

  Map<String, dynamic> _readAttempts(File f) {
    try {
      return jsonDecode(f.readAsStringSync()) as Map<String, dynamic>;
    } catch (_) {
      return {};
    }
  }

  Future<void> _writeAttempts(Map<String, dynamic> j) async {
    try {
      final f = _attemptsFile;
      await f.parent.create(recursive: true);
      final tmp = File('${f.path}.tmp');
      await tmp.writeAsString(jsonEncode(j));
      await tmp.rename(f.path);
    } catch (_) {}
  }

  /// Logs a wrong PIN, password or recovery key (on this device).
  Future<void> recordFailure(String what) async {
    if (!isSetUp) return;
    final j = _readAttempts(_attemptsFile);
    final failed = [...(j['Failed'] as List? ?? const []), {'Time': _utc(DateTime.now()), 'What': what}];
    await _writeAttempts({...j, 'Device': deviceName, 'Failed': failed.length > 50 ? failed.sublist(failed.length - 50) : failed});
  }

  Future<void> _noteUnlock() async {
    final j = _readAttempts(_attemptsFile);
    await _writeAttempts({...j, 'Device': deviceName, 'LastUnlock': _utc(DateTime.now()), 'Failed': j['Failed'] ?? []});
  }

  /// Every logged wrong guess, from all devices, newest first.
  List<FailedAttempt> get failedAttempts {
    final out = <FailedAttempt>[];
    final d = Directory('$folder/attempts');
    if (!d.existsSync()) return out;
    for (final f in d.listSync().whereType<File>().where((f) => f.path.endsWith('.json'))) {
      final j = _readAttempts(f);
      for (final a in (j['Failed'] as List? ?? const [])) {
        if (a is Map) out.add(FailedAttempt(_parseDate(a['Time']), '${a['What'] ?? 'PIN'}', '${j['Device'] ?? 'Another device'}'));
      }
    }
    out.sort((a, b) => b.time.compareTo(a.time));
    return out;
  }

  /// Wrong guesses since the album was last unlocked (on any device).
  Future<List<FailedAttempt>> failedSinceLastUnlock() async {
    DateTime? last;
    final d = Directory('$folder/attempts');
    if (await d.exists()) {
      await for (final f in d.list()) {
        if (f is! File || !f.path.endsWith('.json')) continue;
        final t = _readAttempts(f)['LastUnlock'];
        if (t == null) continue;
        final dt = _parseDate(t);
        if (last == null || dt.isAfter(last)) last = dt;
      }
    }
    return failedAttempts.where((a) => last == null || a.time.isAfter(last)).toList();
  }

  // ------------------------------------------------------------------ photographing wrong guesses

  File get _intruderInfo => File('$folder/intruder.json');
  bool get photographsWrongGuesses => _intruderInfo.existsSync();

  /// Turns "Photograph wrong guesses" on (makes the key pair; takes a few seconds).
  Future<void> enableIntruderPhotos() async {
    final key = _key ?? (throw StateError('Unlock first.'));
    final pair = await Isolate2.run(IntruderCrypto.generate);
    await File('$folder/intruder-key.sbox').writeAsBytes(await Sbox.encryptBytes(utf8.encode(jsonEncode(pair.$2)), key));
    await _intruderInfo.writeAsString(jsonEncode({'PublicKey': pair.$1}));
    notifyListeners();
  }

  /// Turns it off and deletes the photos taken.
  Future<void> disableIntruderPhotos() async {
    if (_key == null) throw StateError('Unlock first.');
    for (final p in [_intruderInfo.path, '$folder/intruder-key.sbox']) {
      try {
        await File(p).delete();
      } catch (_) {}
    }
    try {
      await Directory('$folder/intruders').delete(recursive: true);
    } catch (_) {}
    notifyListeners();
  }

  /// Saves a photo of a wrong guess, encrypted so only someone who can unlock the album can see it.
  Future<void> saveIntruderPhoto(List<int> jpeg) async {
    if (!photographsWrongGuesses) return;
    final pub = (jsonDecode(await _intruderInfo.readAsString()) as Map<String, dynamic>)['PublicKey'] as Map<String, dynamic>;
    final blob = await IntruderCrypto.seal(pub, jpeg);
    final f = File('$folder/intruders/${IntruderCrypto.newName(DateTime.now())}.sbox');
    await f.parent.create(recursive: true);
    await f.writeAsBytes(blob);
  }

  List<IntruderPhoto> get intruderPhotos {
    final d = Directory('$folder/intruders');
    if (!d.existsSync()) return const [];
    final list = [for (final f in d.listSync().whereType<File>().where((f) => f.path.endsWith('.sbox'))) IntruderPhoto(f, IntruderCrypto.timeOf(f.uri.pathSegments.last))];
    list.sort((a, b) => (b.time ?? DateTime(0)).compareTo(a.time ?? DateTime(0)));
    return list;
  }

  Future<Uint8List> readIntruderPhoto(IntruderPhoto p) async {
    final key = _key ?? (throw StateError('Unlock first.'));
    final priv = jsonDecode(utf8.decode(await Sbox.decryptBytes(await File('$folder/intruder-key.sbox').readAsBytes(), key))) as Map<String, dynamic>;
    return IntruderCrypto.open(priv, await p.file.readAsBytes());
  }

  Future<void> deleteIntruderPhoto(IntruderPhoto p) async {
    try {
      await p.file.delete();
    } catch (_) {}
    notifyListeners();
  }

  // ------------------------------------------------------------------ writing

  Future<void> _writeRecord(VaultItem r, {bool touch = true}) async {
    if (touch) r.modified = DateTime.now().toUtc();
    final key = _keyFor(r.protectedIn);
    final f = File(_recordPath(r.id, r.protectedIn));
    await f.parent.create(recursive: true);
    final tmp = File('${f.path}.tmp');
    await tmp.writeAsBytes(await Sbox.encryptBytes(utf8.encode(jsonEncode(r.toRecord())), key), flush: true);
    await tmp.rename(f.path);
  }

  Future<void> _writeAlbum(VaultAlbum a, {bool touch = true}) async {
    final key = _key;
    if (key == null) return;
    if (touch) a.modified = DateTime.now().toUtc();
    final f = File('$folder/albums/${a.id}.sbox');
    await f.parent.create(recursive: true);
    final tmp = File('${f.path}.tmp');
    await tmp.writeAsBytes(await Sbox.encryptBytes(utf8.encode(jsonEncode(a.toRecord())), key), flush: true);
    await tmp.rename(f.path);
  }

  /// The index for Shutterbox 0.2.0 and 0.3.0: the main album's items and its unprotected albums.
  Future<void> _saveIndex() async {
    final key = _key;
    if (key == null) return;
    final live = _live;
    final albums = [
      for (final a in _albums.values.where((a) => !a.deleted && !a.isProtected))
        VaultAlbum(id: a.id, name: a.name, created: a.created, itemIds: [for (final r in live) if (r.albumIds.contains(a.id)) r.id]),
    ];
    final json = utf8.encode(jsonEncode({'Version': 2, 'Items': [for (final i in live) i.toJson()], 'Albums': [for (final a in albums) a.toJson()]}));
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
