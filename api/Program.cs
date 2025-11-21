using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Npgsql;
using Microsoft.Data.Sqlite;
using System.Data.Common;

var builder = WebApplication.CreateBuilder(args);

var configuredProvider = (builder.Configuration["DB_PROVIDER"] ?? builder.Configuration["DATABASE_PROVIDER"])?.ToLowerInvariant();
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres") ??
  builder.Configuration["POSTGRES_CONNECTION"];
var sqliteConnectionString = builder.Configuration.GetConnectionString("Sqlite") ??
  builder.Configuration["SQLITE_CONNECTION"];
var sqlitePathOverride = builder.Configuration["SQLITE_PATH"];

// Default to SQLite when no Postgres connection string is provided unless explicitly overridden.
var useSqlite = string.Equals(configuredProvider, "sqlite", StringComparison.OrdinalIgnoreCase) ||
  (string.IsNullOrWhiteSpace(configuredProvider) && string.IsNullOrWhiteSpace(postgresConnectionString));

if (useSqlite)
{
  var sqliteBuilder = new SqliteConnectionStringBuilder(
    string.IsNullOrWhiteSpace(sqliteConnectionString)
      ? $"Data Source={Path.Combine(AppContext.BaseDirectory, "data", "strim.db")}"
      : sqliteConnectionString);

  if (!string.IsNullOrWhiteSpace(sqlitePathOverride))
  {
    sqliteBuilder.DataSource = sqlitePathOverride;
  }

  if (!Path.IsPathRooted(sqliteBuilder.DataSource))
  {
    sqliteBuilder.DataSource = Path.GetFullPath(sqliteBuilder.DataSource, AppContext.BaseDirectory);
  }

  var sqliteDirectory = Path.GetDirectoryName(sqliteBuilder.DataSource);
  if (!string.IsNullOrWhiteSpace(sqliteDirectory))
  {
    Directory.CreateDirectory(sqliteDirectory);
  }

  builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(sqliteBuilder.ConnectionString));
}
else
{
  var connectionString = postgresConnectionString ??
    "Host=localhost;Port=5432;Database=strim;Username=postgres;Password=postgres";

  // Configure Npgsql data source with dynamic JSON enabled so we can store List<string> as jsonb.
  var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
  dataSourceBuilder.EnableDynamicJson();
  var dataSource = dataSourceBuilder.Build();

  builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(dataSource));
}
builder.Services.AddCors(options =>
{
  options.AddPolicy("default", policy =>
    policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
});
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("fetcher", client =>
{
  client.Timeout = TimeSpan.FromSeconds(15);
  client.DefaultRequestHeaders.UserAgent.ParseAdd("strim-fetch/1.0 (+https://github.com/)");
  client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
  client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-mpegurl"));
  client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
});

var app = builder.Build();
app.UseCors("default");

app.UseDefaultFiles();
app.UseStaticFiles();

// Ensure database exists and matches the model. For now we use EnsureCreated for simplicity.
using (var scope = app.Services.CreateScope())
{
  var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
  EnsureSchema(db);
}

void EnsureSchema(AppDbContext db)
{
  db.Database.EnsureCreated();

  if (db.Database.IsNpgsql())
  {
    TryPatchPostgresSchema(db);
    TryBackfillPostgresShareCodes(db);
  }
  else if (db.Database.IsSqlite())
  {
    TryAddSqliteColumn(db, "totalchannels", "INTEGER NOT NULL DEFAULT 0");
    TryAddSqliteColumn(db, "groupcount", "INTEGER NOT NULL DEFAULT 0");
    TryAddSqliteColumn(db, "expirationutc", "TEXT NULL");
    TryAddSqliteColumn(db, "sharecode", "TEXT NULL");
    TryBackfillSqliteShareCodes(db);
  }
}

void TryPatchPostgresSchema(AppDbContext db)
{
  try
  {
    db.Database.ExecuteSqlRaw(@"
      DO $$
      BEGIN
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'totalchannels') THEN
          ALTER TABLE playlists ADD COLUMN totalchannels integer NOT NULL DEFAULT 0;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'groupcount') THEN
          ALTER TABLE playlists ADD COLUMN groupcount integer NOT NULL DEFAULT 0;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'expirationutc') THEN
          ALTER TABLE playlists ADD COLUMN expirationutc timestamptz NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'playlists' AND column_name = 'sharecode') THEN
          ALTER TABLE playlists ADD COLUMN sharecode varchar(64) NULL;
        END IF;
      END $$;
    ");
  }
  catch
  {
    // Non-fatal; the app can continue even if this migration helper fails.
  }
}

