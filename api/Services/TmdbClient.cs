using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Api.Services;

/// <summary>
/// <see cref="RateLimited"/>: TMDB answered 429, or Strim is still waiting out its Retry-After and sent no request.
/// </summary>
public enum TmdbStatus { Ok, Disabled, NotFound, Unavailable, RateLimited }

public sealed record TmdbResult<T>(TmdbStatus Status, T? Value) where T : class;

public sealed record TmdbMovieSummary(int Id, string? Title, string? ReleaseDate, string? PosterPath, double VoteAverage)
{
  public string? OriginalTitle { get; init; }
  public bool Adult { get; init; }
}

public sealed record TmdbMoviePage(IReadOnlyList<TmdbMovieSummary>? Results)
{
  public int Page { get; init; }
  public int TotalPages { get; init; }
}

public sealed record TmdbGenre(int Id, string? Name);

public sealed record TmdbMovieDetails(
  int Id, string? Title, string? Overview, string? ReleaseDate, string? PosterPath, double VoteAverage,
  IReadOnlyList<TmdbGenre>? Genres, TmdbMoviePage? Similar);

public sealed record TmdbTvSummary(int Id, string? Name, string? FirstAirDate, string? PosterPath, double VoteAverage)
{
  public string? OriginalName { get; init; }
  public bool Adult { get; init; }
}

public sealed record TmdbTvPage(IReadOnlyList<TmdbTvSummary>? Results)
{
  public int Page { get; init; }
  public int TotalPages { get; init; }
}

public sealed record TmdbTvSeason(int SeasonNumber, int? EpisodeCount);

public sealed record TmdbNetwork(int Id, string? Name);

public sealed record TmdbTvDetails(
  int Id, string? Name, string? Overview, string? FirstAirDate, string? LastAirDate, string? Status,
  int? NumberOfSeasons, int? NumberOfEpisodes, string? PosterPath, double VoteAverage,
  IReadOnlyList<TmdbGenre>? Genres, IReadOnlyList<TmdbNetwork>? Networks, IReadOnlyList<TmdbTvSeason>? Seasons,
  TmdbTvPage? Similar);

/// <summary>
/// The only place Strim talks to TMDB. Requests go to one fixed HTTPS host, the token travels in the
/// Authorization header only, redirects, timeouts and errors become Unavailable, and nothing derived from the
/// token or the query is ever logged or thrown. A 429 is never retried: every call returns RateLimited without a
/// request until its Retry-After (seconds or an HTTP date, capped at <see cref="MaxBackoff"/>) has passed.
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

  /// <summary>The pause after a 429 without a readable Retry-After.</summary>
  public static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(60);

  /// <summary>The longest pause a Retry-After can ask for, so a bad header cannot switch TMDB off for days.</summary>
  public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

  /// <summary>How long a trending answer is kept: TMDB updates the list daily.</summary>
  public static readonly TimeSpan TrendingLifetime = TimeSpan.FromHours(6);

  /// <summary>Films trending this week; <paramref name="page"/> 1 to 3, page 1 without a query string.</summary>
  public Task<TmdbResult<TmdbMoviePage>> GetTrendingMoviesAsync(int page, CancellationToken cancellationToken) =>
    SendAsync<TmdbMoviePage>(TrendingPath("trending/movie/week", page), cancellationToken, TrendingLifetime);

  /// <summary>Series trending this week; <paramref name="page"/> 1 to 3, page 1 without a query string.</summary>
  public Task<TmdbResult<TmdbTvPage>> GetTrendingTvAsync(int page, CancellationToken cancellationToken) =>
    SendAsync<TmdbTvPage>(TrendingPath("trending/tv/week", page), cancellationToken, TrendingLifetime);

  private static string TrendingPath(string path, int page)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(page, 3);
    return page == 1 ? path : path + "?page=" + page.ToString(CultureInfo.InvariantCulture);
  }

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

  /// <summary>Searches series by the cleaned title and, when known, the first-air year; nothing else is sent.</summary>
  public Task<TmdbResult<TmdbTvPage>> SearchTvAsync(string title, int? year, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(title);
    var query = "search/tv?include_adult=false&query=" + Uri.EscapeDataString(title.Trim());
    if (year is int y)
    {
      query += "&first_air_date_year=" + y.ToString(CultureInfo.InvariantCulture);
    }
    return SendAsync<TmdbTvPage>(query, cancellationToken);
  }

  /// <summary>Details of one series (with its seasons) and its similar series, fetched in one request.</summary>
  public Task<TmdbResult<TmdbTvDetails>> GetTvWithSimilarAsync(int tvId, CancellationToken cancellationToken)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tvId);
    return SendAsync<TmdbTvDetails>(
      "tv/" + tvId.ToString(CultureInfo.InvariantCulture) + "?append_to_response=similar",
      cancellationToken);
  }

  private async Task<TmdbResult<T>> SendAsync<T>(string relativeUri, CancellationToken cancellationToken,
    TimeSpan? lifetime = null) where T : class
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
        return new(TmdbStatus.RateLimited, null);
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
        var pause = BlockUntilRetryAfter(response.Headers.RetryAfter);
        // Neither the token nor the query: only how long Strim waits.
        _logger.LogWarning("TMDB rate limit reached; pausing requests for {Seconds} s.",
          (int)Math.Ceiling(pause.TotalSeconds));
        return new(TmdbStatus.RateLimited, null);
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
      _cache?.Set(relativeUri, body, lifetime);
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

  /// <summary>Blocks calls for the Retry-After pause (or the default), capped at <see cref="MaxBackoff"/>.</summary>
  private TimeSpan BlockUntilRetryAfter(RetryConditionHeaderValue? retryAfter)
  {
    var now = _timeProvider.GetUtcNow();
    var pause = retryAfter?.Delta is TimeSpan delta ? delta
      : retryAfter?.Date is DateTimeOffset date ? date - now
      : DefaultBackoff;
    pause = pause < TimeSpan.Zero ? TimeSpan.Zero : pause > MaxBackoff ? MaxBackoff : pause;
    var until = now + pause;
    lock (_gate)
    {
      if (until > _blockedUntil)
      {
        _blockedUntil = until;
      }
    }
    return pause;
  }
}
