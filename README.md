# Strim

Strim is a toolkit for ingesting IPTV playlists, applying configurable cleaning rules, and exporting streamlined channel lists. This repository currently contains the foundational scaffolding for the backend API, frontend SPA, and infrastructure required to run the platform locally or in CI.

## Project Structure

```
.
├── backend/            # ASP.NET Core solution with API and tests
├── frontend/           # React + Vite single-page application
├── infrastructure/     # Docker Compose definitions and supporting scripts
├── .github/workflows/  # Continuous integration pipelines
├── spec.md             # Functional and technical specification
└── Strim.sln           # Solution file referencing backend projects
```

## Getting Started

### Prerequisites

- .NET 8 SDK (for local backend development)
- Node.js 20 (for the React frontend)
- Docker (optional, to use the compose environment)

### Backend

```bash
dotnet restore
dotnet run --project backend/src/Strim.Api/Strim.Api.csproj
```

The API hosts Swagger UI in development at `http://localhost:5142/swagger` and exposes a health endpoint at `http://localhost:5142/api/health`.

### Frontend

```bash
cd frontend
npm install
npm run dev
```

The development server runs on `http://localhost:5173` and proxies API requests to the backend server.

### Docker Compose

To run the full stack, including PostgreSQL, use:

```bash
cd infrastructure
docker compose up --build
```

This command starts the backend API, frontend static host, and a Postgres database with persistent storage.

### Continuous Integration

GitHub Actions (see `.github/workflows/ci.yml`) restores, builds, and tests the backend, while linting and building the frontend on each push or pull request.

## Next Steps

The remaining milestones from `spec.md`—such as playlist ingestion, rule management, and the cleaner engine—will be implemented incrementally on top of this foundation.
