# Strim — Agent instructions

## Project
M3U/IP TV playlist editor. Vanilla JS SPA (no framework) + .NET 8 Minimal API backend with EF Core. Deployed on Azure App Service via Docker + Terraform.

## Directory structure
- `/` — static frontend (index.html, main.js, style.css, filter-worker.js)
- `api/` — .NET 8 backend (single-file Program.cs ~1680 lines, no controllers)
- `api.Tests/` — xUnit tests
- `infra/terraform/` — Azure deployment
- `docs/` — production build guide, schema, security notes

## Development commands
```bash
dotnet run --project api              # backend on http://localhost:5000
python3 -m http.server 8000           # frontend only
node cors-proxy.js                    # CORS proxy on :8080
docker compose up --build             # full stack with Postgres
docker compose -f docker-compose.sqlite.yml up --build  # full stack with SQLite
dotnet test                           # run all tests
dotnet test --filter "FullyQualifiedName~SecurityHelpersTests"  # single test class
```

## Framework & toolchain quirks
- **No EF migrations**: schema is managed via `EnsureCreated()` + manual ALTER TABLE patching in `Program.cs`. Never add EF migrations.
- **DB auto-select**: SQLite by default when no `POSTGRES_CONNECTION` is set. Set `DB_PROVIDER=postgres` to force Postgres.
- **Frontend→API routing**: add `?api=http://localhost:5000/api` to the page URL, or set `localStorage.strim.apiBase`.
- **Frontend uses Tailwind CDN** in dev (`https://cdn.tailwindcss.com`). For production, build via `npx tailwindcss` (see `docs/PRODUCTION_BUILD.md`). The CDN script and `'unsafe-inline'` in CSP must be removed for deployment.
- **CORS**: open in dev (`AllowAnyOrigin`), requires explicit `ALLOWED_ORIGINS` env var in production with credential support.
- **Rate limiting**: auth(20/min), fetch(30/min), general(100/min), sensitive(10/min).
- **No npm/Node** dependencies for the frontend (vanilla JS). No `package.json` at root.

## Testing
- xUnit in `api.Tests/`. Single project referencing `api/`.
- Test SSRF helper coverage in `SecurityHelpersTests.cs`.
- No integration tests (no test fixture for DB).

## Key architecture notes
- All API endpoints are defined inline in `Program.cs` (Minimal API pattern, no controllers).
- Auth: ASP.NET Core Identity with cookie auth. Google/Microsoft OAuth optional. CSRF via antiforgery tokens.
- SSRF protection in `api/Services/SecurityHelpers.cs` (blocks private IPs, validates redirects).
- Playlist processing uses a background Web Worker on the frontend (`filter-worker.js`).
- Cached for 15 min in `IMemoryCache` per analyze/generate request (500MB limit).
- Share URLs are unauthenticated: `GET /api/playlists/{id}/share/{code}` returns filtered M3U file.
- `HealthChecks` at `/health`, `/health/live`, `/health/ready`.

## Production deploy
- Docker image built via `Dockerfile` → copies frontend files into `api/wwwroot` → published as single container.
- CI: `.github/workflows/publish-image.yml` builds and pushes to Docker Hub.
- CD: `.github/workflows/deploy-azure.yml` runs Terraform for dev/prod environments with SQLite + Azure Files.
