<p align="center"><img src="logo-wordmark.svg" alt="Strim" width="220"></p>

<p align="center"><strong>Your IPTV playlist, minus the noise.</strong><br>
Switch off the groups you never watch and give your player one clean link that stays up to date.</p>

<p align="center"><a href="https://strim.plis.dev">strim.plis.dev</a> · <a href="https://opensource.org/licenses/MIT">MIT licence</a></p>

---

## What it does

1. **Paste your provider's M3U/M3U8 link.** The server downloads and reads it, so lists with tens of
   thousands of entries load without freezing a phone.
2. **Switch off groups.** Every group is listed with its size and type (live, movies, series).
   Group-titles that mix live channels and series are split, e.g. “Germany” and “Germany · Series”.
   Search, filter by type, sort by size, switch all visible groups at once.
3. **Use the result.** Download the cleaned `.m3u` as a guest, or sign in and save to get a
   **player link**. Each time a player refreshes it, Strim reads the provider's current list and
   applies your choices. Links can be turned off or replaced, and show when a player last used them.

Strim never hosts streams; it only edits the playlist.

## Architecture

```
Browser ──▶ /           landing page, guides, blog          (static, site.css)
        ──▶ /app        editor: app.html + main.js          (static, app.css, no framework)
        ──▶ /api/*      .NET 8 Minimal API                   api/
IPTV player ──▶ /api/playlists/{id}/share/{code}            filtered M3U, anonymous
```

| Path | Purpose |
|------|---------|
| `api/Program.cs` | Composition only: services, middleware order, endpoint mapping |
| `api/Configuration/` | Database selection and schema upkeep, authentication, CORS/rate limits/CSRF, playlist services |
| `api/Endpoints/` | `Auth`, `Playlist` (saved playlists), `Processing` (analyze, generate, share), `Site` (health, `/app`, errors) |
| `api/Services/` | `PlaylistPipeline` (fetch → analyze → generate), disk cache, streaming parser, SSRF helpers, background refresh |
| `api.Tests/` | Unit tests and integration tests (`WebApplicationFactory` with SQLite and a stubbed upstream) |
| `*.html`, `*.css`, `main.js`, `fonts/`, `blog/` | Frontend, copied into `api/wwwroot` by the Dockerfile |
| `tools/Strim.DbMigrator` | One-off SQLite → PostgreSQL data migration |

Playlists are streamed to disk, never held in memory as a whole. Sources, analyses and generated
outputs are cached on disk and reused by content hash; only one processing job runs at a time.

## Run it

**Docker (SQLite, recommended):**

```bash
docker compose -f docker-compose.sqlite.yml up --build   # http://localhost:8080
```

**Docker with PostgreSQL:** `POSTGRES_PASSWORD=… docker compose up --build`

**From source** (.NET 8 SDK):

```bash
dotnet run --project api              # API on http://localhost:5000
python3 -m http.server 8000           # frontend: http://localhost:8000/app.html
dotnet test                           # all tests
```

A frontend on another localhost port talks to `http://localhost:5000/api` automatically. Override
with `?api=https://host/api` on the page URL (remembered in `localStorage`).

## Configuration

| Variable | Default | Meaning |
|----------|---------|---------|
| `DB_PROVIDER` | `sqlite` unless a Postgres connection is set | `sqlite` or `postgres` |
| `SQLITE_PATH` / `SQLITE_CONNECTION` | `data/strim.db` next to the app | SQLite location |
| `POSTGRES_CONNECTION` | | PostgreSQL connection string |
| `PUBLIC_SHARE_BASE_URL` | request host | Base URL used in player links |
| `ALLOWED_ORIGINS` | none | Comma-separated origins allowed for credentialed CORS |
| `TRUSTED_PROXIES` | trust all | IPs/CIDRs whose `X-Forwarded-*` headers are accepted |
| `GOOGLE_CLIENT_ID` / `_SECRET` | | Enables Google sign-in |
| `MICROSOFT_CLIENT_ID` / `_SECRET` / `MICROSOFT_TENANT_ID` | tenant `common` | Enables Microsoft sign-in |
| `GITHUB_CLIENT_ID` / `_SECRET` | | Enables GitHub sign-in |
| `PlaylistCache__*` | see `api/appsettings.json` | Cache directory, size limits, TTLs, timeouts, concurrency |

The schema is created with `EnsureCreated()` and extended in `api/Configuration/Database.cs`;
there are no EF migrations. See [docs/DATABASE_SCHEMA.md](docs/DATABASE_SCHEMA.md).

## Deployment

`.github/workflows/publish-image.yml` runs the tests, then builds the Docker image and pushes it to
Docker Hub tagged `main`, `sha-<commit>` and release tags. The image serves the frontend and API on
port 8080, with `/health/live` and `/health/ready` probes. Mount a volume at the SQLite path to keep
data.

The public instance runs on Coolify behind Cloudflare: the landing page and guides are public,
`/app` and `/api` are behind Cloudflare Access, and player links stay reachable for IPTV apps.

## Documentation

- [DESIGN.md](DESIGN.md): the Signal Paper design system for the app and the site
- [docs/SECURITY.md](docs/SECURITY.md): threat model and where each control lives
- [docs/DATABASE_SCHEMA.md](docs/DATABASE_SCHEMA.md): tables and schema upkeep
- [AGENTS.md](AGENTS.md): conventions for coding agents and contributors

## Licence

MIT. Fonts: Bricolage Grotesque and Figtree, SIL Open Font License (see `fonts/`).
