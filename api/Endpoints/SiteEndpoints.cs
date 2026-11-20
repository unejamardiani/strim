using System.Net;
using System.Text.RegularExpressions;
using Api.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Api.Endpoints;

/// <summary>Health probes, public config, the two HTML entry points and error pages.</summary>
public static class SiteEndpoints
{
  public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder app, IWebHostEnvironment environment, string? publicShareBaseUrl)
  {
    var webRoot = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");

    app.MapHealthChecks("/health", new HealthCheckOptions
    {
      ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
      {
        status = report.Status.ToString(),
        checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description }),
        duration = report.TotalDuration.TotalMilliseconds,
      }),
    });
    // Liveness: the process answers. Readiness: the database answers too.
    app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Name == "database" });
    app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

    // tmdbEnabled tells the frontend whether TMDB data and attribution may appear; never the token itself.
    // shareAccessNote is the instance's note about its access list for the invite hint (Sharing:AccessNote).
    app.MapGet("/api/config", (TmdbClient tmdbClient, IConfiguration configuration) =>
      Results.Ok(new
      {
        publicShareBaseUrl, tmdbEnabled = tmdbClient.IsEnabled,
        shareAccessNote = configuration["Sharing:AccessNote"]?.Trim() ?? "",
      }));

    // The landing page is the default document; the editor lives at /app. The route also matches
    // "/app/", where the page's relative asset paths would resolve under /app/, so redirect that.
    app.MapGet("/app", (HttpContext context) =>
      context.Request.Path.Value!.EndsWith('/')
        ? Results.Redirect($"/app{context.Request.QueryString}", permanent: true)
        : Page(context, Path.Combine(webRoot, "app.html")));

    app.MapGet("/error/{statusCode:int}", async (int statusCode, HttpContext context) =>
    {
      var page = ErrorPages.For(statusCode);
      var path = Path.Combine(webRoot, page.File);
      if (!File.Exists(path)) return Results.Problem($"Error {statusCode}", statusCode: statusCode);

      context.Response.StatusCode = statusCode;
      if (page.Text is null) return Results.File(path, "text/html; charset=utf-8");

      var html = await File.ReadAllTextAsync(path, context.RequestAborted);
      return Results.Content(WithText(html, page.Text), "text/html; charset=utf-8", statusCode: statusCode);
    });

    // Unknown API routes are a JSON 404; anything else gets the HTML 404 page.
    app.MapFallback("/api/{**path}", () => Results.NotFound(new { error = "Not found" }));
    app.MapFallback((HttpContext context) =>
    {
      var path = Path.Combine(webRoot, "404.html");
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      return File.Exists(path)
        ? Results.File(path, "text/html; charset=utf-8")
        : Results.Text("404 - Not Found", "text/plain", statusCode: StatusCodes.Status404NotFound);
    });

    return app;
  }

  // Puts the rule's constant text into an existing error page: title, code (eyebrow and TV channel),
  // heading and lead. Markup, styles, header and footer stay those of the page.
  private static string WithText(string html, ErrorPageText text)
  {
    var code = WebUtility.HtmlEncode(text.Code);
    html = Replace(html, @"<title>.*?</title>", $"<title>{WebUtility.HtmlEncode(text.Title)} | Strim</title>");
    html = Replace(html, @"<p class=""eyebrow"">Error .*?</p>", $"<p class=\"eyebrow\">Error {code}</p>");
    html = Replace(html, @"<span class=""tv-channel"">CH .*?</span>", $"<span class=\"tv-channel\">CH {code}</span>");
    html = Replace(html, @"<h1>.*?</h1>", $"<h1>{WebUtility.HtmlEncode(text.Heading)}</h1>");
    return Replace(html, @"<p class=""lead"">.*?</p>", $"<p class=\"lead\">{WebUtility.HtmlEncode(text.Lead)}</p>");
  }

  private static string Replace(string html, string pattern, string replacement) =>
    Regex.Replace(html, pattern, _ => replacement, RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

  private static IResult Page(HttpContext context, string path)
  {
    if (!File.Exists(path)) return Results.NotFound();
    context.Response.Headers.CacheControl = StaticAssetCaching.Revalidate;
    return Results.File(path, "text/html; charset=utf-8");
  }
}
