using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Linq;
using Microsoft.Extensions.Logging;
using Strim.Api.Application.Playlists;
using Strim.Api.Contracts.Playlists;
using Strim.Api.Data;
using Strim.Api.Domain;
using Strim.Api.Infrastructure.Playlists;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<StrimDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));

builder.Services.AddHttpClient("playlist", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Strim/1.0");
});

builder.Services.AddScoped<IPlaylistParser, M3uPlaylistParser>();
builder.Services.AddScoped<IPlaylistIngestionService, PlaylistIngestionService>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = services.GetRequiredService<StrimDbContext>();
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "An error occurred while applying database migrations");
        throw;
    }
}

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTimeOffset.UtcNow
}));

app.MapGet("/api/playlists", async (StrimDbContext dbContext, CancellationToken cancellationToken) =>
{
    var playlists = await dbContext.Playlists
        .AsNoTracking()
        .Select(p => new PlaylistSummaryResponse
        {
            Id = p.Id,
            Name = p.Name,
            Source = p.Source,
            SourceType = p.SourceType.ToString(),
            ChannelCount = p.Channels.Count,
            CreatedAt = p.CreatedAt
        })
        .OrderByDescending(p => p.CreatedAt)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(playlists);
});

app.MapPost("/api/playlists/parse", async Task<IResult> (
    PlaylistParseRequest request,
    IPlaylistIngestionService ingestionService,
    CancellationToken cancellationToken) =>
{
    if (request is null)
    {
        return Results.BadRequest(new { error = "Request body is required" });
    }

    var (source, sourceType, validationError) = ValidateRequest(request);
    if (validationError is not null)
    {
        return Results.BadRequest(new { error = validationError });
    }

    try
    {
        var result = await ingestionService.IngestAsync(source!, sourceType!, request.Name, cancellationToken).ConfigureAwait(false);
        var response = new PlaylistParseResponse
        {
            Playlist = PlaylistMapper.ToSummary(result.Playlist),
            Channels = PlaylistMapper.ToChannelResponses(result.Channels.Take(25))
        };

        return Results.Ok(response);
    }
    catch (PlaylistParseException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();

static (string? Source, PlaylistSourceType? SourceType, string? Error) ValidateRequest(PlaylistParseRequest request)
{
    var hasUrl = !string.IsNullOrWhiteSpace(request.Url);
    var hasFile = !string.IsNullOrWhiteSpace(request.FilePath);

    if (hasUrl == hasFile)
    {
        return (null, null, "Provide exactly one of url or filePath");
    }

    if (hasUrl)
    {
        return (request.Url!.Trim(), PlaylistSourceType.Url, null);
    }

    return (request.FilePath!.Trim(), PlaylistSourceType.FilePath, null);
}

public partial class Program;
