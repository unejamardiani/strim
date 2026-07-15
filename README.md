# strim

**🚀 Free M3U Playlist Editor & IPTV Cleaner - Filter, Clean & Organize IPTV Playlists in Seconds**

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](https://opensource.org/licenses/MIT)
[![Made with AI](https://img.shields.io/badge/Made%20with-AI-blue.svg)](https://github.com/anthropics/claude)

> **Clean your IPTV playlists from 50,000+ channels down to what you actually watch. Fast, privacy-focused, and 100% free.**

Strim is a lightweight, powerful M3U/M3U8 playlist editor that runs entirely in your browser. Filter unwanted channel groups, remove duplicates, and create shareable playlists for TiviMate, IPTV Smarters, Kodi, VLC, and more.

## ✨ Key Features

- **⚡ Lightning Fast** - Handles playlists with 50,000+ channels using Web Workers
- **🔒 Privacy-First** - All processing happens in your browser, no data uploads
- **🎯 Smart Filtering** - Toggle groups on/off, search by name, filter in real-time
- **🔗 Shareable URLs** - Generate permanent URLs for auto-updating playlists
- **📱 Works Everywhere** - Compatible with TiviMate, IPTV Smarters, Kodi, VLC, Perfect Player, GSE Smart IPTV
- **🆓 100% Free** - Open source, no premium tiers, no feature limits
- **📊 Real-time Stats** - See channel counts update as you filter
- **🌐 CORS Proxy Built-in** - Automatically fetches blocked playlist URLs

## 🎬 Quick Start

**Try it now:** [https://strim.plis.dev](https://strim.plis.dev) ← No installation required!

1. **Open Strim** - Visit [strim.plis.dev](https://strim.plis.dev) (or run locally - see below)
2. **Load your playlist** - Paste URL or raw M3U text
3. **Filter groups** - Toggle unwanted groups off (or Deselect All → enable favorites)
4. **Export** - Download, copy, or generate shareable URL

**That's it!** Your cleaned playlist is ready to use in your favorite IPTV app.

## 🆚 Why Strim?

| Feature | Strim | M3U4U | Playlist Buddy | Xtream Editor |
|---------|-------|-------|----------------|---------------|
| **Price** | ✅ Free | Freemium | Paid | Freemium |
| **Open Source** | ✅ Yes | ❌ No | ❌ No | ❌ No |
| **Privacy (Client-side)** | ✅ Yes | ❌ Server | ❌ Server | Mixed |
| **Large Playlists (50k+)** | ✅ Optimized | ⚠️ Slow | ⚠️ Limited | Good |
| **No Account Required** | ✅ Yes | ❌ Required | ❌ Required | Some features |
| **CORS Proxy Support** | ✅ Built-in | ❌ No | ⚠️ Limited | ❌ No |

## 📱 Works With Your Favorite Apps

- **TiviMate** (Android TV, Fire TV)
- **IPTV Smarters Pro** (iOS, Android, Fire TV)
- **Kodi** (All Platforms)
- **VLC Media Player** (Windows, Mac, Linux)
- **Perfect Player** (Android, Fire TV)
- **GSE Smart IPTV** (iOS, Apple TV)
- **OTT Navigator** (Android, Fire TV)
- Any M3U-compatible IPTV player

## Running locally

### Frontend only (limited functionality)

The frontend can run standalone for **basic playlist cleaning**:

```bash
python3 -m http.server 8000
```

Then open http://localhost:8000 in your browser. You can also open `index.html` directly from the filesystem.

**Works without backend:**
- ✅ Load sample playlist
- ✅ Fetch playlists from URLs
- ✅ Filter and organize groups
- ✅ Download or copy filtered playlists

**Requires backend (see below):**
- ❌ Save playlists (persistence)
- ❌ Shareable URLs
- ❌ Authentication
- ❌ Cross-device sync

### Full stack locally (with backend)

For complete functionality including persistence and sharing, run both frontend and backend:

```bash
# Terminal 1: Start the backend API (requires .NET 8)
cd api
dotnet run

# Terminal 2: Serve the frontend
python3 -m http.server 8000
```

The frontend will automatically connect to the API at `http://localhost:5000/api`. See [Backend persistence](#backend-persistence-net-8-minimal-api) section for database configuration options.

### Production Deployment

**Azure:** The project includes comprehensive Terraform infrastructure in `infra/terraform/` for deploying to Azure App Service with SQLite persistence. See [Deployment](#deployment-infrastructure) section below for details.

**Other Platforms:** Deploy the static files to any web host (Netlify, Vercel, GitHub Pages, etc.). No server-side dependencies required for the basic app.

## Backend persistence (.NET 8 minimal API)

If you want playlists to survive browser resets and be shared across devices, run the included
backend. It now supports either Postgres or SQLite so you can run everything in a single container.

1. Pick a provider (defaults to SQLite if no Postgres connection string is present):
   - `DB_PROVIDER=sqlite` (recommended for a cheap single-container deploy)
   - `DB_PROVIDER=postgres` (existing behavior; requires `POSTGRES_CONNECTION`)
2. Set a connection/file location:
   - SQLite: optionally set `SQLITE_PATH=/app/data/strim.db` (default inside the container) or a full `SQLITE_CONNECTION` string. Mount that directory to Azure Files/Blob storage for persistence.
   - Postgres: `POSTGRES_CONNECTION="Host=...;Port=...;Database=...;Username=...;Password=..."`
3. Start the API: `dotnet run --project api`
4. Serve the frontend (for example `python3 -m http.server 8000`). The API is CORS-open.
5. Point the UI at the API by either serving both from the same origin, or adding `?api=http://localhost:5000/api`
   to the page URL (this is remembered in `localStorage` as `strim.apiBase`).

The API auto-creates the database schema for either provider. If the API is unreachable, saving is
temporarily unavailable; guest sessions remain in-memory and reset on refresh.

### Authentication & playlist ownership

- Saving/updating/deleting playlists now requires signing in. Guest sessions can still fetch and clean playlists but nothing is persisted or shareable after a refresh.
- Share URLs keep working for IPTV apps without requiring a login; anyone with the code can download the filtered playlist.
- Username/password sign-up is built-in and uses cookies.
- Optional federated sign-in:
  - Google: set `Authentication:Google:ClientId` / `Authentication:Google:ClientSecret` or `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET`
  - Microsoft Entra ID: set `Authentication:Microsoft:TenantId` (default `common`), `Authentication:Microsoft:ClientId`, `Authentication:Microsoft:ClientSecret` or the `MICROSOFT_TENANT_ID` / `MICROSOFT_CLIENT_ID` / `MICROSOFT_CLIENT_SECRET` environment variables.

### One-command Docker (app + Postgres)

Build and run everything together with Docker Compose:

```bash
docker compose up --build
```

This starts:
- `app` on http://localhost:8080 serving the SPA and the `/api` endpoints
- `db` (Postgres 16) with database `strimdb` and user/password `strim`

Data is stored in the `pgdata` named volume. The app sets `POSTGRES_CONNECTION` automatically to talk
to the bundled database.

### Single-container Docker (SQLite)

For a cheapest, single-container deploy without Postgres, build and run with SQLite:

```bash
docker build -t strim .
docker run -p 8080:8080 \
  -e DB_PROVIDER=sqlite \
  -v strim-data:/app/data \
  strim
```

Mount `/app/data` (or your `SQLITE_PATH`) to Azure Files/Blob storage to persist the `.db` file
across restarts.

### Production container health and limits

The image has a Docker health check that calls `GET /health/live`; use that endpoint for a
platform liveness probe. The bundled Compose files intentionally do not impose CPU or memory
limits because they are development configurations and large playlist tests need their true
resource usage to be visible.

Set a hard memory limit and alert threshold in the production platform (such as Coolify) after
measuring the largest supported playlist at the configured concurrency. A Docker equivalent is
`--memory=<limit> --memory-reservation=<reservation>`. Do not reuse the development Compose files
as a production resource policy; an arbitrary low cap can turn a large playlist request into an
OOM restart.

### Large-playlist processing

Backend source downloads are spooled to an ephemeral private directory and parsed/generated
line-by-line. Strim hashes each source while downloading and uses upstream `ETag`/
`Last-Modified` validation when available; unchanged sources reuse their parsed metadata and
filtered output. A source is reused without an upstream request for the configured revalidation
interval (15 minutes by default), so validator-less providers do not cause a full download on
every share hit. The default is one expensive playlist job at a time, which deliberately trades
some queueing for predictable RAM and CPU use.

The default 2 GiB source and 8 GiB cache-disk budgets are safety limits, not a 25 MiB product
limit. Configure Coolify environment variables when a provider needs more space:

```text
PlaylistCache__MaxSourceBytes=2147483648
PlaylistCache__MaxDiskBytes=8589934592
PlaylistCache__RevalidationIntervalMinutes=15
PlaylistCache__MaxConcurrentJobs=1
```

Increase the source and disk budgets together only after confirming available ephemeral disk;
these settings do not cause the server to buffer the whole source in managed memory. For a source
ceiling of `S`, use at least roughly `2S` cache disk so the source and a generated output can be
present safely; the cache reserves scratch capacity before writing. The output/session cache is
process-local, so run one Strim replica unless you add shared cache storage or sticky routing.

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

## 🎯 Use Cases

### Remove Adult Content
Filter out inappropriate channels to create family-friendly playlists. Search for "XXX", "Adult", or "18+" groups and toggle them off.

### Language Filtering
Only watch English channels? Deselect all groups, then enable only "USA", "UK", "Canada", and "English" groups. Reduce 50,000 international channels to 5,000 relevant ones.

### Category-Based Playlists
Create specialized playlists:
- **Sports-only** - For game days (ESPN, Fox Sports, etc.)
- **News-only** - Stay informed (CNN, BBC, etc.)
- **Movies & Series** - Entertainment channels only
- **Kids** - Cartoons and educational content

### Speed Up IPTV Apps
Large playlists slow down TiviMate, IPTV Smarters, and other apps. Trim your playlist from 100,000 to 2,000 channels for instant loading.

## 🤝 Contributing

Contributions are welcome! Here's how you can help:

- **Report bugs** - Open an issue if you find a problem
- **Suggest features** - Share ideas for improvements
- **Submit pull requests** - Fix bugs or add features
- **Improve documentation** - Help others understand how to use Strim
- **Share with others** - Help more people discover Strim

## 📊 Project Status

- **Status:** ✅ Live & Active Development
- **Production URL:** [https://strim.plis.dev](https://strim.plis.dev)
- **License:** MIT (free to use, modify, distribute)
- **Platform:** Web (runs in any modern browser)
- **Backend:** Optional .NET 8 API for persistence and sharing
- **Infrastructure:** Azure App Service with Terraform automation

## 🌟 Star History

If you find Strim useful, please give it a star ⭐ on GitHub! It helps others discover the project.

## 🔗 Links

- **Website:** [https://strim.plis.dev](https://strim.plis.dev)
- **Landing Pages:**
  - [Features](https://strim.plis.dev/features.html) - Comprehensive feature showcase
  - [How to Use](https://strim.plis.dev/how-to-use.html) - Step-by-step tutorial
  - [Comparison](https://strim.plis.dev/comparison.html) - Compare with alternatives
  - [Blog](https://strim.plis.dev/blog/) - Tips and guides
- **Issues:** [GitHub Issues](https://github.com/unejamardiani/strim/issues)
- **Discussions:** [GitHub Discussions](https://github.com/unejamardiani/strim/discussions)

## 📚 Documentation

Comprehensive documentation is available in the `/docs` directory:

- **[PRODUCTION_BUILD.md](docs/PRODUCTION_BUILD.md)** - How to build for production (Tailwind CSS, security, performance)
- **[SECURITY.md](docs/SECURITY.md)** - Security considerations and best practices
- **[DATABASE_SCHEMA.md](docs/DATABASE_SCHEMA.md)** - Database schema and migrations
- **[SEO_PROGRESS.md](docs/SEO_PROGRESS.md)** - SEO strategy and implementation tracking

## 🚀 Deployment Infrastructure

The project includes production-ready Terraform configuration for Azure deployment:

**Location:** `infra/terraform/`

**Features:**
- Azure App Service with Linux containers
- SQLite database with Azure Files persistence
- Automated HTTPS and custom domain support
- Environment variable configuration
- Docker registry integration
- Cost-optimized for single-container deployment

**Quick Deploy:**
```bash
cd infra/terraform
terraform init
terraform apply
```

See `infra/terraform/README.md` for detailed deployment instructions and configuration options.

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Maintainer

Strim is maintained by [unejamardiani](https://github.com/unejamardiani). Decisions about releases and roadmap come from the maintainer; contributions via issues and pull requests are welcome.

## ❤️ Support

If Strim saves you time or improves your IPTV experience:
- ⭐ Star this repository
- 🐛 Report bugs and suggest features
- 🔗 Share with others who need playlist cleaning
- 📝 Write about your experience using Strim

## Attribution

This project was created entirely with AI assistance.

## ⚠️ Disclaimer

**This is a learning project.** Strim was developed as an educational exercise to explore and learn AI-powered coding tools such as Claude Code, GitHub Copilot, and similar AI development assistants. The primary purpose of this project is to gain hands-on experience with AI-assisted software development, understand the capabilities and limitations of these tools, and experiment with modern development workflows.

While the application is functional and can be used for M3U playlist editing, it should be considered a demonstration and learning project rather than a production-ready enterprise solution. Use at your own discretion.

---

**Keywords:** m3u editor, m3u8 editor, iptv playlist cleaner, m3u filter, iptv organizer, playlist manager, m3u cleaner online, free m3u editor, iptv tools, m3u playlist editor, tivimate playlist, iptv smarters playlist, m3u duplicate remover, iptv channel filter
