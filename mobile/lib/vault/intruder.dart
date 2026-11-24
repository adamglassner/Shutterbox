import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:crypto/crypto.dart' as hash;
import 'package:pointycastle/export.dart' as pc;

import 'sbox.dart';

/// RSA-OAEP-SHA256 for "Photograph wrong guesses": a photo taken while the Locked album is locked is encrypted to a
/// public key, so only someone who can unlock the album (and so read the private key) can see it.
/// See docs/locked-album-format.md.
class IntruderCrypto {
  static String _b64(BigInt n) {
    var hex = n.toRadixString(16);
    if (hex.length.isOdd) hex = '0$hex';
    return base64.encode([for (var i = 0; i < hex.length; i += 2) int.parse(hex.substring(i, i + 2), radix: 16)]);
  }

  static BigInt _int(String b64) {
    final bytes = base64.decode(b64);
    var n = BigInt.zero;
    for (final b in bytes) {
      n = (n << 8) | BigInt.from(b);
    }
    return n;
  }

  static Uint8List _bytes(BigInt n, int length) {
    final out = Uint8List(length);
    var x = n;
    for (var i = length - 1; i >= 0; i--) {
      out[i] = (x & BigInt.from(0xff)).toInt();
      x = x >> 8;
    }
    return out;
  }

  /// A new 2048-bit key pair: (public JSON, private JSON). Slow (seconds): run it on another isolate.
  static (Map<String, String>, Map<String, String>) generate() {
    final seed = Sbox.randomBytes(32);
    final random = pc.FortunaRandom()..seed(pc.KeyParameter(seed));
    final gen = pc.RSAKeyGenerator()..init(pc.ParametersWithRandom(pc.RSAKeyGeneratorParameters(BigInt.from(65537), 2048, 64), random));
    final pair = gen.generateKeyPair();
    final priv = pair.privateKey as pc.RSAPrivateKey;
    final n = priv.modulus!, d = priv.exponent!, p = priv.p!, q = priv.q!;
    final e = BigInt.from(65537);
    final pub = {'N': _b64(n), 'E': _b64(e)};
    final private = {
      'N': _b64(n),
      'E': _b64(e),
      'D': _b64(d),
      'P': _b64(p),
      'Q': _b64(q),
      'DP': _b64(d % (p - BigInt.one)),
      'DQ': _b64(d % (q - BigInt.one)),
      'InverseQ': _b64(q.modInverse(p)),
    };
    return (pub, private);
  }

  static Uint8List _mgf1(List<int> seed, int length) {
    final out = BytesBuilder();
    for (var c = 0; out.length < length; c++) {
      out.add(hash.sha256.convert([...seed, (c >> 24) & 0xff, (c >> 16) & 0xff, (c >> 8) & 0xff, c & 0xff]).bytes);
    }
    return Uint8List.fromList(out.takeBytes().sublist(0, length));
  }

  static final _lHash = hash.sha256.convert(const <int>[]).bytes;

  /// RSA-OAEP-SHA256 (MGF1-SHA256, empty label) of a short message.
  static Uint8List encrypt(Map<String, dynamic> publicKey, List<int> message) {
    final n = _int('${publicKey['N']}'), e = _int('${publicKey['E']}');
    final k = (n.bitLength + 7) >> 3;
    const hLen = 32;
    if (message.length > k - 2 * hLen - 2) throw ArgumentError('message too long');
    final db = Uint8List(k - hLen - 1);
    db.setAll(0, _lHash);
    db[k - hLen - 1 - message.length - 1] = 1;
    db.setAll(k - hLen - 1 - message.length, message);
    final seed = Sbox.randomBytes(hLen);
    final dbMask = _mgf1(seed, db.length);
    final maskedDb = Uint8List.fromList([for (var i = 0; i < db.length; i++) db[i] ^ dbMask[i]]);
    final seedMask = _mgf1(maskedDb, hLen);
    final maskedSeed = [for (var i = 0; i < hLen; i++) seed[i] ^ seedMask[i]];
    final em = Uint8List.fromList([0, ...maskedSeed, ...maskedDb]);
    var m = BigInt.zero;
    for (final b in em) {
      m = (m << 8) | BigInt.from(b);
    }
    return _bytes(m.modPow(e, n), k);
  }

