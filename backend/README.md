# Strim Backend

This folder contains the ASP.NET Core backend for Strim. The project currently exposes a simple health endpoint and testing scaffold to validate the hosting pipeline.

## Structure

- `src/Strim.Api` – Minimal API host project.
- `tests/Strim.Tests` – xUnit project configured with ASP.NET Core integration testing helpers.
- `Directory.Build.props` – Shared configuration for all backend projects (target framework, nullable, implicit usings).

## Development

The backend targets .NET 8 with implicit usings and nullable reference types enabled. Restore dependencies and run the application from the repository root:

```bash
 dotnet restore
 dotnet run --project backend/src/Strim.Api/Strim.Api.csproj
```

Access the development Swagger UI at `http://localhost:5142/swagger` once the server is running.

## Testing

Run the unit test suite using:

```bash
 dotnet test
```

> **Note:** The execution environment in this repository template might not provide the .NET SDK by default. Install the SDK locally or use the provided Docker workflow to build and test the application.
