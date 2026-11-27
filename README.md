<p align="center"><img src="logo-wordmark.svg" alt="Strim" width="220"></p>

<p align="center"><strong>Your IPTV playlist, minus the noise.</strong><br>
Switch off the groups you never watch, give your players one link that stays up to date, and browse what is left like a library.</p>

<p align="center"><a href="https://strim.plis.dev">strim.plis.dev</a> · <a href="https://opensource.org/licenses/MIT">MIT licence</a></p>

---

## What it does

**Clean**

- Paste your provider's M3U/M3U8 link. The server downloads and reads it, so lists with tens of
  thousands of entries load without freezing a phone.
- Every group is listed with its size and type (live, movies, series). Group-titles that mix live
  channels and series are split, e.g. “Germany” and “Germany · Series”.
- Everything search finds titles across all groups, with a language filter. All on / All off
  switches every visible group and can be undone; “Hide adult groups” switches adult groups off.
- The “Duplicates & variants” report lists repeated entries and quality variants; it removes nothing.
- Download the cleaned `.m3u` as a guest, no account needed.

**Player link**

- Sign in and save to get a player link. Each time a player refreshes it, Strim reads the
  provider's current list and applies your choices.
- When the provider fails, the link answers with the last list that worked.
- Turn a link off or replace it, and see when it was last fetched. “Refresh every 6 hours” keeps
  the list current without a player asking.
- The Watchlist group puts the films and series on your watchlist at the top of the player.

**Library**

- A read-only view of what your list holds (`/app#/p/<id>/library`): Home, Search, Groups and Marks.
  Nothing in it changes the playlist.
- Mark titles as seen, give a thumb up or down and keep a watchlist, per person.
- Only with a TMDB key: film and series cards, posters, “Trending this week” and
  “Because you liked …”. Without it the Library works without them.

**Sharing**

- Share a playlist with up to 10 people.
- An invite link works once and for 7 days.
- People you share with get read-only access and never see the provider link or the player link.

For Xtream Codes `get.php` links Strim also shows the account status, connections and expiry date.

Strim never hosts streams; it only edits the playlist.

## Architecture

```
Browser ──▶ /           landing page, guides                (static, site.css)
        ──▶ /app        editor: app.html + main.js          (static, app.css, no framework)
        ──▶ /app#/p/<id>/library   Library: library.js      (static, library.css)
        ──▶ /api/*      .NET 10 Minimal API                  api/
IPTV player ──▶ /api/playlists/{id}/share/{code}            filtered M3U, anonymous
api/ ──▶ TMDB (optional, only with a token)                 film and series facts, posters
```

| Path | Purpose |
|------|---------|
| `api/Program.cs` | Composition only: services, middleware order, endpoint mapping |
| `api/Configuration/` | Database selection and schema upkeep, authentication, CORS/rate limits/CSRF, playlist services |
| `api/Endpoints/` | `Auth`, `Playlist` (saved playlists), `Processing` (analyze, generate, player link), `Library…` (titles, posters, suggestions), `Mark`, `Share`, `Site` (health, `/app`, errors) |
| `api/Services/` | `PlaylistPipeline` (fetch → analyze → generate), disk cache, streaming parser, SSRF helpers, background refresh; search index, marks, TMDB client with an in-memory cache, Watchlist group, duplicate and variant finders, provider account facts |
| `api/Models/` | Playlists, shares, title marks and the API contracts |
| `api.Tests/` | Unit tests and integration tests (`WebApplicationFactory` with SQLite and a stubbed upstream) |
| `*.html`, `*.css`, `main.js`, `library.js`, `library.css`, `theme-boot.js`, `fonts/` | Frontend, copied into `api/wwwroot` by the Dockerfile |
| `deploy/sanitise/` | SQL that strips personal data from the staging copy of production |
| `tools/Strim.DbMigrator` | One-off SQLite → PostgreSQL data migration |

Playlists are streamed to disk, never held in memory as a whole. Sources, analyses and generated
outputs are cached on disk and reused by content hash; only one processing job runs at a time.

## Run it

**Docker (SQLite, recommended):**

```bash
docker compose -f docker-compose.sqlite.yml up --build   # http://localhost:8080
```

**Docker with PostgreSQL:** `POSTGRES_PASSWORD=… docker compose up --build`

**From source** (.NET 10 SDK):

