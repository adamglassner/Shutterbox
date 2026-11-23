import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:isolate';
import 'dart:math';
import 'dart:typed_data';

import 'package:crypto/crypto.dart' as hash;
import 'package:cryptography/cryptography.dart';

/// The Locked album's file format, byte for byte the same as Shutterbox for Windows (Library/Vault.cs), so an album
/// made on a PC opens on the phone and the other way round.
///
/// A .sbox file is "SBOX1", a 12-byte nonce, then the content in 1 MiB pieces, each its 16-byte AES-256-GCM tag and
/// the encrypted bytes. Piece i uses the base nonce with its last 8 bytes XORed with i, and authenticates i (as a
/// little-endian Int64) plus whether it's the last piece, so a file can't be cut short or reordered unnoticed.
class Sbox {
  static const magic = [0x53, 0x42, 0x4F, 0x58, 0x31]; // SBOX1
  static const chunkSize = 1 << 20;
  static const nonceSize = 12, tagSize = 16;
  static const iterations = 600000;

  static final _aes = AesGcm.with256bits();
  static final _random = Random.secure();

  static Uint8List randomBytes(int n) => Uint8List.fromList(List.generate(n, (_) => _random.nextInt(256)));

  static Uint8List chunkNonce(List<int> base, int index) {
    final n = Uint8List.fromList(base);
    for (var i = 0; i < 8; i++) {
      n[nonceSize - 1 - i] ^= (index >> (8 * i)) & 0xFF;
    }
    return n;
  }

  static Uint8List chunkAad(int index, bool last) {
    final aad = Uint8List(9);
    ByteData.sublistView(aad).setInt64(0, index, Endian.little);
    aad[8] = last ? 1 : 0;
    return aad;
  }

  // ------------------------------------------------------------------ keys

  /// PBKDF2-HMAC-SHA256, 32 bytes (Rfc2898DeriveBytes.Pbkdf2 on Windows). Slow on purpose: run it with [deriveAsync].
  static Uint8List derive(String secret, List<int> salt, int iterations) =>
      pbkdf2Sha256(utf8.encode(secret), salt, iterations, 32);

  /// [derive] on another thread, so the screen keeps moving while 600,000 rounds run.
  static Future<Uint8List> deriveAsync(String secret, List<int> salt, int iterations) {
    final s = Uint8List.fromList(salt);
    return Isolate2.run(() => derive(secret, s, iterations));
  }

  /// Wraps the album key: base64(nonce | tag | encrypted key).
  static Future<String> wrap(List<int> key, List<int> wrappingKey) async {
    final nonce = randomBytes(nonceSize);
    final box = await _aes.encrypt(key, secretKey: SecretKey(wrappingKey), nonce: nonce);
    return base64.encode([...nonce, ...box.mac.bytes, ...box.cipherText]);
  }

  /// The album key, or null when the PIN, password or recovery key is wrong.
  static Future<Uint8List?> unwrap(String wrapped, List<int> wrappingKey) async {
    try {
      final d = base64.decode(wrapped);
      if (d.length < nonceSize + tagSize) return null;
      final box = SecretBox(d.sublist(nonceSize + tagSize), nonce: d.sublist(0, nonceSize), mac: Mac(d.sublist(nonceSize, nonceSize + tagSize)));
      return Uint8List.fromList(await _aes.decrypt(box, secretKey: SecretKey(wrappingKey)));
    } catch (_) {
      return null;
    }
  }

  /// "ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2": 25 letters and digits that are hard to mix up.
  static String newRecoveryKey() {
    const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    final chars = List.generate(25, (_) => alphabet[_random.nextInt(alphabet.length)]);
    return [for (var i = 0; i < 5; i++) chars.sublist(i * 5, i * 5 + 5).join()].join('-');
  }

  static String normalizeRecovery(String key) => key.toUpperCase().replaceAll(RegExp(r'[^A-Z0-9]'), '');

  // ------------------------------------------------------------------ content

