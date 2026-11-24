// The Locked album must be byte-for-byte the Windows app's format: an album made on a PC opens here, and this one there.
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart' as hash;
import 'package:cryptography/cryptography.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shutterbox/models.dart';
import 'package:shutterbox/vault/intruder.dart';
import 'package:shutterbox/vault/sbox.dart';
import 'package:shutterbox/vault/vault.dart';

String hex(List<int> b) => b.map((x) => x.toRadixString(16).padLeft(2, '0')).join();

Directory copyFixture() {
  final to = Directory.systemTemp.createTempSync('sb-vault-');
  final from = Directory('test/fixtures/windows_vault');
  for (final e in from.listSync(recursive: true).whereType<File>()) {
    final rel = e.path.substring(from.path.length + 1);
    final dest = File('${to.path}/$rel');
    dest.parent.createSync(recursive: true);
    e.copySync(dest.path);
  }
  return to;
}

void main() {
  group('PBKDF2-HMAC-SHA256', () {
    test('RFC 7914 test vectors', () {
      expect(hex(pbkdf2Sha256(utf8.encode('passwd'), utf8.encode('salt'), 1, 64)),
          '55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dacbc49ca9cccf179b645991664b39d77ef317c71b845b1e30bd509112041d3a19783');
      expect(hex(pbkdf2Sha256(utf8.encode('Password'), utf8.encode('NaCl'), 80000, 64)),
          '4ddcd8f60b98be21830cee5ef22701f9641a4418d04c0414aeff08876b34ab56a1d425a1225833549adb841b51c9b3176a272bdebba1d078478f62b397f33c8d');
    });

    test('matches the cryptography package', () async {
      for (final (pw, iterations) in [('2468', 1000), ('a much longer password with ünïcödé', 2500), ('x' * 100, 3)]) {
        final salt = Sbox.randomBytes(16);
        final ref = await Pbkdf2(macAlgorithm: Hmac.sha256(), iterations: iterations, bits: 256).deriveKey(secretKey: SecretKey(utf8.encode(pw)), nonce: salt);
        expect(hex(Sbox.derive(pw, salt, iterations)), hex(await ref.extractBytes()), reason: pw);
      }
    });

    test('600,000 rounds finish in reasonable time', () {
      final sw = Stopwatch()..start();
      Sbox.derive('123456', Sbox.randomBytes(16), Sbox.iterations);
      // ignore: avoid_print
      print('600,000 PBKDF2 rounds: ${sw.elapsedMilliseconds} ms');
      expect(sw.elapsed.inSeconds, lessThan(30));
    });
  });

  group('file format', () {
    test('chunk nonce and associated data', () {
      final base = List<int>.generate(12, (i) => i);
      expect(Sbox.chunkNonce(base, 0), base);
      expect(Sbox.chunkNonce(base, 1).last, 11 ^ 1);
      expect(Sbox.chunkNonce(base, 0x0102)[10], 10 ^ 1);
      expect(Sbox.chunkAad(5, true), [5, 0, 0, 0, 0, 0, 0, 0, 1]);
      expect(Sbox.chunkAad(258, false), [2, 1, 0, 0, 0, 0, 0, 0, 0]);
    });

    test('bytes and files round trip, at every piece boundary', () async {
      final key = Sbox.randomBytes(32);
      final dir = Directory.systemTemp.createTempSync('sb-sbox-');
      for (final n in [0, 1, Sbox.chunkSize - 1, Sbox.chunkSize, Sbox.chunkSize + 1, 2 * Sbox.chunkSize + 77]) {
        final data = Uint8List.fromList(List.generate(n, (i) => (i * 31 + 7) & 0xFF));
        final enc = await Sbox.encryptBytes(data, key);
        expect(enc.length, 17 + data.length + 16 * (n == 0 ? 1 : (n + Sbox.chunkSize - 1) ~/ Sbox.chunkSize), reason: '$n bytes');
        expect(await Sbox.decryptBytes(enc, key), data);
        final src = File('${dir.path}/$n.bin')..writeAsBytesSync(data);
        final out = File('${dir.path}/$n.sbox');
        final h1 = await Sbox.encryptFile(src, out, key);
        expect(out.lengthSync(), enc.length);
        final back = File('${dir.path}/$n.back');
        final h2 = await Sbox.decryptFile(out, back, key);
        expect(back.readAsBytesSync(), data);
        expect(hex(h1), hex(h2));
        expect(hex(h1), hash.sha256.convert(data).toString());
      }
    });

    test('a changed, cut short or reordered file is refused', () async {
      final key = Sbox.randomBytes(32);
      final data = Uint8List.fromList(List.generate(Sbox.chunkSize * 2 + 10, (i) => i & 0xFF));
      final enc = await Sbox.encryptBytes(data, key);
      final changed = Uint8List.fromList(enc)..[100] ^= 1;
      await expectLater(Sbox.decryptBytes(changed, key), throwsA(isA<FormatException>()));
      // cut after the first piece: the first piece wasn't the last, so it fails
      await expectLater(Sbox.decryptBytes(enc.sublist(0, 17 + 16 + Sbox.chunkSize), key), throwsA(isA<FormatException>()));
      await expectLater(Sbox.decryptBytes(enc, Sbox.randomBytes(32)), throwsA(isA<FormatException>()));
      await expectLater(Sbox.decryptBytes(utf8.encode('not a locked album file at all'), key), throwsA(isA<FormatException>()));
    });
  });

  group('a Locked album made by Shutterbox for Windows', () {
    late Directory dir;
    late Map<String, dynamic> expected;
    setUp(() {
      dir = copyFixture();
      expected = jsonDecode(File('${dir.path}/expected.json').readAsStringSync()) as Map<String, dynamic>;
    });

    test('opens with its PIN and everything decrypts', () async {
      final v = Vault(dir.path);
      expect(v.isSetUp, isTrue);
      expect(Vault.isVaultFolder(dir.path), isTrue);
      expect(v.info!.kind, 'PIN');
      expect(v.info!.iterations, 1000);
      expect(await v.unlock('1357'), isFalse);
      expect(v.isUnlocked, isFalse);
      expect(await v.unlock('2468'), isTrue);
      expect(v.count, 4);
      final photo = v.items.firstWhere((i) => i.name == 'IMG_2041.jpg');
      expect(photo.taken, DateTime(2026, 10, 14, 9, 12, 30));
      expect(photo.make, 'Canon');
      expect(photo.model, 'EOS R6');
      expect(photo.rotation, 90);
      expect(photo.orientation, 6);
      expect(photo.latitude, closeTo(45.5231, 1e-6));
      expect(photo.isVideo, isFalse);
      expect(photo.added.toUtc().difference(DateTime.utc(2026, 11, 20, 18, 0, 0, 123)).inMilliseconds.abs(), lessThan(2));
      final video = v.items.firstWhere((i) => i.name == 'Beach day.mp4');
      expect(video.isVideo, isTrue);
      expect(video.duration, closeTo(12.48, 1e-9));
      expect(video.make, isNull);
      for (final i in v.items) {
        final bytes = await v.readBytes(i.vaultId!);
        expect(hash.sha256.convert(bytes).toString(), expected[i.vaultId], reason: i.name);
        expect(i.size, bytes.length);
      }
      expect(await v.readThumb(photo.vaultId!), isNotNull);
      expect(await v.readThumb(video.vaultId!), isNull);
      // the album keeps only items that exist
      expect(v.albums.single.name, 'Private');
      expect(v.itemsIn(v.albums.single).map((i) => i.name), ['IMG_2041.jpg']);
      expect(v.albumsOf(photo).single.name, 'Private');
      // the key opens it too (fingerprint unlock keeps a copy of it)
      expect(await Vault(dir.path).unlockWithKey(v.copyKey()!), isTrue);
      v.lock();
      expect(v.items, isEmpty);
      expect(v.albums, isEmpty);
    });

    test('opens with its recovery key, typed any way', () async {
      final v = Vault(dir.path);
      expect(await v.unlockWithRecoveryKey('abcde fghjk lmnpq rstuv wxyz2'), isTrue);
      v.lock();
      expect(await v.unlockWithRecoveryKey('ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ3'), isFalse);
    });

    test('reads a version 1 index (a plain list)', () async {
      final v = Vault(dir.path);
      expect(await v.unlock('2468'), isTrue);
      final key = v.copyKey()!;
      final parsed = Vault.parseIndex(await Sbox.decryptBytes(File('${dir.path}/index-v1.sbox').readAsBytesSync(), key));
      expect(parsed.$1.single.name, 'IMG_2041.jpg');
      expect(parsed.$2, isEmpty);
    });

    test('changes made here keep the Windows format', () async {
      final v = Vault(dir.path, tempFolder: Directory.systemTemp.createTempSync('sb-t-').path);
      expect(await v.unlock('2468'), isTrue);
      final video = v.items.firstWhere((i) => i.isVideo);
      final f = await v.decryptForPlayback(video);
      expect(hash.sha256.convert(f.readAsBytesSync()).toString(), expected[video.vaultId]);
      v.cleanTemp();
      expect(f.existsSync(), isFalse);
      await v.rotate(video, 270);
      final a = await v.createAlbum('Trips');
      await v.addToAlbum(a, [video]);
      await v.renameAlbum(a, 'private');
      expect(a.name, 'private (2)');
      v.lock();
      expect(await v.unlock('2468'), isTrue);
      expect(v.items.firstWhere((i) => i.isVideo).rotation, 270);
      expect(v.albums.map((a) => a.name), ['Private', 'private (2)']);
      final raw = jsonDecode(utf8.decode(await Sbox.decryptBytes(File('${dir.path}/index.sbox').readAsBytesSync(), v.copyKey()!))) as Map<String, dynamic>;
      expect(raw['Version'], 2);
      final item = (raw['Items'] as List).first as Map<String, dynamic>;
      expect(item.keys, containsAll(['Id', 'Name', 'Size', 'Taken', 'TakenSource', 'Width', 'Height', 'DurationSeconds', 'Make', 'Model', 'Latitude', 'Longitude', 'Orientation', 'Rotation', 'Added', 'HasThumb']));
      expect(item['Width'], isA<int>());
      expect(item['Taken'], matches(RegExp(r'^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$')));
    });
  });

  group('a 0.4.0 album (records, sync conflicts, a protected album, wrong guesses)', () {
    late Directory dir;
    late Map<String, dynamic> expected;
    setUp(() {
      dir = Directory.systemTemp.createTempSync('sb-v4-');
      final from = Directory('test/fixtures/vault4');
      for (final e in from.listSync(recursive: true).whereType<File>()) {
        final dest = File('${dir.path}/${e.path.substring(from.path.length + 1)}');
        dest.parent.createSync(recursive: true);
        e.copySync(dest.path);
      }
      expected = jsonDecode(File('${dir.path}/expected4.json').readAsStringSync()) as Map<String, dynamic>;
    });

    test('merges records, conflict copies, deletions and the old index', () async {
      final v = Vault(dir.path, deviceId: 'phone-test', deviceName: 'Test phone');
      expect(v.identity, '0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f');
      expect(await v.unlock('2468'), isTrue);
      final names = v.items.map((i) => i.name).toSet();
      expect(names, {'IMG_2041.jpg', 'Beach day.mp4', 'empty.gif'}); // exact.png was deleted elsewhere; empty.gif came from the old index
      final photo = v.items.firstWhere((i) => i.name == 'IMG_2041.jpg');
      expect(photo.caption, 'Sunrise at the coast'); // the newer record beat its conflict copy
      expect(photo.tags, ['Beach', 'Trip']);
      expect(photo.favorite, isTrue);
      final video = v.items.firstWhere((i) => i.isVideo);
      expect(video.rotation, 180); // the newer conflict copy won
      final albums = v.albums.map((a) => a.name).toList();
      expect(albums, ['Private stuff', 'Very private']); // "Gone" was deleted
      final ps = v.albums.firstWhere((a) => a.name == 'Private stuff');
      expect(v.itemsIn(ps).map((i) => i.name).toSet(), {'IMG_2041.jpg', 'Beach day.mp4'});
      expect(v.coverOf(ps)?.name, 'Beach day.mp4');
      expect(v.search('beach trip', v.items).single.name, 'IMG_2041.jpg');
      expect(v.search('private stuff', v.items).length, 2);
      // the wrong guess after the last unlock was shown
      expect(v.lastUnlockWarnings.length, 1);
      expect(v.lastUnlockWarnings.single.device, 'Desktop PC');
      // tidied: conflict copies gone, every record under its own name
      final recordNames = Directory('${dir.path}/records').listSync().map((f) => f.uri.pathSegments.last).toList();
      expect(recordNames.every((n) => RegExp(r'^[0-9a-f]{32}\.sbox$').hasMatch(n)), isTrue, reason: '$recordNames');
      // the protected album
      final vp = v.albums.firstWhere((a) => a.name == 'Very private');
      expect(vp.isProtected, isTrue);
      expect(v.itemsIn(vp), isEmpty);
      expect(await v.unlockAlbum(vp, '9999'), isTrue);
      final secret = v.itemsIn(vp).single;
      expect(hash.sha256.convert(await v.readBytes(secret.vaultId!, vp.id)).toString(), expected['secret']);
      // the photographed wrong guess
      final shot = v.intruderPhotos.single;
      expect(shot.time, DateTime.utc(2026, 11, 24, 2, 14).toLocal());
      expect(hash.sha256.convert(await v.readIntruderPhoto(shot)).toString(), expected['intruder']);
    });

    test('two devices: changes to different items both survive', () async {
      final a = Vault(dir.path), b = Vault(dir.path);
      expect(await a.unlock('2468'), isTrue);
      expect(await b.unlock('2468'), isTrue);
      final ap = a.items.firstWhere((i) => i.name == 'IMG_2041.jpg');
      final bv = b.items.firstWhere((i) => i.isVideo);
      await a.setDetails(ap, caption: 'from the phone');
      await b.setDetails(bv, caption: 'from the PC');
      await b.reload();
      expect(b.items.firstWhere((i) => i.name == 'IMG_2041.jpg').caption, 'from the phone');
      expect(b.items.firstWhere((i) => i.isVideo).caption, 'from the PC');
      // a delete on one isn't undone by the other's stale view
      await a.reload();
      await a.remove([a.items.firstWhere((i) => i.name == 'empty.gif')]);
      await b.setDetails(b.items.firstWhere((i) => i.isVideo), favorite: true);
      final c = Vault(dir.path);
      expect(await c.unlock('2468'), isTrue);
      expect(c.items.map((i) => i.name), isNot(contains('empty.gif')));
      expect(c.items.firstWhere((i) => i.isVideo).favorite, isTrue);
    });

    test('wrong guesses are logged per device; a new recovery key keeps the identity', () async {
      final v = Vault(dir.path, deviceId: 'phone-x', deviceName: 'Phone X');
      expect(await v.unlock('0000'), isFalse);
      expect(await v.unlock('0000', log: false), isFalse);
      expect(v.failedAttempts.where((a) => a.device == 'Phone X').length, 1);
      expect(await v.unlock('2468'), isTrue);
      expect(v.lastUnlockWarnings.length, 2);
      final id = v.identity;
      final key = await v.newRecoveryKey();
      expect(v.identity, id);
      v.lock();
      expect(await v.unlockWithRecoveryKey('ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2'), isFalse);
      expect(await v.unlockWithRecoveryKey(key), isTrue);
    });
  });

  test('RSA-OAEP for photos of wrong guesses round trips', () async {
    final pair = IntruderCrypto.generate();
    final blob = await IntruderCrypto.seal(pair.$1, [1, 2, 3, 4, 5]);
    expect(await IntruderCrypto.open(pair.$2, blob), [1, 2, 3, 4, 5]);
  });

  test('set up here, add, move-check, restore, change PIN: and Windows can open it (tool/vault_ref.py check)', () async {
    final root = Directory.systemTemp.createTempSync('sb-new-');
    final dir = Directory('${root.path}/Locked');
    final v = Vault(dir.path, tempFolder: '${root.path}/tmp');
    expect(Vault.validate('12', 'PIN'), isNotNull);
    expect(Vault.validate('12a4', 'PIN'), isNotNull);
    expect(Vault.validate('1234', 'PIN'), isNull);
    expect(Vault.validate('abc', 'Password'), isNotNull);
    final recovery = await v.setUp('2468', 'PIN');
    expect(recovery, matches(RegExp(r'^[A-HJ-NP-Z2-9]{5}(-[A-HJ-NP-Z2-9]{5}){4}$')));
    final info = jsonDecode(File('${dir.path}/vault.json').readAsStringSync()) as Map<String, dynamic>;
    expect(info['Iterations'], 600000);
    expect(info['Kind'], 'PIN');

    final photos = Directory('test/fixtures/photos').listSync().whereType<File>().toList()..sort((a, b) => a.path.compareTo(b.path));
    final big = File('${root.path}/Big clip.mp4')..writeAsBytesSync(List.generate(Sbox.chunkSize + 4321, (i) => (i * 7) & 0xFF));
    final sources = [
      for (final (n, f) in photos.take(3).indexed)
        LockSource(
          item: Item(id: 'p$n', name: f.uri.pathSegments.last, taken: DateTime(2026, 9, n + 1, 10), width: 480, height: 360, make: 'Google', model: 'Pixel 9'),
          file: f,
          thumbnail: () async => f.readAsBytesSync(),
        ),
      LockSource(item: Item(id: 'v', name: 'Big clip.mp4', taken: DateTime(2026, 9, 9), duration: 3.5), file: big),
    ];
    final album = await v.createAlbum('Trips');
    final r = await v.add(sources, albumId: album.id, verify: true);
    expect(r.errors, isEmpty);
    expect(r.added, 4);
    expect(r.secured.length, 4);
    // the same again: skipped, but still put in another album
    final second = await v.createAlbum('Second');
    final again = await v.add(sources.take(1).toList(), albumId: second.id);
    expect(again.skipped, 1);
    expect(v.itemsIn(second).length, 1);
    expect(v.itemsIn(album).length, 4);

    // restore one: decrypted, handed to the gallery, gone from the album
    final restored = <String>[];
    final res = await v.restore([v.items.first], '${root.path}/out', saved: (file, item) async {
      restored.add(hash.sha256.convert(file.readAsBytesSync()).toString());
      return true;
    });
    expect(res.added, 1);
    expect(restored.single, hash.sha256.convert(photos.first.readAsBytesSync()).toString());
    expect(v.count, 3);
    await v.remove([v.items.first]);
    expect(v.count, 2);

    await v.changeSecret('secret-password', 'Password');
    v.lock();
    expect(await v.unlock('2468'), isFalse);
    expect(await v.unlock('secret-password'), isTrue);
    await v.changeSecret('2468', 'PIN');
    v.lock();
    expect(await v.unlockWithRecoveryKey(recovery.toLowerCase()), isTrue);

    // moving the folder (to a synced folder, an SD card…)
    final moved = await v.moveTo('${root.path}/Sync');
    expect(moved, isNull);
    expect(v.folder, '${root.path}/Sync/${Vault.portableFolderName}');
    expect(Vault.isVaultFolder(v.folder), isTrue);
    expect(dir.existsSync(), isFalse);
    expect(Vault.findAlbumFolder('${root.path}/Sync'), v.folder);
    v.lock();
    expect(await v.unlock('2468'), isTrue);
    expect(v.count, 2);

    // an album with its own PIN, wrong guesses and a photo of one (tool/vault_ref.py check4 looks at these)
    final vp = await v.createAlbum('Very private');
    await v.addToAlbum(vp, [v.items.first]);
    await v.protectAlbum(vp, '9999', 'PIN');
    expect(v.count, 1);
    expect(v.itemsIn(vp).length, 1);
    v.lockAlbum(vp);
    expect(v.itemsIn(vp), isEmpty);
    expect(await v.unlockAlbum(vp, '1111'), isFalse);
    expect(await v.unlockAlbum(vp, '9999'), isTrue);
    final secretItem = v.itemsIn(vp).single;
    expect(secretItem.protectedIn, vp.id);
    expect((await v.readBytes(secretItem.vaultId!, vp.id)).length, secretItem.size);
    await v.setDetails(secretItem, caption: 'only here', tags: ['x'], favorite: true);
    await v.enableIntruderPhotos();
    await v.saveIntruderPhoto(photos.first.readAsBytesSync());
    expect(v.intruderPhotos.length, 1);
    expect(await v.readIntruderPhoto(v.intruderPhotos.first), photos.first.readAsBytesSync());
    expect(v.failedAttempts.where((a) => a.what == 'Album PIN').length, 1);
    final checked = await v.check();
    expect(checked.problems, isEmpty);
    expect(checked.checked, 2);
    // a 0.3.0 app sees only the main album's item
    final idx = Vault.parseIndex(await Sbox.decryptBytes(File('${v.folder}/index.sbox').readAsBytesSync(), v.copyKey()!));
    expect(idx.$1.length, 1);
    expect(idx.$2.where((a) => a.name == 'Very private'), isEmpty);
    v.lock();
    expect(await v.unlock('2468'), isTrue);
    expect(v.lastUnlockWarnings.map((a) => a.what), contains('Album PIN'));

    // for CI: tool/vault_ref.py checks Windows can open what the phone made
    final export = Platform.environment['SB_EXPORT'];
    if (export != null) {
      final to = Directory(export)..createSync(recursive: true);
      for (final e in Directory(v.folder).listSync(recursive: true).whereType<File>()) {
        final rel = e.path.substring(v.folder.length + 1);
        File('${to.path}/$rel')
          ..parent.createSync(recursive: true)
          ..writeAsBytesSync(e.readAsBytesSync());
      }
      File('${to.path}/recovery.txt').writeAsStringSync(recovery);
    }
  });
}
