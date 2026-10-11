# Strim — Agent instructions

## Project
IPTV (M3U) playlist editor. Public landing page at `/`, the editor at `/app`, a .NET 8 Minimal API
under `/api` with EF Core (SQLite or PostgreSQL). Ships as one Docker image; the public instance
runs on Coolify behind Cloudflare.

## Directory structure
- `/` — static frontend, no build step: `index.html` (landing), `app.html` + `main.js` + `app.css`
  (editor), `theme.css` (shared), `site.css` (landing, guides, blog, error pages), `blog/`, `fonts/`
- `api/Program.cs` — composition only (services, middleware order, endpoint mapping)
- `api/Configuration/` — `Database`, `Authentication`, `Security` (forwarded headers, CORS, rate
  limits, antiforgery), `PlaylistServices`
- `api/Endpoints/` — `AuthEndpoints`, `PlaylistEndpoints`, `ProcessingEndpoints`, `SiteEndpoints`,
  `EndpointSupport` (limits, `RequireCsrfToken`, error mapping)
- `api/Services/` — `PlaylistPipeline`, `PlaylistFileCache`, `PlaylistProcessor`,
  `PlaylistSourceFetcher`, `SecurityHelpers`, background services
- `api.Tests/` — xUnit; `Integration/` hosts the real app via `StrimAppFactory`
- `tools/Strim.DbMigrator` — one-off SQLite → PostgreSQL migration (not in the solution or image)
- `docs/` — security and database schema

## Development commands
```bash
dotnet run --project api              # API on http://localhost:5000
python3 -m http.server 8000           # frontend; editor at http://localhost:8000/app.html
docker compose -f docker-compose.sqlite.yml up --build  # full stack, SQLite, :8080
docker compose up --build             # full stack with Postgres (needs POSTGRES_PASSWORD)
dotnet test                           # all tests
dotnet test --filter "FullyQualifiedName~Integration"   # integration tests only
```

## Rules and quirks
- **No EF migrations.** Schema = `EnsureCreated()` + idempotent column patches in
  `api/Configuration/Database.cs`. Add new columns there for both SQLite and PostgreSQL.
- **CSRF:** every state-changing endpoint except logout uses `.RequireCsrfToken()`. The frontend
  sends `X-CSRF-TOKEN` from `/api/auth/csrf-token` and retries once on a stale token.
- **Playlist work goes through `PlaylistPipeline`** and the `PlaylistJobGate`; never read a whole
  playlist into memory. Map failures with `EndpointHelpers.PlaylistFailure`.
- **Routing:** `app.UseRouting()` is called after the static files on purpose (see `Program.cs`).
  Unknown `/api` routes return a JSON 404; other unknown paths the HTML 404 page.
- **Frontend:** vanilla JS, no npm, no CDN. CSPs have no `unsafe-inline`: no inline `<script>`,
  `<style>` or `style=""`. Reference CSS/JS by plain paths; the Dockerfile adds `?v=<hash>`.
  New top-level files must be added to the Dockerfile copy step.
- **Error pages** (`404/500/50x.html`) use absolute asset paths.
- **Design:** follow DESIGN.md (Signal Paper). Marketing copy must describe what the app really
  does.
- **SSRF:** all upstream fetches use the `fetcher` HttpClient (connect-time address checks) and
  `PlaylistSourceFetcher` (redirect validation). Tests stub the handler in `StrimAppFactory`.
- **Secrets:** never print or commit provider URLs or share links from real data.

## Deploy
1. Merge to `main`: `.github/workflows/publish-image.yml` tests, then pushes the image tagged
   `sha-<7 chars>`.
2. Production is pinned in `plis-labs/coolify-apps` (`apps/strim/production`). A PR changing the
   `sha-*` pin, once merged, triggers the Coolify webhook.
3. Cloudflare Access (in `plis-labs/coolify-infra`) protects `/app` and `/api`, except the share
   route used by IPTV players.
