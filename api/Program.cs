using Api.Configuration;
using Api.Data;
using Api.Endpoints;
using Api.Services;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var environment = builder.Environment;

var publicShareBaseUrl = configuration["PUBLIC_SHARE_BASE_URL"]?.Trim().TrimEnd('/');
if (!string.IsNullOrWhiteSpace(publicShareBaseUrl) &&
    (!Uri.TryCreate(publicShareBaseUrl, UriKind.Absolute, out var shareBaseUri) ||
     (shareBaseUri.Scheme != Uri.UriSchemeHttps && shareBaseUri.Scheme != Uri.UriSchemeHttp)))
{
  throw new InvalidOperationException("PUBLIC_SHARE_BASE_URL must be an absolute HTTP or HTTPS URL.");
}

builder.Services
  .AddStrimForwardedHeaders(configuration)
  .AddStrimDatabase(configuration)
  .AddStrimCors(configuration, environment)
  .AddStrimRateLimits()
  .AddStrimAntiforgery(environment)
  .AddStrimAuthentication(configuration, environment)
  .AddStrimPlaylistServices(configuration);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new() { Title = "Strim API", Version = "v1" }));
// JSON and text only: large M3U output is streamed as-is, compressing it is the edge proxy's job.
builder.Services.AddResponseCompression(options =>
{
  options.EnableForHttps = true;
  options.MimeTypes = new[] { "application/json", "text/plain" };
});

var app = builder.Build();
app.Services.EnsureStrimSchema();

app.UseForwardedHeaders();
// Some proxies (Azure App Service) mark HTTPS only with X-ARR-SSL, not X-Forwarded-Proto.
app.Use((context, next) =>
{
  if (!string.IsNullOrEmpty(context.Request.Headers["X-ARR-SSL"].FirstOrDefault()))
  {
    context.Request.Scheme = "https";
  }
  return next();
});
app.UseAnalyzeBodyGate();
app.UseExceptionHandler();
app.UseResponseCompression();

app.UseSwagger();
if (app.Environment.IsDevelopment())
{
  app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Strim API v1"));
}

app.Use(async (context, next) =>
{
  const string header = "X-Correlation-ID";
  var correlationId = context.Request.Headers[header].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
  context.Response.Headers[header] = correlationId;
  using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
  {
    await next();
  }
});

app.UseCors(Security.CorsPolicy);
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
  // A page and the scripts it references must never be mixed across deploys; see StaticAssetCaching.
  OnPrepareResponse = context =>
  {
    var request = context.Context.Request;
    context.Context.Response.Headers.CacheControl =
      StaticAssetCaching.GetCacheControl(request.Path.Value ?? string.Empty, request.Query.ContainsKey("v"));
  },
});
app.UseWhen(
  context => !context.Request.Path.StartsWithSegments("/api"),
  site => site.UseStatusCodePagesWithReExecute("/error/{0}"));

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapAuthEndpoints();
app.MapPlaylistEndpoints();
app.MapProcessingEndpoints();
app.MapSiteEndpoints(app.Environment, publicShareBaseUrl);

app.Run();

// Lets the integration tests host the app with WebApplicationFactory<Program>.
public partial class Program { }
