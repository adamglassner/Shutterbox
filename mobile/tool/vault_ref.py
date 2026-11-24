#!/usr/bin/env python3
"""A reference copy of the Windows app's Locked album format (src/Shutterbox/Library/Vault.cs), for testing the phone's.

  python3 tool/vault_ref.py make <folder>      writes a Locked album exactly as Shutterbox 0.3.0 for Windows does (test fixture)
  python3 tool/vault_ref.py check <folder>     opens a Locked album the phone app made and checks every file decrypts
  python3 tool/vault_ref.py make4 <folder>     writes a 0.4.0 Locked album (records, albums, a protected album, a sync
                                               conflict copy, a deleted item, an item only in the old index, wrong
                                               guesses and a photographed one): see docs/locked-album-format.md
  python3 tool/vault_ref.py check4 <folder>    checks a 0.4.0 album an app made (PIN 2468, recovery key in recovery.txt,
                                               protected album PIN 9999)

Format: vault.json (plain), index.sbox, items/<id>.sbox, thumbs/<id>.sbox. Each .sbox is "SBOX1", a 12-byte nonce, then
1 MiB pieces, each its 16-byte AES-256-GCM tag followed by the encrypted bytes. A piece's nonce is the base nonce with
the last 8 bytes XORed with its index (big-endian from the end); its associated data is the index as a little-endian
Int64 plus 1 if it's the last piece. The key is wrapped (nonce | tag | cipher, base64) with PBKDF2-SHA256 of the PIN.
"""
import base64
import hashlib
import json
import os
import sys

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

MAGIC = b"SBOX1"
CHUNK = 1 << 20

# Fixed values so the fixture is reproducible
PIN = "2468"
RECOVERY = "ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2"
ITERATIONS = 1000  # the real album uses 600,000; vault.json says how many, and the phone uses what it says


def derive(secret, salt, iterations):
    return hashlib.pbkdf2_hmac("sha256", secret.encode("utf-8"), salt, iterations, 32)


def normalize_recovery(k):
    return "".join(c for c in k.upper() if c.isalnum())


def wrap(key, wrapping_key, nonce):
    ct = AESGCM(wrapping_key).encrypt(nonce, key, None)  # cipher | tag
    return base64.b64encode(nonce + ct[-16:] + ct[:-16]).decode()


def unwrap(blob, wrapping_key):
    d = base64.b64decode(blob)
    return AESGCM(wrapping_key).decrypt(d[:12], d[28:] + d[12:28], None)


def chunk_nonce(base, index):
    n = bytearray(base)
    for i in range(8):
        n[11 - i] ^= (index >> (8 * i)) & 0xFF
    return bytes(n)


def chunk_aad(index, final):
    return index.to_bytes(8, "little", signed=True) + bytes([1 if final else 0])


def encrypt(data, key, base_nonce):
    out = bytearray(MAGIC + base_nonce)
    aes = AESGCM(key)
    pieces = [data[i:i + CHUNK] for i in range(0, len(data), CHUNK)] or [b""]
    for index, piece in enumerate(pieces):
        final = index == len(pieces) - 1
        ct = aes.encrypt(chunk_nonce(base_nonce, index), piece, chunk_aad(index, final))
        out += ct[-16:] + ct[:-16]
    return bytes(out)


def decrypt(blob, key):
    if blob[:5] != MAGIC:
        raise ValueError("not a Shutterbox Locked album file")
    base = blob[5:17]
    pos, index, out = 17, 0, bytearray()
    aes = AESGCM(key)
    while True:
        if pos + 16 > len(blob):
            raise ValueError("cut short")
        tag = blob[pos:pos + 16]
        ct = blob[pos + 16:pos + 16 + CHUNK]
        pos += 16 + len(ct)
        final = pos >= len(blob)
        out += aes.decrypt(chunk_nonce(base, index), ct + tag, chunk_aad(index, final))
        if final:
            return bytes(out)
        index += 1


def det(n, seed):
    """Deterministic bytes."""
    out = bytearray()
    h = seed.encode()
    while len(out) < n:
        h = hashlib.sha256(h).digest()
        out += h
    return bytes(out[:n])


