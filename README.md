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
automatically retry through lightweight public CORS proxies (isomorphic-git, AllOrigins, and
corsproxy.io) if a direct request fails. If a host also blocks those proxies, paste the playlist
text into the app instead.

### HTTPS page loading HTTP playlists

When the app is served over HTTPS (e.g., GitHub Codespaces), browsers will block plain-HTTP playlist
URLs as mixed content. strim detects this and skips the blocked direct request, retrying through the
proxies listed above. If your provider blocks the proxies too, download the playlist and paste the
text into the app.