  static Future<List<int>> _seal(SecretKey key, List<int> base, int index, List<int> plain, bool last, Sink<List<int>> out) async {
    final box = await _aes.encrypt(plain, secretKey: key, nonce: chunkNonce(base, index), aad: chunkAad(index, last));
    out.add(box.mac.bytes);
    out.add(box.cipherText);
    return box.cipherText;
  }

  static Future<List<int>> _open(SecretKey key, List<int> base, int index, List<int> cipher, List<int> tag, bool last) async {
    try {
      return await _aes.decrypt(SecretBox(cipher, nonce: chunkNonce(base, index), mac: Mac(tag)), secretKey: key, aad: chunkAad(index, last));
    } on SecretBoxAuthenticationError {
      throw const FormatException('The file has been changed or is damaged.');
    }
  }

  static Future<Uint8List> encryptBytes(List<int> data, List<int> key) async {
    final out = BytesBuilder(copy: false);
    final sink = _BuilderSink(out);
    final base = randomBytes(nonceSize);
    out.add(magic);
    out.add(base);
    final k = SecretKey(key);
    var index = 0;
    for (var pos = 0;; pos += chunkSize, index++) {
      final end = min(pos + chunkSize, data.length);
      final last = end >= data.length;
      await _seal(k, base, index, data.sublist(min(pos, data.length), end), last, sink);
      if (last) break;
    }
    return out.takeBytes();
  }

  static Future<Uint8List> decryptBytes(List<int> data, List<int> key) async {
    if (data.length < magic.length + nonceSize || !_isMagic(data)) throw const FormatException('This isn\'t a Shutterbox Locked album file.');
    final base = data.sublist(magic.length, magic.length + nonceSize);
    final k = SecretKey(key);
    final out = BytesBuilder(copy: false);
    var pos = magic.length + nonceSize;
    for (var index = 0;; index++) {
      if (pos + tagSize > data.length) throw const FormatException('The file is cut short.');
      final tag = data.sublist(pos, pos + tagSize);
      final end = min(pos + tagSize + chunkSize, data.length);
      final cipher = data.sublist(pos + tagSize, end);
      pos = end;
      final last = pos >= data.length;
      out.add(await _open(k, base, index, cipher, tag, last));
      if (last) break;
    }
    return out.takeBytes();
  }

  static bool _isMagic(List<int> d) {
    for (var i = 0; i < magic.length; i++) {
      if (d[i] != magic[i]) return false;
    }
    return true;
  }

  /// Encrypts a file to another, a piece at a time (long videos never have to fit in memory).
  /// Returns the SHA-256 of what was read.
  static Future<List<int>> encryptFile(File input, File output, List<int> key, {void Function(double)? progress}) async {
    final src = await input.open();
    final dst = await output.open(mode: FileMode.write);
    final digestSink = _DigestSink();
    final hasher = hash.sha256.startChunkedConversion(digestSink);
    try {
      final total = await src.length();
      final base = randomBytes(nonceSize);
      await dst.writeFrom(magic);
      await dst.writeFrom(base);
      final k = SecretKey(key);
      final sink = _FileSink();
      var buf = await src.read(chunkSize);
      for (var index = 0;; index++) {
        final next = buf.length == chunkSize ? await src.read(chunkSize) : Uint8List(0);
        final last = next.isEmpty;
        hasher.add(buf);
        sink.parts.clear();
        await _seal(k, base, index, buf, last, sink);
        for (final p in sink.parts) {
          await dst.writeFrom(p);
        }
        if (total > 0) progress?.call(min(1, (index + 1) * chunkSize / total));
        if (last) break;
        buf = next;
      }
      await dst.flush();
    } finally {
      await src.close();
      await dst.close();
    }
    hasher.close();
    return digestSink.value!.bytes;
  }