```bash
dotnet run --project api              # API on http://localhost:5000
python3 -m http.server 8000           # frontend: http://localhost:8000/app.html
dotnet test                           # all tests
```

A frontend on another localhost port talks to `http://localhost:5000/api` automatically. For local
development only, override with `?api=http://localhost:5000/api` on the page URL (remembered in
`localStorage`); on any other host name the app ignores the override and uses its own `/api`.

## Configuration

| Variable | Default | Meaning |
|----------|---------|---------|
| `DB_PROVIDER` | `sqlite` unless a Postgres connection is set | `sqlite` or `postgres` |
| `SQLITE_PATH` / `SQLITE_CONNECTION` | `data/strim.db` next to the app | SQLite location |
| `POSTGRES_CONNECTION` | | PostgreSQL connection string |
| `PUBLIC_SHARE_BASE_URL` | request host | Base URL used in player links |
| `ALLOWED_ORIGINS` | none | Comma-separated origins allowed for credentialed CORS |
| `TRUSTED_PROXIES` | none (forwarded headers ignored) | IPs/CIDRs whose `X-Forwarded-*`, `X-ARR-SSL` and `CF-Connecting-IP` headers are accepted |
| `GOOGLE_CLIENT_ID` / `_SECRET` | | Enables Google sign-in |
| `MICROSOFT_CLIENT_ID` / `_SECRET` / `MICROSOFT_TENANT_ID` | tenant `common` | Enables Microsoft sign-in |
| `GITHUB_CLIENT_ID` / `_SECRET` | | Enables GitHub sign-in |
| `CF_ACCESS_TEAM_DOMAIN` | none | Cloudflare Access team domain (for example `team.cloudflareaccess.com`); with `CF_ACCESS_AUD` enables sign-in through Access |
| `CF_ACCESS_AUD` | none | AUD tag of the Access application that protects Strim |
| `TMDB_READ_ACCESS_TOKEN` | none (optional) | TMDB API read access token; enables film and series cards, posters and suggestion rails in the Library. Only a cleaned title and year are sent to TMDB. Without it the Library works without them |
| `DATA_PROTECTION_KEYS_PATH` | ASP.NET Core default (`~/.aspnet/DataProtection-Keys`); image: `/home/app/.aspnet/DataProtection-Keys` | Directory of the keys that sign session and antiforgery cookies |
| `PlaylistCache__*` | see `api/appsettings.json` | Cache directory, size limits, TTLs, timeouts, concurrency |

The schema is created with `EnsureCreated()` and extended in `api/Configuration/Database.cs`;
there are no EF migrations. See [docs/DATABASE_SCHEMA.md](docs/DATABASE_SCHEMA.md).

## Deployment

`.github/workflows/publish-image.yml` runs the tests, then builds the Docker image and pushes it to
Docker Hub tagged `main`, `sha-<commit>` and release tags. The image serves the frontend and API on
port 8080, with `/health/live` and `/health/ready` probes (`curl` is in the image).

The container runs as the non-root user `app`, UID and GID 1654. The files under `/app` belong to
root and are read-only for it. The app writes only to:

| Path | Content | Keep in a volume |
|------|---------|------------------|
| `/app/data` | SQLite database (`SQLITE_PATH`) | yes |
| `/home/app/.aspnet/DataProtection-Keys` | cookie keys (`DATA_PROTECTION_KEYS_PATH`); without them users sign in again after a restart | yes |
| `/tmp/strim-playlists`, `/tmp` | playlist cache and temporary files | no |

A volume created by an earlier image belongs to root. Give it to the app user before you start the
new image, for example `chown -R 1654:1654 <volume path>`. Keys that an earlier image kept in
`/root/.aspnet/DataProtection-Keys` stay valid: mount the same volume at the new keys path.

The public instance at `strim.plis.dev` runs on Coolify behind Cloudflare. It asks for the
Cloudflare Access sign-in on every page; only player links are reachable without it, so IPTV apps
can fetch them.

## Documentation

- [DESIGN.md](DESIGN.md): the Signal Paper design system for the app and the site
- [docs/SECURITY.md](docs/SECURITY.md): threat model and where each control lives
- [docs/DATABASE_SCHEMA.md](docs/DATABASE_SCHEMA.md): tables and schema upkeep
- [AGENTS.md](AGENTS.md): conventions for coding agents and contributors

## Licence

MIT. Fonts: Bricolage Grotesque and Figtree, SIL Open Font License (see `fonts/`).
