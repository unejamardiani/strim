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

    app.MapGet("/api/config", () => Results.Ok(new { publicShareBaseUrl }));

    // The landing page is the default document; the editor lives at /app. The route also matches
    // "/app/", where the page's relative asset paths would resolve under /app/, so redirect that.
    app.MapGet("/app", (HttpContext context) =>
      context.Request.Path.Value!.EndsWith('/')
        ? Results.Redirect($"/app{context.Request.QueryString}", permanent: true)
        : Page(context, Path.Combine(webRoot, "app.html")));

    app.MapGet("/error/{statusCode:int}", (int statusCode, HttpContext context) =>
    {
      var page = statusCode switch
      {
        404 => "404.html",
        500 => "500.html",
        >= 500 and < 600 => "50x.html",
        _ => "404.html",
      };
      var path = Path.Combine(webRoot, page);
      if (!File.Exists(path)) return Results.Problem($"Error {statusCode}", statusCode: statusCode);

      context.Response.StatusCode = statusCode;
      return Results.File(path, "text/html; charset=utf-8");
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

  private static IResult Page(HttpContext context, string path)
  {
    if (!File.Exists(path)) return Results.NotFound();
    context.Response.Headers.CacheControl = StaticAssetCaching.Revalidate;
    return Results.File(path, "text/html; charset=utf-8");
  }
}
