namespace Api.Configuration;

/// <summary>Security headers on every response and no-store on API answers.</summary>
public static class SecurityHeaders
{
  /// <summary>
  /// Adds the headers when the response starts, so they also land on static files, error pages
  /// and responses rewritten by the exception handler. The page CSPs stay in the pages' meta tags;
  /// the header only carries frame-ancestors, which a meta tag cannot set.
  /// </summary>
  public static IApplicationBuilder UseStrimSecurityHeaders(this IApplicationBuilder app, IWebHostEnvironment environment) =>
    app.Use((context, next) =>
    {
      context.Response.OnStarting(() =>
      {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy = "frame-ancestors 'none'";
        if (context.Request.IsHttps && environment.IsProduction())
        {
          headers.StrictTransportSecurity = "max-age=31536000";
        }
        // Endpoints that set their own policy (the share route) keep it.
        if (context.Request.Path.StartsWithSegments("/api") && string.IsNullOrEmpty(headers.CacheControl))
        {
          headers.CacheControl = "no-store";
        }
        return Task.CompletedTask;
      });
      return next();
    });
}
