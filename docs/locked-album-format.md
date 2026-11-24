# The Locked album's files

Shutterbox for Windows and Shutterbox for Android use the same Locked album folder, so one album can move between
them or be shared through a cloud folder. This is the format both follow. `mobile/tool/vault_ref.py` is a small
reference implementation that the tests of both apps check against.

All JSON uses the property names shown (PascalCase). Dates without `Z` are local time; dates with `Z` are UTC.

## Encryption

- **Key:** a random 256-bit key encrypts everything with AES-256-GCM.
- **`.sbox` files:** `"SBOX1"`, a 12-byte nonce, then the content in 1 MiB pieces, each its 16-byte tag followed by the
  encrypted bytes. Piece *i* uses the nonce with its last 8 bytes XORed with *i* (big-endian from the end), and its
  associated data is *i* as a little-endian Int64 plus one byte, 1 for the last piece. A changed, reordered or cut-short
  file doesn't decrypt.
- **Wrapped keys:** base64 of nonce (12) ‖ tag (16) ‖ encrypted key (32).

## Files

| File | What |
|---|---|
| `vault.json` | Plain. `Version` 1, `Kind` ("PIN" or "Password"), `Salt`, `Iterations`, `WrappedKey` (the key wrapped with PBKDF2-SHA256 of the PIN or password), `RecoverySalt`, `RecoveryWrappedKey` (wrapped with PBKDF2-SHA256 of the recovery key: its letters and digits, upper case), `AutoLockMinutes`. Since 0.4.0 also `Id`: never changes, even when the recovery key does. |
| `items/<id>.sbox` | The photos and videos. |
| `thumbs/<id>.sbox` | A small upright JPEG of each. |
| `records/<id>.sbox` | Since 0.4.0, one per item: its details (below). |
| `albums/<id>.sbox` | Since 0.4.0, one per album inside the Locked album (below). |
| `protected/<album id>/…` | Since 0.4.0, an album with its own PIN: `records/`, `items/` and `thumbs/` like above, encrypted with that album's own key. |
| `index.sbox` | The list of items and albums in one file (version 2: `{"Version":2,"Items":[…],"Albums":[…]}`; version 1 was just the list of items). Shutterbox 0.2.0 and 0.3.0 read only this. Since 0.4.0 it's still written, for them, but `records/` and `albums/` are what count. |
| `attempts/<device id>.json` | Since 0.4.0, plain: `{"Device": name, "LastUnlock": UTC date, "Failed": [{"Time": UTC date, "What": "PIN" / "Password" / "Recovery key" / "Album PIN"}]}` (the last 50). |
| `intruder.json`, `intruder-key.sbox`, `intruders/` | Since 0.4.0, when "Photograph wrong guesses" is on (below). |

### Records: one file per item, so two devices can change the album at once

A cloud folder copies whole files. With one index file, a phone and a PC that both changed the album would overwrite
each other's changes. With a file per item, each change touches only its own file, so changes on different items
merge by themselves.

`records/<id>.sbox` holds the item's details: `Id`, `Name`, `Size`, `Taken`, `TakenSource`, `Width`, `Height`,
`DurationSeconds`, `Make`, `Model`, `Latitude`, `Longitude`, `Orientation`, `Rotation`, `Added`, `HasThumb` (as in the
index), plus `Caption`, `Tags` (list), `Favorite`, `AlbumIds` (list), `Modified` (UTC) and `Deleted`.

- An album's contents are the items whose `AlbumIds` list it, so adding items to an album on two devices merges too.
- Deleting writes the record with `Deleted: true` (and deletes the item and thumbnail), so a device that hasn't seen the
  delete yet doesn't bring it back.
- A sync app's conflict copy (`<id> (1).sbox`, `<id>-conflict-….sbox`…) is read like any record, and the one with the
  latest `Modified` wins.

`albums/<id>.sbox` holds `Id`, `Name`, `Created`, `Modified` (UTC), `Deleted`, `CoverId` and `Protected`: null, or
`{"Kind", "Salt", "Iterations", "WrappedKey"}` for an album with its own PIN or password, whose key (a separate random
256-bit key) is wrapped like the main one. Its items live in `protected/<album id>/`, encrypted with that key, and never
appear in `index.sbox`. If its PIN is forgotten, its items can't be recovered.

### Reading an album made by an older version

`index.sbox` is read too. An item there with no record (added by 0.2.0 or 0.3.0) whose file exists becomes a record,
and so does an album there with no album file. An item that has a record (even a deleted one) is never taken from the
index.

### Wrong guesses

Each device keeps its own `attempts/<device id>.json`. On unlocking, Shutterbox shows the failed attempts from all
devices since the latest `LastUnlock`. Making you wait after several wrong guesses is done on each device; the real
protection against guessing is the 600,000 PBKDF2 rounds each guess costs.

**Photograph wrong guesses** (optional): `intruder.json` is plain `{"PublicKey": {"N": …, "E": …}}`, a 2048-bit RSA public key
(big-endian unsigned integers, base64). Its private key, `{"N","E","D","P","Q","DP","DQ","InverseQ"}`, is in
`intruder-key.sbox`, encrypted with the album key. A photo taken after a wrong guess, while the album is locked, is
`intruders/<yyyyMMddTHHmmssZ>-<random>.sbox`: `"SBXI1"`, a 2-byte big-endian length, the RSA-OAEP-SHA256 encryption of
a random 256-bit file key, then the JPEG as an `.sbox` with that file key. Only someone who can unlock the album can
see it.