  /// Decrypts a file, a piece at a time, into [output] (or only hashes it when [output] is null).
  /// Returns the SHA-256 of the decrypted content.
  static Future<List<int>> decryptFile(File input, File? output, List<int> key, {void Function(double)? progress}) async {
    final src = await input.open();
    final dst = output == null ? null : await output.open(mode: FileMode.write);
    final digestSink = _DigestSink();
    final hasher = hash.sha256.startChunkedConversion(digestSink);
    try {
      final total = await src.length();
      final head = await src.read(magic.length + nonceSize);
      if (head.length != magic.length + nonceSize || !_isMagic(head)) throw const FormatException('This isn\'t a Shutterbox Locked album file.');
      final base = head.sublist(magic.length);
      final k = SecretKey(key);
      for (var index = 0;; index++) {
        final tag = await src.read(tagSize);
        if (tag.length != tagSize) throw const FormatException('The file is cut short.');
        final cipher = await src.read(chunkSize);
        final pos = await src.position();
        final last = pos >= total;
        final plain = await _open(k, base, index, cipher, tag, last);
        hasher.add(plain);
        if (dst != null) await dst.writeFrom(plain);
        if (total > 0) progress?.call(pos / total);
        if (last) break;
      }
      await dst?.flush();
    } finally {
      await src.close();
      await dst?.close();
    }
    hasher.close();
    return digestSink.value!.bytes;
  }

  /// SHA-256 of a file.
  static Future<List<int>> sha256File(File f) async => (await hash.sha256.bind(f.openRead()).first).bytes;
}

class _BuilderSink implements Sink<List<int>> {
  _BuilderSink(this.b);
  final BytesBuilder b;
  @override
  void add(List<int> data) => b.add(data);
  @override
  void close() {}
}

class _FileSink implements Sink<List<int>> {
  final parts = <List<int>>[];
  @override
  void add(List<int> data) => parts.add(data);
  @override
  void close() {}
}

class _DigestSink implements Sink<hash.Digest> {
  hash.Digest? value;
  @override
  void add(hash.Digest data) => value = data;
  @override
  void close() {}
}

/// Runs work on another isolate (Isolate.run), falling back to this one where that isn't possible.
class Isolate2 {
  static Future<T> run<T>(T Function() work) async {
    try {
      return await Isolate.run(work);
    } catch (_) {
      return work();
    }
  }
}

// ------------------------------------------------------------------ PBKDF2-HMAC-SHA256

const _k = <int>[
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, //
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
];

const _iv = <int>[0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];

/// One SHA-256 block: [state] (8 words) updated with [block] (16 words).
void _compress(Uint32List state, Uint32List block, Uint32List w) {
  for (var i = 0; i < 16; i++) {
    w[i] = block[i];
  }
  for (var i = 16; i < 64; i++) {
    final x = w[i - 15], y = w[i - 2];
    final s0 = ((x >> 7) | (x << 25)) ^ ((x >> 18) | (x << 14)) ^ (x >> 3);
    final s1 = ((y >> 17) | (y << 15)) ^ ((y >> 19) | (y << 13)) ^ (y >> 10);
    w[i] = (w[i - 16] + s0 + w[i - 7] + s1) & 0xFFFFFFFF;
  }
  var a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7];
  for (var i = 0; i < 64; i++) {
    final s1 = ((e >> 6) | (e << 26)) ^ ((e >> 11) | (e << 21)) ^ ((e >> 25) | (e << 7));
    final ch = (e & f) ^ ((~e) & g);
    final t1 = (h + (s1 & 0xFFFFFFFF) + (ch & 0xFFFFFFFF) + _k[i] + w[i]) & 0xFFFFFFFF;
    final s0 = ((a >> 2) | (a << 30)) ^ ((a >> 13) | (a << 19)) ^ ((a >> 22) | (a << 10));
    final maj = (a & b) ^ (a & c) ^ (b & c);
    final t2 = ((s0 & 0xFFFFFFFF) + maj) & 0xFFFFFFFF;
    h = g;
    g = f;
    f = e;
    e = (d + t1) & 0xFFFFFFFF;
    d = c;
    c = b;
    b = a;
    a = (t1 + t2) & 0xFFFFFFFF;
  }
  state[0] = (state[0] + a) & 0xFFFFFFFF;
  state[1] = (state[1] + b) & 0xFFFFFFFF;
  state[2] = (state[2] + c) & 0xFFFFFFFF;
  state[3] = (state[3] + d) & 0xFFFFFFFF;
  state[4] = (state[4] + e) & 0xFFFFFFFF;
  state[5] = (state[5] + f) & 0xFFFFFFFF;
  state[6] = (state[6] + g) & 0xFFFFFFFF;
  state[7] = (state[7] + h) & 0xFFFFFFFF;
}

