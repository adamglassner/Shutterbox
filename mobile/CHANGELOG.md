# Shutterbox for Android

## 0.4.0

Locked album:

- **Works on a PC and a phone at once through a cloud folder.** Each item's details now have their own encrypted file, so changes made on different devices merge instead of overwriting each other (a sync app's conflict copies are sorted out too). Albums from 0.3.0 open as before, and 0.3.0 can still read what's added.
- **Keep it out of screenshots:** while it's unlocked, screenshots and screen recordings come out black and the recent-apps preview is blank (on by default).
- **Lock as soon as you leave** Shutterbox (optional), **lock when the phone is put face down** (optional), and a lock button that also takes you back to Photos.
- **Hide the Locked tab:** open it by pressing and holding "Photos" at the top, or by typing your PIN or password in search.
- **Lock with Shutterbox** from Android's share menu, and a **camera** in the Locked album whose photos go straight in without touching the gallery.
- **Share for a moment:** sends temporary decrypted copies, deleted when the album locks.
- **Wrong guesses:** after 5 wrong guesses in a row Shutterbox makes you wait, longer each time; every wrong guess on any device is logged and you're told about them when you unlock. Optionally, the front camera **photographs wrong guesses**, encrypted so only you can see them.
- **Albums with their own PIN or password**, re-encrypted with their own key.
- **Captions, tags, favorites and search** inside the Locked album, all encrypted.
- **Check the album** (every file decrypts) and **get a new recovery key** in Settings.

## 0.3.0

The first Shutterbox for Android, with everything Shutterbox 0.3.0 for Windows has:

- **Your photos and videos**: everything in your phone's gallery, newest first, grouped by day, month or year (each day shows the folder it came from). Videos have their own section, apart from your photos. Choose which folders Shutterbox shows in Settings → Folders.
- **Viewer**: swipe between photos, pinch or double-tap to zoom, play videos (play/pause, seek, mute), favorite, rotate (remembered in Shutterbox; the file isn't changed), share, open in another app to edit, delete.
- **Details**: date, camera, lens and exposure, size, location with a map link, folder, caption and tags.
- **Search** names, folders, tags, captions, cameras, albums, months and years. Several words narrow it down.
- **Albums** of your own (rename, delete, pick a cover), **Favorites**, **Recently added**, **Folders** and **Trash** (emptying it deletes from your phone, into Android's own trash where it has one).
- **Slideshow** of everything, an album or a selection, oldest first, with crossfades.
- **Import** a folder (a camera card or USB drive with an adapter) or chosen files into date folders (DCIM/Shutterbox/2026/2026-10-14), skipping what you already have, and optionally clearing the card afterwards.
- **Easy selecting**: tap the check circles, long-press and drag across tiles (it scrolls as you go), Select on a day's heading, Select all.
- **Locked album**: an encrypted album opened with a PIN or password, and your fingerprint or face. Copy to Locked album or Move to Locked album (encrypted and checked before the original is deleted), albums inside it, restore, delete, auto-lock, change PIN, recovery key, move it to another folder.
- **Open existing Locked albums**: it's the same format as Shutterbox for Windows. Copy the "Shutterbox Locked album" folder from your PC to your phone (or the other way) and open it with its PIN, password or recovery key.
- **Updates from GitHub**: Settings → Updates → Check for updates downloads the new version and opens Android's installer. Shutterbox also checks once a day.
- Dark (the default) or light theme, five accent colours, small/medium/large thumbnails.
