using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Api.Services;

public enum TmdbStatus { Ok, Disabled, NotFound, Unavailable }

public sealed record TmdbResult<T>(TmdbStatus Status, T? Value) where T : class;

public sealed record TmdbMovieSummary(int Id, string? Title, string? ReleaseDate, string? PosterPath, double VoteAverage);

public sealed record TmdbMoviePage(IReadOnlyList<TmdbMovieSummary>? Results);

public sealed record TmdbGenre(int Id, string? Name);

public sealed record TmdbMovieDetails(
  int Id, string? Title, string? Overview, string? ReleaseDate, string? PosterPath, double VoteAverage,
  IReadOnlyList<TmdbGenre>? Genres, TmdbMoviePage? Similar);

/// <summary>
/// The only place Strim talks to TMDB. Requests go to one fixed HTTPS host, the token travels in the
/// Authorization header only, redirects, timeouts, errors and rate limits all become Unavailable, and
/// nothing derived from the token is ever logged or thrown.
/// </summary>
public sealed class TmdbClient
{
  public const string HttpClientName = "tmdb";
  public const string TokenVariable = "TMDB_READ_ACCESS_TOKEN";
  public static readonly Uri BaseAddress = new("https://api.themoviedb.org/3/");

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
  };

  private readonly IHttpClientFactory _httpClientFactory;
  private readonly ILogger<TmdbClient> _logger;
  private readonly TimeProvider _timeProvider;
  private readonly string? _token;
  private readonly TmdbCache? _cache;
  private readonly object _gate = new();
  private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

  public TmdbClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<TmdbClient> logger,
    TimeProvider timeProvider, TmdbCache? cache = null)
  {
    _cache = cache;
    _httpClientFactory = httpClientFactory;
    _logger = logger;
    _timeProvider = timeProvider;
    var token = configuration[TokenVariable]?.Trim();
    _token = string.IsNullOrEmpty(token) ? null : token;
  }

  public bool IsEnabled => _token is not null;

  internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

  internal TimeSpan DefaultBackoff { get; init; } = TimeSpan.FromSeconds(60);

  public Task<TmdbResult<TmdbMoviePage>> SearchMovieAsync(string title, int? year, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(title);
    var query = "search/movie?include_adult=false&query=" + Uri.EscapeDataString(title.Trim());
    if (year is int y)
    {
      query += "&year=" + y.ToString(CultureInfo.InvariantCulture);
    }
    return SendAsync<TmdbMoviePage>(query, cancellationToken);
  }

  /// <summary>Details of one film with its similar films, fetched in one request.</summary>
  public Task<TmdbResult<TmdbMovieDetails>> GetMovieWithSimilarAsync(int movieId, CancellationToken cancellationToken)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(movieId);
    return SendAsync<TmdbMovieDetails>(
      "movie/" + movieId.ToString(CultureInfo.InvariantCulture) + "?append_to_response=similar",
      cancellationToken);
  }

  private async Task<TmdbResult<T>> SendAsync<T>(string relativeUri, CancellationToken cancellationToken) where T : class
  {
    if (_token is null)
    {
      return new(TmdbStatus.Disabled, null);
    }
    if (_cache is not null && _cache.TryGet(relativeUri, out var cached)
      && JsonSerializer.Deserialize<T>(cached, JsonOptions) is { } hit)
    {
      return new(TmdbStatus.Ok, hit);
    }
    lock (_gate)
    {
      if (_timeProvider.GetUtcNow() < _blockedUntil)
      {
        return new(TmdbStatus.Unavailable, null);
      }
    }

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(Timeout);
    try
    {
      using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress, relativeUri));
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
      request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

      var client = _httpClientFactory.CreateClient(HttpClientName);
      using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
      if (response.StatusCode == HttpStatusCode.TooManyRequests)
      {
        BlockUntilRetryAfter(response.Headers.RetryAfter);
        _logger.LogWarning("TMDB rate limit reached; pausing requests.");
        return new(TmdbStatus.Unavailable, null);
      }
      if (response.StatusCode == HttpStatusCode.NotFound)
      {
        return new(TmdbStatus.NotFound, null);
      }
      if (!response.IsSuccessStatusCode)
      {
        // Includes 3xx: redirects are never followed, the host is fixed.
        _logger.LogWarning("TMDB answered {StatusCode}.", (int)response.StatusCode);
        return new(TmdbStatus.Unavailable, null);
      }

      var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
      var value = JsonSerializer.Deserialize<T>(body, JsonOptions);
      if (value is null)
      {
        return new(TmdbStatus.Unavailable, null);
      }
      // Only successful answers are cached; errors, rate limits and timeouts return before this line.
      _cache?.Set(relativeUri, body);
      return new(TmdbStatus.Ok, value);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      // A fresh exception: the transport's message and inner exceptions can echo request details.
      throw new OperationCanceledException("The TMDB request was cancelled.", cancellationToken);
    }
    catch (Exception ex)
    {
      // Only the exception type: messages of transport errors can echo request details.
      _logger.LogWarning("TMDB request failed ({ErrorType}).", ex.GetType().Name);
      return new(TmdbStatus.Unavailable, null);
    }
  }

  private void BlockUntilRetryAfter(RetryConditionHeaderValue? retryAfter)
  {
    var now = _timeProvider.GetUtcNow();
    var until = retryAfter?.Delta is TimeSpan delta ? now + delta
      : retryAfter?.Date is DateTimeOffset date ? date
      : now + DefaultBackoff;
    lock (_gate)
    {
      if (until > _blockedUntil)
      {
        _blockedUntil = until;
      }
    }
  }
}