/// PBKDF2 with HMAC-SHA256. The inner and outer HMAC states are computed once, so each round is two SHA-256 blocks.
Uint8List pbkdf2Sha256(List<int> password, List<int> salt, int iterations, int length) {
  var key = password;
  if (key.length > 64) key = hash.sha256.convert(key).bytes;
  final ipad = Uint8List(64)..fillRange(0, 64, 0x36);
  final opad = Uint8List(64)..fillRange(0, 64, 0x5c);
  for (var i = 0; i < key.length; i++) {
    ipad[i] ^= key[i];
    opad[i] ^= key[i];
  }
  final w = Uint32List(64);
  Uint32List words(Uint8List b) {
    final out = Uint32List(16);
    final bd = ByteData.sublistView(b);
    for (var i = 0; i < 16; i++) {
      out[i] = bd.getUint32(i * 4, Endian.big);
    }
    return out;
  }

  final innerStart = Uint32List.fromList(_iv);
  _compress(innerStart, words(ipad), w);
  final outerStart = Uint32List.fromList(_iv);
  _compress(outerStart, words(opad), w);

  // HMAC of a 32-byte message from the precomputed states: one padded block each
  final block = Uint32List(16);
  final state = Uint32List(8);
  void hmac32(Uint32List msg, Uint32List out) {
    // inner: (ipad block already done) + msg(32 bytes) + padding; total length 64 + 32 = 96 bytes = 768 bits
    for (var i = 0; i < 8; i++) {
      block[i] = msg[i];
    }
    block[8] = 0x80000000;
    for (var i = 9; i < 15; i++) {
      block[i] = 0;
    }
    block[15] = 768;
    state.setAll(0, innerStart);
    _compress(state, block, w);
    for (var i = 0; i < 8; i++) {
      block[i] = state[i];
    }
    block[8] = 0x80000000;
    for (var i = 9; i < 15; i++) {
      block[i] = 0;
    }
    block[15] = 768;
    state.setAll(0, outerStart);
    _compress(state, block, w);
    out.setAll(0, state);
  }

  final result = BytesBuilder();
  final blocks = (length + 31) ~/ 32;
  for (var bi = 1; bi <= blocks; bi++) {
    // U1 = HMAC(password, salt || INT(bi)), with the general HMAC for the first round
    final first = hash.Hmac(hash.sha256, password).convert([...salt, (bi >> 24) & 0xFF, (bi >> 16) & 0xFF, (bi >> 8) & 0xFF, bi & 0xFF]).bytes;
    final u = words(Uint8List.fromList([...first, ...List.filled(32, 0)]));
    final u8 = Uint32List(8)..setAll(0, u.sublist(0, 8));
    final t = Uint32List.fromList(u8);
    for (var r = 1; r < iterations; r++) {
      hmac32(u8, u8);
      for (var i = 0; i < 8; i++) {
        t[i] ^= u8[i];
      }
    }
    final bytes = ByteData(32);
    for (var i = 0; i < 8; i++) {
      bytes.setUint32(i * 4, t[i], Endian.big);
    }
    result.add(bytes.buffer.asUint8List());
  }
  return Uint8List.fromList(result.takeBytes().sublist(0, length));
}
