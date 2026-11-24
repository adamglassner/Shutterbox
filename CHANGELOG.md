# Changelog

## 0.5.0

- **Places:** every photo with a location on a map (OpenStreetMap). Drag to move, scroll to zoom; pins group photos taken close together and split as you zoom in. Click one to see its photos. Map tiles are cached on your PC.
- **On this day:** photos and videos taken on today's date in earlier years, in the sidebar.

## 0.4.0

Locked album:

- **Works on a PC and a phone at once through a cloud folder.** Each item's details now have their own encrypted file, so changes made on different devices merge instead of overwriting each other (sync apps' conflict copies are sorted out too). It's the same format as Shutterbox for Android. Albums from 0.3.0 open as before, and 0.3.0 can still read what's added.
- **Kept out of screenshots, recordings and screen sharing** while it's unlocked (on by default).
- **Lock it when you leave:** when Shutterbox is minimized, you switch to another app or Windows locks (optional). **Ctrl+Alt+L** anywhere locks it and hides Shutterbox.
- **Hide Locked in the sidebar:** open it with Ctrl+Shift+L, or by typing your PIN or password in search and pressing Enter.
- **Lock with Shutterbox** when you right-click photos and videos in File Explorer (Windows 11: Show more options).
- **Share for a moment:** Copy or Open with… makes temporary decrypted copies, deleted when it locks.
- **Wrong guesses:** after 5 wrong guesses in a row Shutterbox makes you wait, longer each time; every wrong guess on any device is logged, and you're told about them when you unlock. Optionally, the camera **photographs wrong guesses**, encrypted so only you can see them.
- **Albums with their own PIN or password**, re-encrypted with their own key.
- **Captions, tags, favorites and search** for Locked album items, all encrypted.
- **Check the album** (every file decrypts) and **get a new recovery key** in Settings.

## 0.3.0 (2026-11-22)

- **Albums inside the Locked album.** Tabs at the top of the Locked album: All, your albums, and New album. Add locked items to albums, remove them, rename or delete an album (its items stay in the Locked album). Album names are encrypted with everything else and only show while it's unlocked.
- **Copy to Locked album or Move to Locked album.** Both are in the Lock menu (selection bar) and the right-click menu, straight into any of its albums. **Copy** leaves your originals where they are. **Move** encrypts each item, checks the encrypted copy decrypts back exactly, and only then deletes the original from your PC (not to the Recycle Bin, so no unlocked copy is left behind).
- **Selecting lots of photos and videos is much easier:**
  - a **check circle** on every tile (it shows when you point at one): click it to add or remove that item, no Ctrl needed;
  - **Select mode** (the check button at the top): every click adds or removes, and dragging across tiles selects them all;
  - **drag a box** from any empty space to select everything it touches; it scrolls when you reach the top or bottom, and Ctrl adds to what's already selected;
  - **Select all** in the selection bar, **Select** on each day, and **Space** to add or remove the photo you're on with the keyboard (Shift+arrows still extend).

## 0.2.0 (2026-11-22)

- **Videos.** Shutterbox now finds the videos in your folders (MP4, MOV, M4V, WMV, AVI, MKV, WebM, 3GP, MTS and more) and keeps them in their own **Videos** section, so the Photos timeline stays photos only. Video tiles show their length, and the viewer plays them with play/pause, a seek bar and mute. Importing from a camera or phone brings its videos along too. Slideshows skip videos.
- **Locked album.** Copy photos and videos into an encrypted album that can only be opened in Shutterbox. Unlock it with a **PIN**, a **password** or **Windows Hello** (fingerprint, face or Windows PIN), the same way WillowWallet does. A recovery key, shown once when you set it up, gets you in if you forget your PIN or password.
  - Everything is encrypted with AES-256-GCM, in 1 MB pieces so long videos are fine, and names, dates and thumbnails are encrypted too. Nothing in the album can be opened outside Shutterbox.
  - Choose **Lock** on any selection, drag photos onto **Locked** in the sidebar, or use **Add files**. Your originals stay where they are; Shutterbox offers to move them to the Recycle Bin afterwards.
  - It locks itself after a few minutes without use (you choose how long), when you click Lock now, and when you close Shutterbox.
  - **Restore** decrypts items back to a folder; **Delete** removes them from the album for good.
  - **Take it with you.** The album is one folder. Move it into OneDrive, Dropbox, Google Drive or onto a USB drive from Settings, and on another PC choose "Use a Locked album from another PC" and unlock it with your PIN, password or recovery key.
- The details panel shows a video's length.

## 0.1.0 (2026-11-12)

First release of Shutterbox, a photo organizer for Windows 10 and 11. Everything stays on your PC; the Shutterbox Server comes in a later version.

- **Your folders, your files.** Point Shutterbox at the folders where your pictures already are (your Pictures folder, an external drive…). It finds every photo in them and their subfolders, and notices new, changed and deleted photos while it's open. Your files are never moved or changed.
- **Timeline.** All your photos, newest first, grouped by day, month or year. Each day shows the folder it came from. Small, medium or large thumbnails.
- **Details panel.** Date taken, camera, lens, exposure, size, GPS location (with a map link) and folder. Add a caption and tags.
- **Search** by file name, folder, tag, caption, camera, album, month or year.
- **Favorites, albums and Trash.** Mark favorites with F. Make albums and drag photos onto them in the sidebar. Deleted photos wait in Trash; emptying it moves the files to the Windows Recycle Bin.
- **Viewer and slideshow.** Full-window viewer with zoom and pan, next and previous, rotate, favorite, and a slideshow with crossfades.
- **Import** from a camera card, phone or folder. Shutterbox copies the photos into date folders and skips the ones you already have.
- **Reads** JPEG, PNG, GIF, BMP, TIFF, WebP and more. HEIC, AVIF and camera RAW files work when their Windows extensions from the Microsoft Store are installed.
- Dark and Light themes with five accent colours, and keyboard shortcuts for everything.
