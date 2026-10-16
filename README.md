# Shutterbox

A photo collection and organization app with clients for every device and a server you host yourself.

## Components

| Component | Platforms | Role |
|---|---|---|
| Desktop client | Windows, macOS, Linux | Browse, search, import and edit your library |
| Mobile client | Android, iOS | Browse your library and back up photos from your phone |
| Server | Self-hosted (Docker) | Stores originals, generates thumbnails, indexes faces and places, syncs every client |

## Status

Design stage. Mockups for the first screens are in [`design/mockups`](design/mockups).

## Mockups

| Screen | File |
|---|---|
| Desktop library: sidebar, day-by-day timeline, photo info panel | [`Main.dc.html`](design/mockups/Main.dc.html) |
| Server admin dashboard: storage, connected devices, background jobs | [`Server.dc.html`](design/mockups/Server.dc.html) |
| Mobile library: photo grid with backup progress | [`MobileLibrary.dc.html`](design/mockups/MobileLibrary.dc.html) |
| Mobile photo viewer: details sheet and actions | [`MobileViewer.dc.html`](design/mockups/MobileViewer.dc.html) |

The `.dc.html` files are source for a Claude design canvas, and `canvas.json` holds the canvas layout. They need the canvas runtime to render, so they won't display correctly opened straight from GitHub.

### Visual direction

- Dark "darkroom" interface so photos stand out
- Amber accent, like a darkroom safelight (`#E8A33D`)
- Fonts: Sora for headings, IBM Plex Sans for body text, IBM Plex Mono for technical details
