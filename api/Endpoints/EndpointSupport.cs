using System.Net;
using System.Security.Claims;
using Api.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace Api.Endpoints;

/// <summary>Input limits shared by the endpoints.</summary>
public static class Limits
{
  public const int MaxUrlLength = 2048;
  public const int MaxPlaylistNameLength = 200;
  public const int MaxPlaylistTextSize = 50 * 1024 * 1024;
  /// <summary>Raw text plus room for the JSON envelope.</summary>
  public const long MaxAnalyzeRequestBodySize = MaxPlaylistTextSize + (1L * 1024 * 1024);
  /// <summary>Matches PlaylistCacheOptions.MaxGroupCount so every group can be hidden.</summary>
  public const int MaxDisabledGroupsCount = 10_000;
}

public static class EndpointFilters
{
  public const string CsrfError = "Invalid or missing CSRF token";

  /// <summary>
  /// Validates the X-CSRF-TOKEN header. Session cookies are SameSite=None in production so that
  /// cross-origin clients work, which makes the token the protection for state-changing requests.
  /// </summary>
  public static TBuilder RequireCsrfToken<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
    builder.AddEndpointFilter(async (context, next) =>
    {
      var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
      try
      {
        await antiforgery.ValidateRequestAsync(context.HttpContext);
      }
      catch (AntiforgeryValidationException)
      {
        return Results.BadRequest(new { error = CsrfError });
      }
      return await next(context);
    });
}

public static class EndpointHelpers
{
  public static string? UserId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);

  public static IResult Busy() =>
    Results.Problem("Playlist processing is busy. Please retry shortly.", statusCode: StatusCodes.Status429TooManyRequests);

  /// <summary>
  /// Maps a failure while fetching or processing a playlist to a response. Messages of
  /// InvalidOperationException are controlled (URL validation, SSRF, limits) and safe to show.
  /// </summary>
  public static IResult PlaylistFailure(Exception exception, ILogger logger, string operation) => exception switch
  {
    PlaylistSizeExceededException or PlaylistGeneratedSizeExceededException =>
      Results.Problem(exception.Message, statusCode: StatusCodes.Status413PayloadTooLarge),
    PlaylistDiskCapacityExceededException =>
      Results.Problem(exception.Message, statusCode: StatusCodes.Status507InsufficientStorage),
    OperationCanceledException =>
      Results.Problem("Fetch or processing timed out", statusCode: (int)HttpStatusCode.GatewayTimeout),
    InvalidOperationException =>
      Results.BadRequest(new { error = exception.Message }),
    HttpRequestException http => UpstreamFailure(http, logger, operation),
    _ => UnexpectedFailure(exception, logger, operation),
  };

  private static IResult UpstreamFailure(HttpRequestException exception, ILogger logger, string operation)
  {
    logger.LogWarning(exception, "HTTP request failed during playlist {Operation}", operation);
    var status = exception.StatusCode.HasValue ? (int)exception.StatusCode.Value : (int)HttpStatusCode.BadGateway;
    return Results.Problem(UpstreamMessage(exception), statusCode: status);
  }

  private static IResult UnexpectedFailure(Exception exception, ILogger logger, string operation)
  {
    logger.LogError(exception, "Unexpected error during playlist {Operation}", operation);
    return Results.Problem($"Failed to {operation} playlist", statusCode: (int)HttpStatusCode.BadGateway);
  }

  private static string UpstreamMessage(HttpRequestException exception)
  {
    if (exception.Message.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase))
    {
      return exception.Message;
    }
    if (exception.StatusCode == HttpStatusCode.Forbidden)
    {
      return "The playlist source rejected Strim's backend request (HTTP 403). The provider may block cloud/proxy servers or require access from your own network.";
    }
    return exception.StatusCode.HasValue
      ? $"The playlist source returned HTTP {(int)exception.StatusCode.Value}."
      : "Failed to fetch the playlist from the source";
  }
}
