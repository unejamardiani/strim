# Strim

Strim is a toolkit for ingesting IPTV playlists, applying configurable cleaning rules, and exporting streamlined channel lists. The repository now includes playlist ingestion, persistence, and preview capabilities alongside the original project scaffolding.

## Project Structure

```
.
├── backend/            # ASP.NET Core API, EF Core data layer, and automated tests
├── frontend/           # React + Vite single-page application
├── infrastructure/     # Docker Compose definitions and supporting scripts
├── .github/workflows/  # Continuous integration pipelines
├── spec.md             # Functional and technical specification
└── Strim.sln           # Solution file referencing backend projects
```

## Backend

The API exposes endpoints for health checks and playlist management:

- `GET /api/health` – Service heartbeat.
- `POST /api/playlists/parse` – Ingest a playlist from a URL or filesystem path, persist it, and return a preview of the first 25 channels.
- `GET /api/playlists` – List stored playlists with channel counts and metadata.

### Local Development

```
dotnet restore
dotnet run --project backend/src/Strim.Api/Strim.Api.csproj
```

The API hosts Swagger UI in development at `http://localhost:5142/swagger`.

### Database & Migrations

Entity Framework Core targets PostgreSQL by default. The sample connection string is configured in `appsettings.json` and can be overridden through the standard `ConnectionStrings__Database` environment variable. Migrations are applied automatically on startup; generate new migrations locally with:

```
dotnet ef migrations add <Name> --project backend/src/Strim.Api/Strim.Api.csproj --output-dir Data/Migrations
```

## Frontend

```
cd frontend
npm install
npm run dev
```

The development server runs on `http://localhost:5173` and proxies API requests. The UI offers:

- Backend health status and manual refresh controls.
- A playlist ingestion form supporting URL or file path sources.
- A preview of the most recent ingest, plus a table of all stored playlists.

Set `VITE_API_URL` in `.env.local` when developing against a remote backend.

## Docker Compose

To run the full stack, including PostgreSQL, use:

```
cd infrastructure
docker compose up --build
```

This command starts the backend API, applies database migrations, serves the frontend statically, and provisions a Postgres database with persistent storage.

## Continuous Integration

GitHub Actions (see `.github/workflows/ci.yml`) restore, build, and test the backend, while linting and building the frontend on each push or pull request.

## Next Steps

The remaining milestones from `spec.md`—such as rule management, the cleaner engine, scheduling, and advanced playlist transformations—will build on this ingestion layer.
