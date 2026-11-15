using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Api.Services;

/// <summary>
/// Downloads an upstream playlist directly to a temporary file, validating every redirect and
/// computing a content hash along the way. No complete HTTP body is materialized in memory.
/// </summary>
public sealed class PlaylistSourceFetcher
{
  private const int MaxRedirects = 5;
  private readonly IHttpClientFactory _httpClientFactory;
  private readonly PlaylistFileCache _cache;
  private readonly PlaylistCacheOptions _options;
  private readonly ILogger<PlaylistSourceFetcher> _logger;

  public PlaylistSourceFetcher(
    IHttpClientFactory httpClientFactory,
    PlaylistFileCache cache,
    IOptions<PlaylistCacheOptions> options,
    ILogger<PlaylistSourceFetcher> logger)
  {
    _httpClientFactory = httpClientFactory;
    _cache = cache;
    _options = options.Value;
    _logger = logger;
  }

  public async Task<PlaylistFetchResult> FetchAsync(
    string sourceUrl,
    PlaylistSourceFile? cachedSource,
    CancellationToken cancellationToken)
  {
    var initialUri = ValidateSourceUri(sourceUrl);
    var client = _httpClientFactory.CreateClient(Configuration.PlaylistServices.FetcherClient);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.DownloadTimeoutSeconds)));

    var currentUri = initialUri;
    for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
    {
      using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
      var canUseCachedValidators = SameOrigin(initialUri, currentUri);
      if (canUseCachedValidators)
      {
        AddConditionalHeaders(request, cachedSource);
      }

      try
      {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
        {
          if (redirectCount >= MaxRedirects)
          {
            throw new InvalidOperationException("Too many redirects");
          }

          currentUri = ValidateRedirectUri(currentUri, response.Headers.Location);
          continue;
        }

        if (response.StatusCode == HttpStatusCode.NotModified &&
            canUseCachedValidators &&
            cachedSource is not null &&
            File.Exists(cachedSource.FilePath))
        {
          return PlaylistFetchResult.Unchanged(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        }

        if (!response.IsSuccessStatusCode)
        {
          throw new HttpRequestException($"Upstream returned {(int)response.StatusCode}", null, response.StatusCode);
        }

        var contentLength = response.Content.Headers.ContentLength;
        var reservation = _cache.ReserveSourceCapacity(contentLength);
        var temporaryPath = _cache.CreateTemporaryPath("source");
        try
        {
          var downloaded = await CopyToFileAsync(response, temporaryPath, timeout.Token);
          _logger.LogInformation(
            "Downloaded playlist source from {Host}: {LengthBytes} bytes, sha256 {HashPrefix}",
            currentUri.Host,
            downloaded.LengthBytes,
            downloaded.ContentHash[..12]);
          return PlaylistFetchResult.Downloaded(downloaded with { DiskReservation = reservation });
        }
        catch
        {
          _cache.DeleteTemporaryFile(temporaryPath, reservation.ReservedBytes);
          reservation.Dispose();
          throw;
        }
      }
      catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException socketEx)
      {
        _logger.LogWarning(ex, "Connection failed while fetching playlist source from {Host}: {SocketError}", currentUri.Host, socketEx.SocketErrorCode);
        throw new HttpRequestException("Unable to connect to the playlist source. Please check the URL and try again.", ex);
      }
      catch (HttpRequestException ex) when (ex.StatusCode is null)
      {
        _logger.LogWarning(ex, "Network error while fetching playlist source from {Host}", currentUri.Host);
        throw new HttpRequestException("Unable to connect to the playlist source. The server may be unreachable or the URL may be incorrect.", ex);
      }
    }

    throw new InvalidOperationException("Too many redirects");
  }

  /// <summary>
  /// Reads a short text answer (for example a provider's account JSON) into memory with the same
  /// address checks, redirect validation, HTTP client and timeout as <see cref="FetchAsync"/>.
  /// Bodies above <see cref="SmallTextMaxBytes"/> are refused. Every failure is returned as a result;
  /// messages and log entries name only the host, never the path or query string.
  /// </summary>
  public async Task<SmallTextFetchResult> FetchSmallTextAsync(string sourceUrl, CancellationToken cancellationToken)
  {
    Uri currentUri;
    try
    {
      currentUri = ValidateSourceUri(sourceUrl);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
      return SmallTextFetchResult.Failed(ex.Message);
    }

    var client = _httpClientFactory.CreateClient(Configuration.PlaylistServices.FetcherClient);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.DownloadTimeoutSeconds)));

    try
    {
      for (var redirectCount = 0; ; redirectCount++)
      {
        using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
        {
          if (redirectCount >= MaxRedirects)
          {
            return Fail(currentUri, "Too many redirects");
          }

          try
          {
            currentUri = ValidateRedirectUri(currentUri, response.Headers.Location);
          }
          catch (InvalidOperationException ex)
          {
            return Fail(currentUri, ex.Message);
          }

          continue;
        }

        if (!response.IsSuccessStatusCode)
        {
          return Fail(currentUri, $"Upstream returned {(int)response.StatusCode}", (int)response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > SmallTextMaxBytes)
        {
          return Fail(currentUri, "Response is too large");
        }

        var text = await ReadSmallTextAsync(response, timeout.Token);
        return text is null
          ? Fail(currentUri, "Response is too large")
          : SmallTextFetchResult.Succeeded(text, (int)response.StatusCode);
      }
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      return Fail(currentUri, "The request timed out");
    }
    catch (HttpRequestException)
    {
      return Fail(currentUri, "Unable to connect");
    }
    catch (IOException)
    {
      return Fail(currentUri, "Reading the response failed");
    }
  }

  /// <summary>Upper bound for <see cref="FetchSmallTextAsync"/> bodies.</summary>
  public const int SmallTextMaxBytes = 64 * 1024;

  private SmallTextFetchResult Fail(Uri uri, string error, int? statusCode = null)
  {
    _logger.LogWarning("Small text fetch from {Host} failed: {Error}", uri.Host, error);
    return SmallTextFetchResult.Failed(error, statusCode);
  }

  private static async Task<string?> ReadSmallTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
  {
    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var buffer = new MemoryStream();
    var chunk = new byte[8 * 1024];
    while (true)
    {
      var read = await source.ReadAsync(chunk, cancellationToken);
      if (read == 0) break;
      if (buffer.Length + read > SmallTextMaxBytes) return null;
      buffer.Write(chunk, 0, read);
    }

    var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
    var encoding = System.Text.Encoding.UTF8;
    if (!string.IsNullOrEmpty(charset))
    {
      try
      {
        encoding = System.Text.Encoding.GetEncoding(charset);
      }
      catch (ArgumentException)
      {
        // Unknown charset: fall back to UTF-8.
      }
    }

    return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
  }

  public static Uri ValidateSourceUri(string sourceUrl)
  {
    if (string.IsNullOrWhiteSpace(sourceUrl))
    {
      throw new ArgumentException("url is required", nameof(sourceUrl));
    }

    if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
      throw new InvalidOperationException("Only http/https URLs are allowed");
    }

    if (SecurityHelpers.IsBlockedUrl(uri))
    {
      throw new InvalidOperationException("Access to internal or private network addresses is not allowed");
    }

    return uri;
  }

  private static Uri ValidateRedirectUri(Uri currentUri, Uri location)
  {
    var redirectUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
    if (redirectUri.Scheme != Uri.UriSchemeHttp && redirectUri.Scheme != Uri.UriSchemeHttps)
    {
      throw new InvalidOperationException("Redirect to non-HTTP(S) URL is not allowed");
    }

    if (SecurityHelpers.IsBlockedUrl(redirectUri))
    {
      throw new InvalidOperationException("Redirect to internal or private network addresses is not allowed");
    }

    return redirectUri;
  }

  private static bool IsRedirect(HttpStatusCode statusCode) =>
    (int)statusCode is >= 300 and < 400;

  private static bool SameOrigin(Uri left, Uri right) =>
    string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
    string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
    left.Port == right.Port;

  private static void AddConditionalHeaders(HttpRequestMessage request, PlaylistSourceFile? cachedSource)
  {
    if (cachedSource is null)
    {
      return;
    }

    if (!string.IsNullOrWhiteSpace(cachedSource.ETag) && EntityTagHeaderValue.TryParse(cachedSource.ETag, out var entityTag))
    {
      request.Headers.IfNoneMatch.Add(entityTag);
    }

    if (cachedSource.LastModifiedUtc.HasValue)
    {
      request.Headers.IfModifiedSince = cachedSource.LastModifiedUtc;
    }
  }

  private async Task<DownloadedPlaylistFile> CopyToFileAsync(
    HttpResponseMessage response,
    string temporaryPath,
    CancellationToken cancellationToken)
  {
    var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
    try
    {
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
      await using var destination = new FileStream(
        temporaryPath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
      PlaylistFileCache.TrySetPrivateFile(temporaryPath);

      long totalBytes = 0;
      while (true)
      {
        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (read == 0) break;

        totalBytes += read;
        _cache.EnsureWithinSourceLimit(totalBytes);
        hash.AppendData(buffer, 0, read);
        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
      }

      await destination.FlushAsync(cancellationToken);
      PlaylistFileCache.TrySetPrivateFile(temporaryPath);
      return new DownloadedPlaylistFile(
        temporaryPath,
        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
        totalBytes,
        response.Headers.ETag?.ToString(),
        response.Content.Headers.LastModified);
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }
}

public sealed record SmallTextFetchResult(bool Success, string? Text, int? StatusCode, string? Error)
{
  public static SmallTextFetchResult Succeeded(string text, int statusCode) => new(true, text, statusCode, null);

  public static SmallTextFetchResult Failed(string error, int? statusCode = null) => new(false, null, statusCode, error);
}

public sealed record PlaylistFetchResult(bool NotModified, DownloadedPlaylistFile? DownloadedFile, string? ETag, DateTimeOffset? LastModifiedUtc)
{
  public static PlaylistFetchResult Downloaded(DownloadedPlaylistFile file) =>
    new(false, file, file.ETag, file.LastModifiedUtc);

  public static PlaylistFetchResult Unchanged(string? eTag, DateTimeOffset? lastModifiedUtc) =>
    new(true, null, eTag, lastModifiedUtc);
}
