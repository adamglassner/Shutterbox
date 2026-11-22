# Changelog

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