def make(folder):
    key = det(32, "key")
    salt, rsalt = det(16, "salt"), det(16, "rsalt")
    os.makedirs(os.path.join(folder, "items"), exist_ok=True)
    os.makedirs(os.path.join(folder, "thumbs"), exist_ok=True)
    info = {
        "Version": 1, "Kind": "PIN",
        "Salt": base64.b64encode(salt).decode(), "Iterations": ITERATIONS,
        "WrappedKey": wrap(key, derive(PIN, salt, ITERATIONS), det(12, "n1")),
        "RecoverySalt": base64.b64encode(rsalt).decode(),
        "RecoveryWrappedKey": wrap(key, derive(normalize_recovery(RECOVERY), rsalt, ITERATIONS), det(12, "n2")),
        "AutoLockMinutes": 5,
    }
    with open(os.path.join(folder, "vault.json"), "w", encoding="utf-8") as f:
        f.write(json.dumps(info, indent=2))  # Windows writes it indented too

    files = {
        # id: (name, bytes)
        "0123456789abcdef0123456789abcdef": ("IMG_2041.jpg", det(5000, "photo")),
        "fedcba9876543210fedcba9876543210": ("Beach day.mp4", det(CHUNK * 2 + 123, "video")),  # three pieces
        "00000000000000000000000000000001": ("exact.png", det(CHUNK, "exact")),  # exactly one full piece
        "00000000000000000000000000000002": ("empty.gif", b""),
    }
    for i, (fid, (name, data)) in enumerate(files.items()):
        with open(os.path.join(folder, "items", fid + ".sbox"), "wb") as f:
            f.write(encrypt(data, key, det(12, "file" + fid)))
    with open(os.path.join(folder, "thumbs", "0123456789abcdef0123456789abcdef.sbox"), "wb") as f:
        f.write(encrypt(det(700, "thumb"), key, det(12, "thumb")))

    # System.Text.Json: PascalCase, compact; DateTime.Now has an offset and 7 decimals, file dates don't
    items = [
        {"Id": "0123456789abcdef0123456789abcdef", "Name": "IMG_2041.jpg", "Size": 5000, "Taken": "2026-10-14T09:12:30",
         "TakenSource": "exif", "Width": 4032, "Height": 3024, "DurationSeconds": 0, "Make": "Canon", "Model": "EOS R6",
         "Latitude": 45.5231, "Longitude": -122.6765, "Orientation": 6, "Rotation": 90,
         "Added": "2026-11-20T10:00:00.1234567-08:00", "HasThumb": True},
        {"Id": "fedcba9876543210fedcba9876543210", "Name": "Beach day.mp4", "Size": CHUNK * 2 + 123, "Taken": "2026-07-04T16:45:00",
         "TakenSource": "file", "Width": 1920, "Height": 1080, "DurationSeconds": 12.48, "Make": None, "Model": None,
         "Latitude": None, "Longitude": None, "Orientation": 1, "Rotation": 0,
         "Added": "2026-11-20T10:00:01.5-08:00", "HasThumb": False},
        {"Id": "00000000000000000000000000000001", "Name": "exact.png", "Size": CHUNK, "Taken": "2025-01-02T03:04:05",
         "TakenSource": "name", "Width": 10, "Height": 10, "DurationSeconds": 0, "Make": None, "Model": None,
         "Latitude": None, "Longitude": None, "Orientation": 1, "Rotation": 0,
         "Added": "2026-11-20T10:00:02-08:00", "HasThumb": False},
        {"Id": "00000000000000000000000000000002", "Name": "empty.gif", "Size": 0, "Taken": "2025-01-02T03:04:05",
         "TakenSource": "file", "Width": 0, "Height": 0, "DurationSeconds": 0, "Make": None, "Model": None,
         "Latitude": None, "Longitude": None, "Orientation": 1, "Rotation": 0,
         "Added": "2026-11-20T10:00:03-08:00", "HasThumb": False},
    ]
    index = {"Version": 2, "Items": items, "Albums": [
        {"Id": "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", "Name": "Private", "ItemIds": ["0123456789abcdef0123456789abcdef", "gone-item"],
         "Created": "2026-11-20T10:05:00.0000001-08:00"},
    ]}
    with open(os.path.join(folder, "index.sbox"), "wb") as f:
        f.write(encrypt(json.dumps(index, separators=(",", ":")).encode(), key, det(12, "index")))

    # Version 1 index (a plain list), as Shutterbox 0.2.0 wrote it
    with open(os.path.join(folder, "index-v1.sbox"), "wb") as f:
        f.write(encrypt(json.dumps(items[:1], separators=(",", ":")).encode(), key, det(12, "index1")))

    with open(os.path.join(folder, "expected.json"), "w") as f:
        json.dump({fid: hashlib.sha256(data).hexdigest() for fid, (name, data) in files.items()}, f, indent=1)
    print("made", folder)