void TryBackfillPostgresShareCodes(AppDbContext db)
{
  try
  {
    db.Database.ExecuteSqlRaw(@"
      UPDATE playlists
      SET sharecode = md5(random()::text || clock_timestamp()::text)
      WHERE sharecode IS NULL OR sharecode = '';
    ");
  }
  catch
  {
    // Non-fatal; best-effort.
  }
}

void TryAddSqliteColumn(AppDbContext db, string columnName, string definition)
{
  try
  {
    var connection = db.Database.GetDbConnection();
    db.Database.OpenConnection();

    var columnExists = false;
    using (var cmd = connection.CreateCommand())
    {
      cmd.CommandText = "PRAGMA table_info('playlists');";
      using var reader = cmd.ExecuteReader();
      while (reader.Read())
      {
        if (reader.FieldCount > 1 && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
        {
          columnExists = true;
          break;
        }
      }
    }

    if (!columnExists)
    {
      using var alter = connection.CreateCommand();
      alter.CommandText = $"ALTER TABLE playlists ADD COLUMN {columnName} {definition};";
      alter.ExecuteNonQuery();
    }
  }
  catch
  {
    // Non-fatal; best-effort; SQLite schemas are patched opportunistically.
  }
  finally
  {
    db.Database.CloseConnection();
  }
}

void TryBackfillSqliteShareCodes(AppDbContext db)
{
  try
  {
    db.Database.ExecuteSqlRaw(@"
      UPDATE playlists
      SET sharecode = lower(hex(randomblob(16)))
      WHERE sharecode IS NULL OR sharecode = '';
    ");
  }
  catch
  {
    // Non-fatal; best-effort.
  }
}

async Task<string> FetchPlaylistText(string url, IHttpClientFactory httpClientFactory)
{
  if (string.IsNullOrWhiteSpace(url))
  {
    throw new ArgumentException("url is required", nameof(url));
  }

  if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
  {
    throw new InvalidOperationException("Only http/https URLs are allowed");
  }

  var client = httpClientFactory.CreateClient("fetcher");
  using var res = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
  if (!res.IsSuccessStatusCode)
  {
    throw new HttpRequestException($"Upstream returned {(int)res.StatusCode}", null, res.StatusCode);
  }

  var bytes = await res.Content.ReadAsByteArrayAsync();
  return Encoding.UTF8.GetString(bytes);
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/playlists", async (AppDbContext db) =>
{
  var items = await db.Playlists.OrderByDescending(p => p.UpdatedAt).ToListAsync();
  return Results.Ok(items);
});

app.MapGet("/api/playlists/{id:guid}", async (Guid id, AppDbContext db) =>
{
  var item = await db.Playlists.FindAsync(id);
  return item is null ? Results.NotFound() : Results.Ok(item);
});

app.MapPost("/api/playlists", async (PlaylistRequest input, AppDbContext db) =>
{
  if (string.IsNullOrWhiteSpace(input.Name))
  {
    return Results.BadRequest(new { error = "Name is required." });
  }

  try
  {
    var entity = new Playlist
    {
      Name = input.Name.Trim(),
      SourceUrl = input.SourceUrl,
      SourceName = input.SourceName,
      DisabledGroups = input.DisabledGroups ?? new List<string>(),
      TotalChannels = input.TotalChannels ?? 0,
      GroupCount = input.GroupCount ?? 0,
      ExpirationUtc = input.ExpirationUtc,
      ShareCode = string.IsNullOrWhiteSpace(input.ShareCode) ? Guid.NewGuid().ToString("N") : input.ShareCode.Trim(),
    };

    db.Playlists.Add(entity);
    await db.SaveChangesAsync();
    return Results.Created($"/api/playlists/{entity.Id}", entity);
  }
  catch (Exception ex)
  {
    return Results.Problem($"Failed to save playlist: {ex.Message}", statusCode: 500);
  }
});

app.MapPut("/api/playlists/{id:guid}", async (Guid id, PlaylistRequest input, AppDbContext db) =>
{
  var entity = await db.Playlists.FindAsync(id);
  if (entity is null) return Results.NotFound();

  if (string.IsNullOrWhiteSpace(input.Name))
  {
    return Results.BadRequest(new { error = "Name is required." });
  }

  try
  {
    entity.Name = input.Name.Trim();
    entity.SourceUrl = input.SourceUrl;
    entity.SourceName = input.SourceName;
    entity.DisabledGroups = input.DisabledGroups ?? new List<string>();
    entity.TotalChannels = input.TotalChannels ?? entity.TotalChannels;
    entity.GroupCount = input.GroupCount ?? entity.GroupCount;
    entity.ExpirationUtc = input.ExpirationUtc ?? entity.ExpirationUtc;
    if (!string.IsNullOrWhiteSpace(input.ShareCode))
    {
      entity.ShareCode = input.ShareCode.Trim();
    }
    if (string.IsNullOrWhiteSpace(entity.ShareCode))
    {
      entity.ShareCode = Guid.NewGuid().ToString("N");
    }

    await db.SaveChangesAsync();
    return Results.Ok(entity);
  }
  catch (Exception ex)
  {
    return Results.Problem($"Failed to update playlist: {ex.Message}", statusCode: 500);
  }
});

app.MapDelete("/api/playlists/{id:guid}", async (Guid id, AppDbContext db) =>
{
  var entity = await db.Playlists.FindAsync(id);
  if (entity is null) return Results.NotFound();
  db.Playlists.Remove(entity);
  await db.SaveChangesAsync();
  return Results.NoContent();
});

app.MapPost("/api/playlist/analyze", async (AnalyzePlaylistRequest input, IHttpClientFactory httpClientFactory, IMemoryCache cache) =>
{
  if (string.IsNullOrWhiteSpace(input.SourceUrl) && string.IsNullOrWhiteSpace(input.RawText))
  {
    return Results.BadRequest(new { error = "Provide a sourceUrl or rawText." });
  }

  try
  {
    var playlistText = string.IsNullOrWhiteSpace(input.RawText)
      ? await FetchPlaylistText(input.SourceUrl!, httpClientFactory)
      : input.RawText!;

    var (groupsMap, total) = PlaylistProcessor.CountGroups(playlistText);
    var cacheKey = $"pl-{Guid.NewGuid():N}";
    cache.Set(cacheKey, playlistText, TimeSpan.FromMinutes(15));

    DateTimeOffset? expiration = null;
    if (!string.IsNullOrWhiteSpace(input.SourceUrl) && Uri.TryCreate(input.SourceUrl, UriKind.Absolute, out var parsedUri))
    {
      expiration = PlaylistProcessor.TryExtractExpiration(parsedUri);
    }

    var friendlyName = string.IsNullOrWhiteSpace(input.SourceName)
      ? PlaylistProcessor.DeriveNameFromUrl(input.SourceUrl)
      : input.SourceName!.Trim();

    var response = new AnalyzePlaylistResponse(
      cacheKey,
      input.SourceUrl,
      friendlyName,
      total,
      groupsMap.Count,
      expiration,
      PlaylistProcessor.ToGroupResults(groupsMap));

    return Results.Ok(response);
  }
  catch (TaskCanceledException)
  {
    return Results.Problem("Fetch timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
  }
  catch (InvalidOperationException ex)
  {
    return Results.BadRequest(new { error = ex.Message });
  }
  catch (HttpRequestException ex)
  {
    var status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int)HttpStatusCode.BadGateway;
    return Results.Problem(ex.Message, statusCode: status);
  }
  catch (Exception ex)
  {
    return Results.Problem(ex.Message, statusCode: (int)HttpStatusCode.BadGateway);
  }
});

app.MapPost("/api/playlist/generate", async (GeneratePlaylistRequest input, IHttpClientFactory httpClientFactory, IMemoryCache cache) =>
{
  if (string.IsNullOrWhiteSpace(input.CacheKey) && string.IsNullOrWhiteSpace(input.SourceUrl))
  {
    return Results.BadRequest(new { error = "Provide a cacheKey or sourceUrl." });
  }

  string? playlistText = null;
  if (!string.IsNullOrWhiteSpace(input.CacheKey))
  {
    cache.TryGetValue(input.CacheKey!, out playlistText);
  }

  try
  {
    if (playlistText is null && !string.IsNullOrWhiteSpace(input.SourceUrl))
    {
      playlistText = await FetchPlaylistText(input.SourceUrl!, httpClientFactory);
      if (!string.IsNullOrWhiteSpace(input.CacheKey))
      {
        cache.Set(input.CacheKey!, playlistText, TimeSpan.FromMinutes(15));
      }
    }
  }
  catch (TaskCanceledException)
  {
    return Results.Problem("Fetch timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
  }
  catch (InvalidOperationException ex)
  {
    return Results.BadRequest(new { error = ex.Message });
  }
  catch (HttpRequestException ex)
  {
    var status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int)HttpStatusCode.BadGateway;
    return Results.Problem(ex.Message, statusCode: status);
  }
  catch (Exception ex)
  {
    return Results.Problem(ex.Message, statusCode: (int)HttpStatusCode.BadGateway);
  }

  if (playlistText is null)
  {
    return Results.BadRequest(new { error = "Unable to load playlist from cache or sourceUrl." });
  }

  var disabled = new HashSet<string>(input.DisabledGroups ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
  var filtered = PlaylistProcessor.GenerateFiltered(playlistText, disabled);
  var response = new GeneratePlaylistResponse(filtered.Text, filtered.TotalChannels, filtered.KeptChannels);
  return Results.Ok(response);
});

app.MapGet("/api/playlists/{id:guid}/share/{code}", async (Guid id, string code, AppDbContext db, IHttpClientFactory httpClientFactory) =>
{
  var playlist = await db.Playlists.FindAsync(id);
  if (playlist is null) return Results.NotFound();
  if (!string.Equals(playlist.ShareCode, code, StringComparison.Ordinal))
  {
    return Results.Unauthorized();
  }
  if (string.IsNullOrWhiteSpace(playlist.SourceUrl))
  {
    return Results.BadRequest(new { error = "Playlist is missing sourceUrl." });
  }

  try
  {
    var text = await FetchPlaylistText(playlist.SourceUrl, httpClientFactory);
    var disabled = new HashSet<string>(playlist.DisabledGroups ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
    var filtered = PlaylistProcessor.GenerateFiltered(text, disabled);
    var fileName = $"{(playlist.SourceName ?? playlist.Name ?? "playlist")}-filtered.m3u";
    return Results.File(Encoding.UTF8.GetBytes(filtered.Text), "application/x-mpegurl", fileDownloadName: fileName);
  }
  catch (TaskCanceledException)
  {
    return Results.Problem("Fetch timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
  }
  catch (HttpRequestException ex)
  {
    var status = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int)HttpStatusCode.BadGateway;
    return Results.Problem(ex.Message, statusCode: status);
  }
  catch (Exception ex)
  {
    return Results.Problem(ex.Message, statusCode: (int)HttpStatusCode.BadGateway);
  }
});

app.MapGet("/api/fetch", async (string url, IHttpClientFactory httpClientFactory) =>
{
  if (string.IsNullOrWhiteSpace(url))
  {
    return Results.BadRequest(new { error = "url is required" });
  }

  if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
  {
    return Results.BadRequest(new { error = "Only http/https URLs are allowed" });
  }

  try
  {
    var client = httpClientFactory.CreateClient("fetcher");
    using var res = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
    if (!res.IsSuccessStatusCode)
    {
      return Results.StatusCode((int)res.StatusCode);
    }
    var bytes = await res.Content.ReadAsByteArrayAsync();
    // Return with a content-length to avoid proxy/protocol quirks.
    return Results.File(bytes, "application/x-mpegurl; charset=utf-8");
  }
  catch (TaskCanceledException)
  {
    return Results.Problem("Fetch timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
  }
  catch (Exception ex)
  {
    return Results.Problem(ex.Message, statusCode: (int)HttpStatusCode.BadGateway);
  }
});

app.MapPost("/api/fetch", async (FetchRequest request, IHttpClientFactory httpClientFactory) =>
{
  var url = request.Url;
  if (string.IsNullOrWhiteSpace(url))
  {
    return Results.BadRequest(new { error = "url is required" });
  }

  if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
  {
    return Results.BadRequest(new { error = "Only http/https URLs are allowed" });
  }

  try
  {
    var client = httpClientFactory.CreateClient("fetcher");
    using var res = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
    if (!res.IsSuccessStatusCode)
    {
      return Results.StatusCode((int)res.StatusCode);
    }
    var bytes = await res.Content.ReadAsByteArrayAsync();
    return Results.File(bytes, "application/x-mpegurl; charset=utf-8");
  }
  catch (TaskCanceledException)
  {
    return Results.Problem("Fetch timed out", statusCode: (int)HttpStatusCode.GatewayTimeout);
  }
  catch (Exception ex)
  {
    return Results.Problem(ex.Message, statusCode: (int)HttpStatusCode.BadGateway);
  }
});

// SPA fallback
app.MapFallbackToFile("/index.html");

app.Run();

public record PlaylistRequest(
  string Name,
  string? SourceUrl,
  string? SourceName,
  List<string>? DisabledGroups,
  int? TotalChannels,
  int? GroupCount,
  DateTimeOffset? ExpirationUtc,
  string? ShareCode);

public record FetchRequest(string Url);