  static Uint8List decrypt(Map<String, dynamic> privateKey, List<int> cipher) {
    final n = _int('${privateKey['N']}');
    final p = _int('${privateKey['P']}'), q = _int('${privateKey['Q']}');
    final dp = _int('${privateKey['DP']}'), dq = _int('${privateKey['DQ']}'), qInv = _int('${privateKey['InverseQ']}');
    final k = (n.bitLength + 7) >> 3;
    var c = BigInt.zero;
    for (final b in cipher) {
      c = (c << 8) | BigInt.from(b);
    }
    // CRT
    final m1 = c.modPow(dp, p), m2 = c.modPow(dq, q);
    final h = (qInv * (m1 - m2)) % p;
    final m = m2 + h * q;
    final em = _bytes(m, k);
    const hLen = 32;
    final maskedSeed = em.sublist(1, 1 + hLen), maskedDb = em.sublist(1 + hLen);
    final seedMask = _mgf1(maskedDb, hLen);
    final seed = [for (var i = 0; i < hLen; i++) maskedSeed[i] ^ seedMask[i]];
    final dbMask = _mgf1(seed, maskedDb.length);
    final db = [for (var i = 0; i < maskedDb.length; i++) maskedDb[i] ^ dbMask[i]];
    var ok = em[0] == 0;
    for (var i = 0; i < hLen; i++) {
      ok &= db[i] == _lHash[i];
    }
    var i = hLen;
    while (i < db.length && db[i] == 0) {
      i++;
    }
    if (!ok || i >= db.length || db[i] != 1) throw const FormatException('Couldn\'t decrypt the photo\'s key.');
    return Uint8List.fromList(db.sublist(i + 1));
  }

  static const magic = [0x53, 0x42, 0x58, 0x49, 0x31]; // SBXI1

  /// "SBXI1", 2-byte length, the RSA-wrapped file key, then the JPEG as an .sbox with that key.
  static Future<Uint8List> seal(Map<String, dynamic> publicKey, List<int> jpeg) async {
    final fileKey = Sbox.randomBytes(32);
    final wrapped = encrypt(publicKey, fileKey);
    final body = await Sbox.encryptBytes(jpeg, fileKey);
    return Uint8List.fromList([...magic, wrapped.length >> 8, wrapped.length & 0xff, ...wrapped, ...body]);
  }

  static Future<Uint8List> open(Map<String, dynamic> privateKey, List<int> blob) async {
    for (var i = 0; i < magic.length; i++) {
      if (blob.length < 7 || blob[i] != magic[i]) throw const FormatException('Not a Shutterbox photo of a wrong guess.');
    }
    final n = (blob[5] << 8) | blob[6];
    final fileKey = decrypt(privateKey, blob.sublist(7, 7 + n));
    return Sbox.decryptBytes(blob.sublist(7 + n), fileKey);
  }

  /// "20261124T021400Z-ab12"
  static String newName(DateTime now) {
    final u = now.toUtc();
    String two(int x) => x.toString().padLeft(2, '0');
    final r = Random.secure().nextInt(1 << 16).toRadixString(16).padLeft(4, '0');
    return '${u.year}${two(u.month)}${two(u.day)}T${two(u.hour)}${two(u.minute)}${two(u.second)}Z-$r';
  }

  /// The time in a name made by [newName].
  static DateTime? timeOf(String name) {
    final m = RegExp(r'^(\d{4})(\d\d)(\d\d)T(\d\d)(\d\d)(\d\d)Z').firstMatch(name);
    if (m == null) return null;
    return DateTime.utc(int.parse(m[1]!), int.parse(m[2]!), int.parse(m[3]!), int.parse(m[4]!), int.parse(m[5]!), int.parse(m[6]!)).toLocal();
  }
}
