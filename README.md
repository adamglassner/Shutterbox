# Shutterbox

A photo organizer for Windows 10 and 11, by Bytehawk Studio.

Shutterbox shows the pictures already on your PC as one timeline, with albums, favorites, tags, search, a viewer and slideshows. It reads your folders and never moves or changes your files.

**Download:** get the latest release from [Releases](https://github.com/adamglassner/shutterbox/releases), or install it from the **Bytehawk Studio Launcher**, which also keeps it up to date.

- `Shutterbox-<version>-Setup.exe`: installer (Start menu shortcut, uninstaller; choose "Install for me only" if you don't have administrator rights)
- `Shutterbox-<version>-win-x64.zip`: portable, just unzip and run `Shutterbox.exe`

Neither needs .NET installed.

## Features

| | |
|---|---|
| **Library folders** | Choose the folders Shutterbox looks in (your Pictures folder, an external drive…). It finds the photos in them and their subfolders, and notices new, changed and deleted photos while it's open. If a drive is unplugged, its photos are kept until it's back. |
| **Timeline** | Newest first, grouped by **day, month or year**. Each day shows the folder it came from. Small, medium or large thumbnails. |
| **Details** | Date taken, camera, lens, exposure, size, GPS location (with a map link) and folder. Add a caption and tags. |
| **Search** | File names, folders, tags, captions, cameras, albums, months and years. Several words narrow it down (`canon portland`). |
| **Favorites** | Press **F** or click the heart. |
| **Albums** | A photo can be in any number of albums. Drag photos onto an album in the sidebar, or use **Add to album**. Pick any photo as the cover. |
| **Trash** | Deleted photos wait in Trash. Emptying it moves the files to the Windows Recycle Bin. |
| **Viewer** | Full window, zoom (mouse wheel or double-click) and pan, next/previous, rotate, favorite, edit in your photo editor, copy, open with. |
| **Slideshow** | Of everything, an album or a selection, oldest first, with crossfades. |
| **Import** | From a camera card, phone or folder. Copies into date folders (`2026\2026-10-14`), skips photos you already have and can clear the card afterwards. Or drop files and folders on the window. |
| **Looks** | Dark (the default, so photos stand out) or Light, with five accent colours. |

Rotating in Shutterbox doesn't change the file; it's remembered in the library.

### File types

JPEG, PNG, GIF, BMP, TIFF, WebP, JPEG XR and ICO work out of the box. HEIC/HEIF (iPhone), AVIF and camera RAW files (DNG, CR2, CR3, NEF, ARW…) use the codecs Windows has, so install **HEIF Image Extensions**, **AV1 Video Extension** or **Raw Image Extension** from the Microsoft Store if they don't show. Videos aren't supported yet.

### Keyboard

| Key | Does |
|---|---|
| Click, Ctrl+click, Shift+click | Select |
| Arrow keys, Home, End, Page Up/Down | Move (Shift extends the selection) |
| Enter or double-click | Open |
| Ctrl+A / Esc | Select all / clear |
| F | Favorite |
| R / Shift+R | Rotate right / left |
| Delete | Move to Trash |
| Ctrl+C | Copy the files |
| Ctrl+F | Search |
| Ctrl+I | Import |
| Ctrl+R | Rescan folders |
| F5 | Slideshow |
| I | Show or hide the details panel |

In the viewer: Left/Right, + and − to zoom, 0 to fit, Space for the slideshow, Esc to go back.

## Where things are kept

| | |
|---|---|
| `%APPDATA%\Shutterbox\library.json` | Your library: folders, photo details, favorites, tags, captions, rotation, albums, Trash |
| `%APPDATA%\Shutterbox\settings.json` | Settings |
| `%LOCALAPPDATA%\Shutterbox\thumbs\` | Thumbnail cache (safe to delete; Settings → Clear thumbnail cache) |

Uninstalling Shutterbox leaves these and your photos alone.

## Coming later

- **Shutterbox Server**: a server you run on your own PC or NAS to back up and sync your library and see it on your phone
- Android and iOS apps
- People (faces) and Places (map)
- Videos

## Building

Requires the .NET 8 SDK and, for the installer, Inno Setup 6.

```powershell
.\build.ps1                 # exe, portable zip and installer in dist\
.\build.ps1 -SkipInstaller  # exe and zip only
```

Every push is built and tested by GitHub Actions on Windows: it runs `Shutterbox.exe --self-test`, which makes a folder of sample photos and drives the real window through scanning, the timeline, search, albums, Trash, import, the viewer and both themes, saving screenshots. Pushing a new `<Version>` in `src/Shutterbox/Shutterbox.csproj` to `main` (with a matching `## <version>` section in `CHANGELOG.md`) publishes a GitHub Release, which the Bytehawk Studio Launcher picks up.

## Design

The original mockups (desktop, server admin, mobile) are in [`design/mockups`](design/mockups). They're source files for a Claude design canvas and need the canvas to render.

---

© 2026 Bytehawk Studio