def check(folder):
    """Open a Locked album with the PIN 2468 and decrypt everything in it."""
    with open(os.path.join(folder, "vault.json"), encoding="utf-8") as f:
        info = json.load(f)
    for k in ["Version", "Kind", "Salt", "Iterations", "WrappedKey", "RecoverySalt", "RecoveryWrappedKey", "AutoLockMinutes"]:
        assert k in info, f"vault.json is missing {k}"
    assert isinstance(info["Iterations"], int) and isinstance(info["AutoLockMinutes"], int)
    key = unwrap(info["WrappedKey"], derive(PIN, base64.b64decode(info["Salt"]), info["Iterations"]))
    key2 = unwrap(info["RecoveryWrappedKey"], derive(normalize_recovery(open(os.path.join(folder, "recovery.txt")).read()),
                                                       base64.b64decode(info["RecoverySalt"]), info["Iterations"]))
    assert key == key2, "the recovery key opens a different key"
    with open(os.path.join(folder, "index.sbox"), "rb") as f:
        index = json.loads(decrypt(f.read(), key))
    assert index["Version"] == 2
    ints = ["Size", "Width", "Height", "Orientation", "Rotation"]
    for item in index["Items"]:
        for k in ["Id", "Name", "Size", "Taken", "TakenSource", "Width", "Height", "DurationSeconds", "Make", "Model",
                  "Latitude", "Longitude", "Orientation", "Rotation", "Added", "HasThumb"]:
            assert k in item, f"item is missing {k}"
        for k in ints:
            assert isinstance(item[k], int) and not isinstance(item[k], bool), f"{k} must be a whole number for Windows: {item[k]!r}"
        with open(os.path.join(folder, "items", item["Id"] + ".sbox"), "rb") as f:
            data = decrypt(f.read(), key)
        assert len(data) == item["Size"], f"{item['Name']}: {len(data)} bytes, expected {item['Size']}"
        if item["HasThumb"]:
            with open(os.path.join(folder, "thumbs", item["Id"] + ".sbox"), "rb") as f:
                decrypt(f.read(), key)
        print("ok", item["Name"], len(data), "bytes")
    for a in index["Albums"]:
        assert set(a) >= {"Id", "Name", "ItemIds", "Created"}
        print("album", a["Name"], len(a["ItemIds"]))
    print("The phone's Locked album opens with the Windows format")


# ---------------------------------------------------------------- 0.4.0

ALBUM_PIN = "9999"
RECORD_KEYS = ["Id", "Name", "Size", "Taken", "TakenSource", "Width", "Height", "DurationSeconds", "Make", "Model",
               "Latitude", "Longitude", "Orientation", "Rotation", "Added", "HasThumb",
               "Caption", "Tags", "Favorite", "AlbumIds", "Modified", "Deleted"]


def b64(b):
    return base64.b64encode(b).decode()


def unb64(s):
    return base64.b64decode(s)


