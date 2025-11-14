# Strim Backend

The Strim backend is an ASP.NET Core minimal API that ingests IPTV playlists, persists them to PostgreSQL via Entity Framework Core, and exposes endpoints for playlist discovery and health checks.

## Structure

- `src/Strim.Api` – Minimal API host, domain model, EF Core context, migrations, and playlist ingestion services.
- `tests/Strim.Tests` – xUnit suite with integration tests backed by an in-memory database and parser unit tests.
- `Directory.Build.props` – Shared configuration for backend projects (target framework, nullable, implicit usings).

## Development

Restore dependencies and run the API from the repository root:

```
dotnet restore
dotnet run --project backend/src/Strim.Api/Strim.Api.csproj
```

Swagger UI is available at `http://localhost:5142/swagger` during development.

### Configuration

The API expects a PostgreSQL connection string under `ConnectionStrings:Database`. Defaults are provided in `appsettings.json`, and Docker Compose injects container-friendly values automatically. Migrations under `Data/Migrations` are applied at startup; add new migrations with `dotnet ef migrations add` as the model evolves.

### Key Endpoints

- `GET /api/health` – Service heartbeat.
- `POST /api/playlists/parse` – Accepts a JSON payload with either `url` or `filePath` (plus optional `name`), downloads/parses the playlist, saves it, and returns a preview.
- `GET /api/playlists` – Lists stored playlists, including channel counts and timestamps.

Example request:

```json
{
  "url": "https://provider.example/playlist.m3u8",
  "name": "Provider"
}
```

## Testing

Run the test suite with:

```
dotnet test
```

Integration tests use a custom `WebApplicationFactory` that swaps the PostgreSQL provider for EF Core's in-memory provider, so no external dependencies are required.

> **Note:** The execution environment in this repository template might not provide the .NET SDK by default. Install the SDK locally or use Docker-based workflows to build and test the application.
