#!/usr/bin/env python3
"""A reference copy of the Windows app's Locked album format (src/Shutterbox/Library/Vault.cs), for testing the phone's.

  python3 tool/vault_ref.py make <folder>    writes a Locked album exactly as Shutterbox for Windows does (test fixture)
  python3 tool/vault_ref.py check <folder>   opens a Locked album the phone app made and checks every file decrypts

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


if __name__ == "__main__":
    {"make": make, "check": check}[sys.argv[1]](sys.argv[2])