def int_b64(n):
    return b64(n.to_bytes((n.bit_length() + 7) // 8 or 1, "big"))


def b64_int(s):
    return int.from_bytes(unb64(s), "big")


def record(item, **extra):
    r = dict(item)
    r.update({"Caption": "", "Tags": [], "Favorite": False, "AlbumIds": [], "Modified": "2026-11-24T05:00:00Z", "Deleted": False})
    r.update(extra)
    return r


def put(folder, rel, obj_or_bytes, key, seed):
    path = os.path.join(folder, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    data = obj_or_bytes if isinstance(obj_or_bytes, (bytes, bytearray)) else json.dumps(obj_or_bytes, separators=(",", ":")).encode()
    with open(path, "wb") as f:
        f.write(encrypt(data, key, det(12, seed)))


def make4(folder):
    """A 0.4.0 Locked album, as the apps write it (see docs/locked-album-format.md)."""
    from cryptography.hazmat.primitives.asymmetric import rsa, padding
    from cryptography.hazmat.primitives import hashes
    make(folder)  # the 0.3.0 album: vault.json, items, index.sbox
    key = det(32, "key")
    with open(os.path.join(folder, "vault.json")) as f:
        info = json.load(f)
    info["Id"] = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f"
    with open(os.path.join(folder, "vault.json"), "w") as f:
        f.write(json.dumps(info, indent=2))
    with open(os.path.join(folder, "index.sbox"), "rb") as f:
        index = json.loads(decrypt(f.read(), key))
    items = {i["Id"]: i for i in index["Items"]}
    photo, video, exact, empty = list(items)
    # records for three of the four: "empty.gif" is only in the index (added by 0.3.0 after the upgrade)
    put(folder, f"records/{photo}.sbox", record(items[photo], Caption="Sunrise at the coast", Tags=["Beach", "Trip"], Favorite=True,
                                               AlbumIds=["a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"]), key, "r1")
    # a sync conflict copy of the photo's record, older: loses
    put(folder, f"records/{photo} (1).sbox", record(items[photo], Caption="older caption", Modified="2026-11-20T05:00:00Z"), key, "r1c")
    # the video's record, and a newer conflict copy that wins (rotated, in an album)
    put(folder, f"records/{video}.sbox", record(items[video], Modified="2026-11-22T05:00:00Z"), key, "r2")
    put(folder, f"records/{video}-conflict-PIXEL.sbox", record(items[video], Rotation=180, AlbumIds=["a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"],
                                                                 Modified="2026-11-23T05:00:00Z"), key, "r2c")
    # "exact.png" was deleted on another device: its record says so, so the stale index doesn't bring it back
    put(folder, f"records/{exact}.sbox", record(items[exact], Deleted=True), key, "r3")
    os.remove(os.path.join(folder, "items", exact + ".sbox"))
    # albums: "Private" (renamed on a device to "Private stuff"), a deleted one, and one with its own PIN
    put(folder, "albums/a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1.sbox", {"Id": "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", "Name": "Private stuff",
        "Created": "2026-11-20T10:05:00", "Modified": "2026-11-24T05:00:00Z", "Deleted": False, "CoverId": video, "Protected": None}, key, "a1")
    put(folder, "albums/b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2.sbox", {"Id": "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", "Name": "Gone",
        "Created": "2026-11-21T10:05:00", "Modified": "2026-11-24T05:00:00Z", "Deleted": True, "CoverId": None, "Protected": None}, key, "a2")
    akey, asalt = det(32, "album key"), det(16, "album salt")
    put(folder, "albums/c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3.sbox", {"Id": "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3", "Name": "Very private",
        "Created": "2026-11-22T10:05:00", "Modified": "2026-11-24T05:00:00Z", "Deleted": False, "CoverId": None,
        "Protected": {"Kind": "PIN", "Salt": b64(asalt), "Iterations": ITERATIONS,
                      "WrappedKey": wrap(akey, derive(ALBUM_PIN, asalt, ITERATIONS), det(12, "n3"))}}, key, "a3")
    secret = det(2000, "secret photo")
    pid = "d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4"
    base = os.path.join(folder, "protected", "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3")
    put(base, f"items/{pid}.sbox", secret, akey, "p1")
    put(base, f"records/{pid}.sbox", record({**items[photo], "Id": pid, "Name": "secret.jpg", "Size": len(secret), "HasThumb": False, "Rotation": 0}), akey, "p2")
    # wrong guesses: one on a PC since the last unlock
    os.makedirs(os.path.join(folder, "attempts"), exist_ok=True)
    with open(os.path.join(folder, "attempts", "pc-0001.json"), "w") as f:
        json.dump({"Device": "Desktop PC", "LastUnlock": "2026-11-23T20:00:00Z",
                   "Failed": [{"Time": "2026-11-23T19:00:00Z", "What": "PIN"}, {"Time": "2026-11-24T02:14:00Z", "What": "PIN"}]}, f)
    # photographing wrong guesses
    priv = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    n = priv.private_numbers()
    with open(os.path.join(folder, "intruder.json"), "w") as f:
        json.dump({"PublicKey": {"N": int_b64(n.public_numbers.n), "E": int_b64(n.public_numbers.e)}}, f)
    put(folder, "intruder-key.sbox", {"N": int_b64(n.public_numbers.n), "E": int_b64(n.public_numbers.e), "D": int_b64(n.d),
        "P": int_b64(n.p), "Q": int_b64(n.q), "DP": int_b64(n.dmp1), "DQ": int_b64(n.dmq1), "InverseQ": int_b64(n.iqmp)}, key, "ik")
    os.makedirs(os.path.join(folder, "intruders"), exist_ok=True)
    fkey = det(32, "intruder file key")
    wrapped = priv.public_key().encrypt(fkey, padding.OAEP(mgf=padding.MGF1(hashes.SHA256()), algorithm=hashes.SHA256(), label=None))
    jpeg = det(3000, "intruder photo")
    with open(os.path.join(folder, "intruders", "20261124T021400Z-abcd.sbox"), "wb") as f:
        f.write(b"SBXI1" + len(wrapped).to_bytes(2, "big") + wrapped + encrypt(jpeg, fkey, det(12, "ij")))
    with open(os.path.join(folder, "expected4.json"), "w") as f:
        json.dump({"secret": hashlib.sha256(secret).hexdigest(), "intruder": hashlib.sha256(jpeg).hexdigest()}, f)
    print("made 0.4.0 album", folder)


def read_intruder(path, priv_json):
    from cryptography.hazmat.primitives.asymmetric import rsa, padding
    from cryptography.hazmat.primitives import hashes
    p = {k: b64_int(v) for k, v in priv_json.items()}
    priv = rsa.RSAPrivateNumbers(p["P"], p["Q"], p["D"], p["DP"], p["DQ"], p["InverseQ"], rsa.RSAPublicNumbers(p["E"], p["N"])).private_key()
    with open(path, "rb") as f:
        blob = f.read()
    assert blob[:5] == b"SBXI1"
    n = int.from_bytes(blob[5:7], "big")
    fkey = priv.decrypt(blob[7:7 + n], padding.OAEP(mgf=padding.MGF1(hashes.SHA256()), algorithm=hashes.SHA256(), label=None))
    return decrypt(blob[7 + n:], fkey)


def check4(folder):
    """A 0.4.0 album an app made: PIN 2468, recovery key in recovery.txt, a protected album with PIN 9999."""
    check(folder)  # everything 0.3.0 can see still works
    with open(os.path.join(folder, "vault.json")) as f:
        info = json.load(f)
    assert info.get("Id"), "vault.json has no Id"
    key = unwrap(info["WrappedKey"], derive(PIN, unb64(info["Salt"]), info["Iterations"]))
    records = {}
    for name in os.listdir(os.path.join(folder, "records")):
        with open(os.path.join(folder, "records", name), "rb") as f:
            r = json.loads(decrypt(f.read(), key))
        for k in RECORD_KEYS:
            assert k in r, f"record {name} is missing {k}"
        assert r["Modified"].endswith("Z"), "Modified must be UTC"
        assert isinstance(r["Tags"], list) and isinstance(r["AlbumIds"], list)
        for k in ["Size", "Width", "Height", "Orientation", "Rotation"]:
            assert isinstance(r[k], int) and not isinstance(r[k], bool), f"{k} must be a whole number"
        assert name == r["Id"] + ".sbox", f"stray record file {name}"
        records[r["Id"]] = r
    with open(os.path.join(folder, "index.sbox"), "rb") as f:
        index = json.loads(decrypt(f.read(), key))
    live = {i for i, r in records.items() if not r["Deleted"]}
    assert {i["Id"] for i in index["Items"]} == live, "index.sbox must list exactly the items that aren't deleted"
    for i in live:
        assert os.path.exists(os.path.join(folder, "items", i + ".sbox"))
    albums = {}
    for name in os.listdir(os.path.join(folder, "albums")):
        with open(os.path.join(folder, "albums", name), "rb") as f:
            a = json.loads(decrypt(f.read(), key))
        for k in ["Id", "Name", "Created", "Modified", "Deleted", "CoverId", "Protected"]:
            assert k in a, f"album {name} is missing {k}"
        albums[a["Id"]] = a
    protected = [a for a in albums.values() if a["Protected"] and not a["Deleted"]]
    for a in protected:
        pr = a["Protected"]
        akey = unwrap(pr["WrappedKey"], derive(ALBUM_PIN, unb64(pr["Salt"]), pr["Iterations"]))
        base = os.path.join(folder, "protected", a["Id"])
        for name in os.listdir(os.path.join(base, "records")):
            with open(os.path.join(base, "records", name), "rb") as f:
                r = json.loads(decrypt(f.read(), akey))
            if r["Deleted"]:
                continue
            with open(os.path.join(base, "items", r["Id"] + ".sbox"), "rb") as f:
                assert len(decrypt(f.read(), akey)) == r["Size"]
            assert r["Id"] not in {i["Id"] for i in index["Items"]}, "a protected album's items must not be in index.sbox"
        print("protected album", a["Name"], "opens with its PIN")
    if os.path.exists(os.path.join(folder, "intruder.json")):
        with open(os.path.join(folder, "intruder-key.sbox"), "rb") as f:
            priv = json.loads(decrypt(f.read(), key))
        for name in os.listdir(os.path.join(folder, "intruders")):
            read_intruder(os.path.join(folder, "intruders", name), priv)
            print("intruder photo", name, "decrypts")
    for name in os.listdir(os.path.join(folder, "attempts")) if os.path.isdir(os.path.join(folder, "attempts")) else []:
        with open(os.path.join(folder, "attempts", name)) as f:
            a = json.load(f)
        assert "Failed" in a and "Device" in a
    print(f"0.4.0 album OK: {len(live)} items, {len(albums)} albums, {len(protected)} protected")


if __name__ == "__main__":
    {"make": make, "check": check, "make4": make4, "check4": check4}[sys.argv[1]](sys.argv[2])
