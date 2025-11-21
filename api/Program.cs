using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
  builder.Configuration.GetConnectionString("Postgres") ??
  builder.Configuration["POSTGRES_CONNECTION"] ??
  "Host=localhost;Port=5432;Database=strim;Username=postgres;Password=postgres";

// Configure Npgsql data source with dynamic JSON enabled so we can store List<string> as jsonb.
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.EnableDynamicJson();
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(dataSource));
builder.Services.AddCors(options =>
{
  options.AddPolicy("default", policy =>
    policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
});
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
  db.Database.EnsureCreated();
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

  var entity = new Playlist
  {
    Name = input.Name.Trim(),
    SourceUrl = input.SourceUrl,
    SourceName = input.SourceName,
    RawText = input.RawText ?? string.Empty,
    FilteredText = input.FilteredText ?? string.Empty,
    DisabledGroups = input.DisabledGroups ?? new List<string>(),
  };

  db.Playlists.Add(entity);
  await db.SaveChangesAsync();
  return Results.Created($"/api/playlists/{entity.Id}", entity);
});

app.MapPut("/api/playlists/{id:guid}", async (Guid id, PlaylistRequest input, AppDbContext db) =>
{
  var entity = await db.Playlists.FindAsync(id);
  if (entity is null) return Results.NotFound();

  if (string.IsNullOrWhiteSpace(input.Name))
  {
    return Results.BadRequest(new { error = "Name is required." });
  }

  entity.Name = input.Name.Trim();
  entity.SourceUrl = input.SourceUrl;
  entity.SourceName = input.SourceName;
  entity.RawText = input.RawText ?? string.Empty;
  entity.FilteredText = input.FilteredText ?? string.Empty;
  entity.DisabledGroups = input.DisabledGroups ?? new List<string>();

  await db.SaveChangesAsync();
  return Results.Ok(entity);
});

app.MapDelete("/api/playlists/{id:guid}", async (Guid id, AppDbContext db) =>
{
  var entity = await db.Playlists.FindAsync(id);
  if (entity is null) return Results.NotFound();
  db.Playlists.Remove(entity);
  await db.SaveChangesAsync();
  return Results.NoContent();
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
  string? RawText,
  string? FilteredText,
  List<string>? DisabledGroups);

public record FetchRequest(string Url);
