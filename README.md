# strim

A lightweight single-page app for cleaning M3U / M3U8 playlists. Point strim at a playlist URL
or paste the raw text, toggle the channel groups you want to keep, and download a fresh, filtered
playlist.

## Running locally

No build tools are required – it is a static site:

```bash
python3 -m http.server 8000
```

Then open http://localhost:8000 in your browser. You can also open `index.html` directly from the
filesystem.

### Hosting cheaply on Azure

Deploy the contents of this directory to Azure Static Web Apps or an App Service configured for
static files. The app has no server-side dependencies.

## Playlist fetching and CORS

Some playlist hosts do not send CORS headers, which blocks direct browser fetches. strim will
automatically retry through lightweight public CORS proxies if a direct request fails. If a host
also blocks those proxies, paste the playlist text into the app instead.

### Option 1: Local CORS Proxy (Recommended for development)

Run the included CORS proxy server in a separate terminal:

```bash
node cors-proxy.js
```

This starts a local proxy on `http://localhost:8080` that allows fetching from any URL without CORS restrictions. The app will automatically try this proxy first when running locally.

### Option 2: Paste Content Directly

1. Download the M3U file manually using `curl`, `wget`, or your browser
2. Copy the content
3. Paste it into the "Paste raw M3U text" textarea in the app

### Option 3: Public CORS Proxies

The app will try each of these in order until one succeeds:

- Local proxy (`http://localhost:8080` - only if cors-proxy.js is running)
- `https://api.allorigins.win/get?url=…` (JSON wrapper)
- `https://api.allorigins.win/raw?url=…`
- `https://cors.io/?…`
- `https://thingproxy.freeboard.io/fetch/…`
- `https://corsproxy.io/?…`

If none of them work the UI now shows a per-proxy error summary so you can see what failed.

### HTTPS page loading HTTP playlists

When the app is served over HTTPS (e.g., GitHub Codespaces), browsers will block plain-HTTP playlist
URLs as mixed content. strim detects this and skips the blocked direct request, retrying through the
proxies listed above. If your provider blocks the proxies too, download the playlist and paste the
text into the app.

Note: toggling group visibility updates the counts and UI immediately, but the filtered playlist is
only regenerated when you click `Refresh`, `Copy`, `Download`, or `Generate shareable URL` — this
keeps toggling responsive for very large playlists.

For very large playlists (hundreds of thousands of channels) the app now generates the filtered
playlist in a background Web Worker to avoid blocking the UI. When you click `Refresh`, `Copy`,
`Download` or `Generate shareable URL`, a background job runs and a progress indicator appears in
the status pill. Buttons are disabled while generation runs to prevent accidental double-clicks.

Additionally, the Groups list uses a virtualized renderer so only visible group rows are
rendered to the DOM. This keeps toggling individual groups extremely fast even when there are
hundreds of thousands of groups.