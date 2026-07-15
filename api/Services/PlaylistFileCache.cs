using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Api.Services;

/// <summary>
/// Stores only playlist metadata in process memory. Source and generated files live in a
/// private temporary directory and are evicted by TTL and a disk budget.
/// </summary>
public sealed class PlaylistFileCache
{
  private readonly object _sync = new();
  private readonly Dictionary<string, PlaylistSourceFile> _sources = new(StringComparer.Ordinal);
  private readonly Dictionary<string, PlaylistSession> _sessions = new(StringComparer.Ordinal);
  private readonly Dictionary<string, PlaylistOutputFile> _outputs = new(StringComparer.Ordinal);
  private readonly Dictionary<string, string> _outputVariantKeys = new(StringComparer.Ordinal);
  // Failed deletes must remain part of capacity accounting. Otherwise a transient filesystem
  // failure turns the configured disk budget into a soft limit for the life of the process.
  private readonly Dictionary<string, PendingDeleteFile> _pendingDeletes = new(StringComparer.Ordinal);
  private readonly PlaylistCacheOptions _options;
  private readonly ILogger<PlaylistFileCache> _logger;
  private readonly Func<string, bool> _deleteFile;
  private readonly string _directory;
  private readonly TimeSpan _entryTtl;
  private readonly TimeSpan _sourceTtl;
  private readonly TimeSpan _revalidationInterval;
  private long _reservedBytes;

  public PlaylistFileCache(IOptions<PlaylistCacheOptions> options, ILogger<PlaylistFileCache> logger)
    : this(options, logger, TryDelete)
  {
  }

  internal PlaylistFileCache(
    IOptions<PlaylistCacheOptions> options,
    ILogger<PlaylistFileCache> logger,
    Func<string, bool> deleteFile)
  {
    _options = options.Value;
    _logger = logger;
    _deleteFile = deleteFile ?? throw new ArgumentNullException(nameof(deleteFile));
    _entryTtl = TimeSpan.FromMinutes(Math.Max(1, _options.EntryTtlMinutes));
    _sourceTtl = TimeSpan.FromMinutes(Math.Max(1, _options.SourceTtlMinutes));
    _revalidationInterval = TimeSpan.FromMinutes(Math.Max(0, _options.RevalidationIntervalMinutes));
    _directory = string.IsNullOrWhiteSpace(_options.Directory)
      ? Path.Combine(Path.GetTempPath(), "strim-playlists")
      : Path.GetFullPath(_options.Directory);

    Directory.CreateDirectory(_directory);
    TrySetPrivateDirectory(_directory);
    CleanupStaleCacheFiles();

    if (_options.MaxDiskBytes > 0 && _options.MaxSourceBytes > _options.MaxDiskBytes)
    {
      _logger.LogWarning(
        "PlaylistCache MaxDiskBytes ({MaxDiskBytes}) is lower than MaxSourceBytes ({MaxSourceBytes}); unknown-length sources may be rejected before download to preserve the disk budget",
        _options.MaxDiskBytes,
        _options.MaxSourceBytes);
    }
  }

  public string CreateTemporaryPath(string prefix) =>
    Path.Combine(_directory, $"{prefix}-{Guid.NewGuid():N}.partial");

  public int MaxLineLengthChars => Math.Max(1, _options.MaxLineLengthChars);

  public int MaxGroupCount => Math.Max(1, _options.MaxGroupCount);

  public int MaxGroupTitleLengthChars => Math.Max(1, _options.MaxGroupTitleLengthChars);

  public long MaxGroupMetadataBytes => Math.Max(1, _options.MaxGroupMetadataBytes);

  public long MaxGeneratedBytes => _options.MaxSourceBytes > 0
    ? _options.MaxSourceBytes
    : Math.Max(0, _options.MaxDiskBytes);

  public string CreateOutputTemporaryPath() => CreateTemporaryPath("output");

  /// <summary>
  /// Reserves enough cache capacity before a source body is written. Unknown-length responses
  /// reserve their configured maximum so a partial download cannot silently overrun the disk.
  /// </summary>
  public PlaylistDiskReservation ReserveSourceCapacity(long? advertisedLengthBytes)
  {
    if (advertisedLengthBytes.HasValue)
    {
      EnsureWithinSourceLimit(advertisedLengthBytes.Value);
    }

    // Reserve the actual maximum, rather than trusting Content-Length. An upstream can omit or
    // lie about that header; this keeps MaxDiskBytes a real upper bound during streaming.
    var requestedBytes = _options.MaxSourceBytes > 0
      ? _options.MaxSourceBytes
      : advertisedLengthBytes.GetValueOrDefault(_options.MaxDiskBytes);

    return ReserveCapacity(requestedBytes, "source download", protectedPaths: null);
  }

  /// <summary>
  /// A generated M3U can approach the input size. Reserve the temporary output before parsing
  /// so disk capacity remains a hard bound even while an output is being written.
  /// </summary>
  public PlaylistDiskReservation ReserveOutputCapacity(PlaylistSourceFile source)
  {
    var requestedBytes = MaxGeneratedBytes > 0
      ? MaxGeneratedBytes
      : checked(source.LengthBytes + 128 * 1024L);
    return ReserveCapacity(requestedBytes, "generated playlist", new[] { source.FilePath });
  }

  public async Task<DownloadedPlaylistFile> WriteRawTextAsync(string text, CancellationToken cancellationToken)
  {
    var path = CreateTemporaryPath("raw");
    var expectedBytes = (long)Encoding.UTF8.GetByteCount(text);
    EnsureWithinSourceLimit(expectedBytes);
    var reservation = ReserveCapacity(expectedBytes, "raw playlist", protectedPaths: null);
    long length = 0;
    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

    try
    {
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      await using var stream = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
      TrySetPrivateFile(path);

      const int charsPerChunk = 16 * 1024;
      for (var offset = 0; offset < text.Length; offset += charsPerChunk)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var charCount = Math.Min(charsPerChunk, text.Length - offset);
        var bytesWritten = Encoding.UTF8.GetBytes(text, offset, charCount, buffer, 0);
        await stream.WriteAsync(buffer.AsMemory(0, bytesWritten), cancellationToken);
        hash.AppendData(buffer, 0, bytesWritten);
        length += bytesWritten;

        EnsureWithinSourceLimit(length);
      }

      await stream.FlushAsync(cancellationToken);
      TrySetPrivateFile(path);
      return new DownloadedPlaylistFile(path, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length, null, null, reservation);
    }
    catch
    {
      DeleteTemporaryFile(path, reservation.ReservedBytes);
      reservation.Dispose();
      throw;
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  public PlaylistSourceFile StoreDownloaded(string sourceUrl, DownloadedPlaylistFile downloaded)
  {
    var now = DateTimeOffset.UtcNow;
    var sourceKey = SourceKeyForUrl(sourceUrl);

    try
    {
      lock (_sync)
      {
        if (_sources.TryGetValue(sourceKey, out var existing) &&
            string.Equals(existing.ContentHash, downloaded.ContentHash, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(existing.FilePath))
        {
          DeleteOrTrackNoLock(downloaded.TemporaryPath, downloaded.LengthBytes);
          existing.ETag = downloaded.ETag ?? existing.ETag;
          existing.LastModifiedUtc = downloaded.LastModifiedUtc ?? existing.LastModifiedUtc;
          existing.LengthBytes = downloaded.LengthBytes;
          existing.ExpiresAtUtc = now.Add(_sourceTtl);
          existing.NextRevalidationUtc = now.Add(_revalidationInterval);
          existing.LastAccessedUtc = now;
          return existing;
        }

        var finalPath = Path.Combine(_directory, $"source-{sourceKey}-{downloaded.ContentHash[..Math.Min(16, downloaded.ContentHash.Length)]}.m3u");
        try
        {
          File.Move(downloaded.TemporaryPath, finalPath, overwrite: true);
          TrySetPrivateFile(finalPath);
        }
        catch
        {
          DeleteOrTrackNoLock(downloaded.TemporaryPath, downloaded.LengthBytes);
          throw;
        }

        var source = new PlaylistSourceFile(
          sourceKey,
          sourceUrl,
          finalPath,
          downloaded.ContentHash,
          downloaded.LengthBytes,
          downloaded.ETag,
          downloaded.LastModifiedUtc,
          now.Add(_sourceTtl),
          now.Add(_revalidationInterval),
          now);
        _sources[sourceKey] = source;
        _pendingDeletes.Remove(finalPath);

        if (existing is not null && !string.Equals(existing.FilePath, finalPath, StringComparison.Ordinal))
        {
          DeleteOrTrackNoLock(existing.FilePath, existing.LengthBytes);
        }

        return source;
      }
    }
    finally
    {
      // A source reservation is held only while its temporary file exists. Once it is moved
      // into tracked cache storage (or discarded), normal budget accounting takes over.
      downloaded.Dispose();
    }
  }

  public PlaylistSourceFile StoreRawText(string cacheIdentity, DownloadedPlaylistFile downloaded)
  {
    // Raw input must not collide with a URL-based source key. The identity is an opaque session id.
    return StoreDownloaded($"raw://{cacheIdentity}", downloaded);
  }

  public void TouchNotModified(PlaylistSourceFile source, string? eTag, DateTimeOffset? lastModifiedUtc)
  {
    lock (_sync)
    {
      source.ETag = eTag ?? source.ETag;
      source.LastModifiedUtc = lastModifiedUtc ?? source.LastModifiedUtc;
      source.ExpiresAtUtc = DateTimeOffset.UtcNow.Add(_sourceTtl);
      source.NextRevalidationUtc = DateTimeOffset.UtcNow.Add(_revalidationInterval);
      source.LastAccessedUtc = DateTimeOffset.UtcNow;
    }
  }

  public PlaylistSourceFile? TryGetSource(string sourceUrl)
  {
    var sourceKey = SourceKeyForUrl(sourceUrl);
    lock (_sync)
    {
      if (!_sources.TryGetValue(sourceKey, out var source) || !IsAvailable(source))
      {
        return null;
      }

      source.LastAccessedUtc = DateTimeOffset.UtcNow;
      return source;
    }
  }

  public PlaylistSourceFile? TryGetFreshSource(string sourceUrl)
  {
    var sourceKey = SourceKeyForUrl(sourceUrl);
    lock (_sync)
    {
      if (!_sources.TryGetValue(sourceKey, out var source) ||
          !IsAvailable(source) ||
          source.NextRevalidationUtc <= DateTimeOffset.UtcNow)
      {
        return null;
      }

      source.LastAccessedUtc = DateTimeOffset.UtcNow;
      return source;
    }
  }

  public string CreateSession(PlaylistSourceFile source)
  {
    var key = $"pl-{Guid.NewGuid():N}";
    lock (_sync)
    {
      _sessions[key] = new PlaylistSession(source.SourceKey, DateTimeOffset.UtcNow.Add(_entryTtl));
      return key;
    }
  }

  public PlaylistAnalysisResult? TryGetAnalysis(PlaylistSourceFile source)
  {
    lock (_sync)
    {
      return source.Analysis;
    }
  }

  public void StoreAnalysis(PlaylistSourceFile source, PlaylistAnalysisResult analysis)
  {
    lock (_sync)
    {
      source.Analysis = analysis;
    }
  }

  public PlaylistSourceFile? TryGetSessionSource(string sessionKey)
  {
    lock (_sync)
    {
      if (!_sessions.TryGetValue(sessionKey, out var session) || session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
      {
        _sessions.Remove(sessionKey);
        return null;
      }

      if (!_sources.TryGetValue(session.SourceKey, out var source) || !IsAvailable(source))
      {
        return null;
      }

      source.LastAccessedUtc = DateTimeOffset.UtcNow;
      return source;
    }
  }

  public PlaylistOutputFile? TryGetOutput(string sourceHash, IReadOnlyCollection<string> disabledGroups)
  {
    var variantKey = OutputVariantKey(sourceHash, disabledGroups);
    lock (_sync)
    {
      if (!_outputVariantKeys.TryGetValue(variantKey, out var outputKey) ||
          !_outputs.TryGetValue(outputKey, out var output) ||
          !IsAvailable(output))
      {
        if (!string.IsNullOrWhiteSpace(outputKey))
        {
          RemoveOutputNoLock(outputKey);
        }
        return null;
      }

      output.LastAccessedUtc = DateTimeOffset.UtcNow;
      return output;
    }
  }

  public PlaylistOutputFile StoreOutput(
    PlaylistSourceFile source,
    IReadOnlyCollection<string> disabledGroups,
    string temporaryPath,
    PlaylistGenerationResult result)
  {
    var now = DateTimeOffset.UtcNow;
    var outputKey = $"out-{Guid.NewGuid():N}";
    var variantKey = OutputVariantKey(source.ContentHash, disabledGroups);
    var finalPath = Path.Combine(_directory, $"output-{outputKey[4..]}.m3u");

    lock (_sync)
    {
      try
      {
        File.Move(temporaryPath, finalPath, overwrite: true);
        TrySetPrivateFile(finalPath);
      }
      catch
      {
        DeleteOrTrackNoLock(temporaryPath, result.OutputBytes);
        throw;
      }

      if (_outputVariantKeys.TryGetValue(variantKey, out var oldOutputKey))
      {
        RemoveOutputNoLock(oldOutputKey);
      }

      var output = new PlaylistOutputFile(
        outputKey,
        variantKey,
        finalPath,
        result.OutputBytes,
        result.TotalChannels,
        result.KeptChannels,
        now.Add(_entryTtl),
        now);
      _outputs[outputKey] = output;
      _outputVariantKeys[variantKey] = outputKey;
      _pendingDeletes.Remove(finalPath);
      return output;
    }
  }

  public PlaylistFileLease? TryLeaseOutput(string outputKey, string? downloadFileName = null)
  {
    lock (_sync)
    {
      if (!_outputs.TryGetValue(outputKey, out var output) || !IsAvailable(output))
      {
        RemoveOutputNoLock(outputKey);
        return null;
      }

      output.ActiveLeases++;
      output.LastAccessedUtc = DateTimeOffset.UtcNow;
      return new PlaylistFileLease(
        output.FilePath,
        SanitizeFileName(downloadFileName ?? "playlist-filtered.m3u"),
        () => ReleaseOutput(outputKey));
    }
  }

  public PlaylistFileLease LeaseTransientFile(
    string path,
    string? fileName = null,
    IDisposable? associatedResource = null,
    long knownLengthBytes = 0) =>
    new(path, fileName, () =>
    {
      DeleteTemporaryFile(path, knownLengthBytes);
      associatedResource?.Dispose();
    });

  /// <summary>
  /// Deletes a cache-owned temporary file. If the filesystem rejects the delete, retain a
  /// conservative capacity debt and retry it from later cleanup passes.
  /// </summary>
  public void DeleteTemporaryFile(string path, long knownLengthBytes = 0)
  {
    lock (_sync)
    {
      DeleteOrTrackNoLock(path, knownLengthBytes);
    }
  }

  public void Cleanup()
  {
    lock (_sync)
    {
      EnforceBudgetNoLock();
    }
  }

  public void EnsureWithinSourceLimit(long bytes)
  {
    if (_options.MaxSourceBytes > 0 && bytes > _options.MaxSourceBytes)
    {
      throw new PlaylistSizeExceededException(_options.MaxSourceBytes);
    }
  }

  private void ReleaseOutput(string outputKey)
  {
    lock (_sync)
    {
      if (_outputs.TryGetValue(outputKey, out var output))
      {
        output.ActiveLeases = Math.Max(0, output.ActiveLeases - 1);
        var isCurrentVariant = _outputVariantKeys.TryGetValue(output.VariantKey, out var currentOutputKey) &&
          string.Equals(currentOutputKey, outputKey, StringComparison.Ordinal);
        if (output.ActiveLeases == 0 && (output.ExpiresAtUtc <= DateTimeOffset.UtcNow || !isCurrentVariant))
        {
          RemoveOutputNoLock(outputKey);
        }
      }
    }
  }

  private bool IsAvailable(PlaylistSourceFile source) =>
    source.ExpiresAtUtc > DateTimeOffset.UtcNow && File.Exists(source.FilePath);

  private bool IsAvailable(PlaylistOutputFile output) =>
    output.ExpiresAtUtc > DateTimeOffset.UtcNow && File.Exists(output.FilePath);

  private PlaylistDiskReservation ReserveCapacity(
    long requestedBytes,
    string operation,
    IEnumerable<string>? protectedPaths)
  {
    if (_options.MaxDiskBytes <= 0 || requestedBytes <= 0)
    {
      return PlaylistDiskReservation.Empty;
    }

    lock (_sync)
    {
      if (requestedBytes > _options.MaxDiskBytes)
      {
        throw new PlaylistDiskCapacityExceededException(operation, requestedBytes, _options.MaxDiskBytes);
      }

      var allowedTrackedBytes = _options.MaxDiskBytes - _reservedBytes - requestedBytes;
      if (allowedTrackedBytes < 0)
      {
        throw new PlaylistDiskCapacityExceededException(operation, requestedBytes, _options.MaxDiskBytes);
      }

      var protectedPathSet = ToPathSet(protectedPaths);
      var remainingBytes = EvictToTrackedBudgetNoLock(allowedTrackedBytes, protectedPathSet);
      if (remainingBytes > allowedTrackedBytes)
      {
        throw new PlaylistDiskCapacityExceededException(operation, requestedBytes, _options.MaxDiskBytes);
      }

      _reservedBytes += requestedBytes;
      return new PlaylistDiskReservation(requestedBytes, () => ReleaseReservation(requestedBytes));
    }
  }

  private void ReleaseReservation(long bytes)
  {
    lock (_sync)
    {
      _reservedBytes = Math.Max(0, _reservedBytes - bytes);
      EnforceBudgetNoLock();
    }
  }

  private void EnforceBudgetNoLock(IEnumerable<string>? protectedPaths = null)
  {
    var maxBytes = _options.MaxDiskBytes;
    if (maxBytes <= 0)
    {
      RemoveExpiredEntriesNoLock(ToPathSet(protectedPaths));
      return;
    }

    var allowedTrackedBytes = Math.Max(0, maxBytes - _reservedBytes);
    _ = EvictToTrackedBudgetNoLock(allowedTrackedBytes, ToPathSet(protectedPaths));
  }

  private long EvictToTrackedBudgetNoLock(long maxTrackedBytes, ISet<string> protectedPaths)
  {
    RemoveExpiredEntriesNoLock(protectedPaths);

    long usedBytes = GetTrackedBytesNoLock();
    if (usedBytes <= maxTrackedBytes)
    {
      return usedBytes;
    }

    foreach (var output in _outputs.Values
      .Where(x => x.ActiveLeases == 0 && !protectedPaths.Contains(x.FilePath))
      .OrderBy(x => x.LastAccessedUtc)
      .ToList())
    {
      var length = GetTrackedLength(output.FilePath, output.LengthBytes);
      if (RemoveOutputNoLock(output.OutputKey))
      {
        usedBytes -= length;
      }
      if (usedBytes <= maxTrackedBytes) return usedBytes;
    }

    foreach (var source in _sources.Values
      .Where(x => !protectedPaths.Contains(x.FilePath))
      .OrderBy(x => x.LastAccessedUtc)
      .ToList())
    {
      var length = GetTrackedLength(source.FilePath, source.LengthBytes);
      if (RemoveSourceNoLock(source.SourceKey))
      {
        usedBytes -= length;
      }
      if (usedBytes <= maxTrackedBytes) return usedBytes;
    }

    _logger.LogWarning(
      "Playlist cache cannot free enough disk capacity ({UsedBytes} tracked bytes; target {TargetBytes}) because files are active, protected, or awaiting a successful delete",
      usedBytes,
      maxTrackedBytes);
    return usedBytes;
  }

  private void RemoveExpiredEntriesNoLock(ISet<string> protectedPaths)
  {
    RetryPendingDeletesNoLock();
    var now = DateTimeOffset.UtcNow;
    foreach (var session in _sessions.Where(x => x.Value.ExpiresAtUtc <= now).Select(x => x.Key).ToList())
    {
      _sessions.Remove(session);
    }

    foreach (var output in _outputs.Values
      .Where(x => x.ExpiresAtUtc <= now && x.ActiveLeases == 0 && !protectedPaths.Contains(x.FilePath))
      .OrderBy(x => x.LastAccessedUtc)
      .ToList())
    {
      RemoveOutputNoLock(output.OutputKey);
    }

    foreach (var source in _sources.Values
      .Where(x => x.ExpiresAtUtc <= now && !protectedPaths.Contains(x.FilePath))
      .OrderBy(x => x.LastAccessedUtc)
      .ToList())
    {
      RemoveSourceNoLock(source.SourceKey);
    }
  }

  private static ISet<string> ToPathSet(IEnumerable<string>? paths) =>
    paths is null
      ? new HashSet<string>(StringComparer.Ordinal)
      : new HashSet<string>(paths, StringComparer.Ordinal);

  private long GetTrackedBytesNoLock() =>
    _sources.Values.Sum(x => GetTrackedLength(x.FilePath, x.LengthBytes)) +
    _outputs.Values.Sum(x => GetTrackedLength(x.FilePath, x.LengthBytes)) +
    _pendingDeletes.Values.Sum(x => GetTrackedLength(x.FilePath, x.LengthBytes));

  private bool RemoveSourceNoLock(string sourceKey)
  {
    if (!_sources.Remove(sourceKey, out var source))
    {
      return false;
    }

    return DeleteOrTrackNoLock(source.FilePath, source.LengthBytes);
  }

  private bool RemoveOutputNoLock(string outputKey)
  {
    if (!_outputs.TryGetValue(outputKey, out var output) || output.ActiveLeases > 0)
    {
      return false;
    }

    _outputs.Remove(outputKey);

    if (_outputVariantKeys.TryGetValue(output.VariantKey, out var mapped) && mapped == outputKey)
    {
      _outputVariantKeys.Remove(output.VariantKey);
    }

    return DeleteOrTrackNoLock(output.FilePath, output.LengthBytes);
  }

  private bool DeleteOrTrackNoLock(string path, long knownLengthBytes)
  {
    if (_deleteFile(path))
    {
      _pendingDeletes.Remove(path);
      return true;
    }

    var isNewFailure = !_pendingDeletes.ContainsKey(path);
    _pendingDeletes[path] = new PendingDeleteFile(path, GetTrackedLength(path, knownLengthBytes));
    if (isNewFailure)
    {
      _logger.LogWarning("Unable to delete playlist cache file {CacheFile}; retaining its disk capacity until cleanup succeeds", path);
    }
    return false;
  }

  private void RetryPendingDeletesNoLock()
  {
    foreach (var pending in _pendingDeletes.Values.ToList())
    {
      DeleteOrTrackNoLock(pending.FilePath, pending.LengthBytes);
    }
  }

  private void CleanupStaleCacheFiles()
  {
    try
    {
      // Cache metadata is process-local, so source/output files from a previous process cannot
      // be safely reused. Restrict deletion to names this service itself generates.
      lock (_sync)
      {
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
          var name = Path.GetFileName(file);
          if (name.EndsWith(".partial", StringComparison.Ordinal) ||
              name.StartsWith("source-", StringComparison.Ordinal) ||
              name.StartsWith("output-", StringComparison.Ordinal) ||
              name.StartsWith("raw-", StringComparison.Ordinal))
          {
            // A stat failure at startup must not make a left-over cache file invisible to the
            // new process. The whole cache budget is the safe fallback until retry succeeds.
            DeleteOrTrackNoLock(file, _options.MaxDiskBytes);
          }
        }
      }
    }
    catch (Exception ex)
    {
      _logger.LogDebug(ex, "Unable to clean stale playlist cache files at startup");
    }
  }

  private static string SourceKeyForUrl(string sourceUrl) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl))).ToLowerInvariant();

  private static string OutputVariantKey(string sourceHash, IReadOnlyCollection<string> disabledGroups)
  {
    var normalizedGroups = disabledGroups
      .Where(x => !string.IsNullOrWhiteSpace(x))
      .Select(x => x.Trim().ToUpperInvariant())
      .Distinct(StringComparer.Ordinal)
      .OrderBy(x => x, StringComparer.Ordinal);
    var canonical = string.Join('\n', normalizedGroups);
    var groupsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    return $"{sourceHash}:{groupsHash}";
  }

  public static string SanitizeFileName(string? fileName)
  {
    const string fallback = "playlist-filtered.m3u";
    const int maxLength = 180;
    if (string.IsNullOrWhiteSpace(fileName)) return fallback;

    // This whitelist is deliberately independent of OS filename rules. In particular, Linux
    // permits CR/LF in a filename, but it must never reach Content-Disposition.
    var safe = new StringBuilder(Math.Min(fileName.Length, maxLength));
    foreach (var character in fileName)
    {
      if (safe.Length >= maxLength) break;
      safe.Append(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' or '-' or '_' or '.'
        ? character
        : '_');
    }

    var value = safe.ToString().Trim(' ', '.');
    if (string.IsNullOrWhiteSpace(value)) return fallback;
    if (!value.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
    {
      value = value.Length > maxLength - 4 ? value[..(maxLength - 4)] : value;
      value = $"{value}.m3u";
    }
    return value.Length <= maxLength ? value : value[..maxLength];
  }

  private static long GetTrackedLength(string path, long knownLengthBytes)
  {
    try { return new FileInfo(path).Length; }
    catch (FileNotFoundException) { return 0; }
    catch (DirectoryNotFoundException) { return 0; }
    catch (UnauthorizedAccessException) { return Math.Max(0, knownLengthBytes); }
    catch (IOException) { return Math.Max(0, knownLengthBytes); }
    catch { return Math.Max(0, knownLengthBytes); }
  }

  internal static bool TryDelete(string path)
  {
    try
    {
      // File.Delete already treats a missing file as success. Do not pre-check File.Exists:
      // it reports false for some access failures and would hide an existing orphan.
      File.Delete(path);
      return true;
    }
    catch
    {
      return false;
    }
  }

  private static void TrySetPrivateDirectory(string path)
  {
    if (OperatingSystem.IsWindows()) return;
    try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    catch (PlatformNotSupportedException) { }
    catch (UnauthorizedAccessException) { }
  }

  internal static void TrySetPrivateFile(string path)
  {
    if (OperatingSystem.IsWindows()) return;
    try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    catch (PlatformNotSupportedException) { }
    catch (UnauthorizedAccessException) { }
  }
}

public sealed class PlaylistSourceFile
{
  internal PlaylistSourceFile(
    string sourceKey,
    string sourceUrl,
    string filePath,
    string contentHash,
    long lengthBytes,
    string? eTag,
    DateTimeOffset? lastModifiedUtc,
    DateTimeOffset expiresAtUtc,
    DateTimeOffset nextRevalidationUtc,
    DateTimeOffset lastAccessedUtc)
  {
    SourceKey = sourceKey;
    SourceUrl = sourceUrl;
    FilePath = filePath;
    ContentHash = contentHash;
    LengthBytes = lengthBytes;
    ETag = eTag;
    LastModifiedUtc = lastModifiedUtc;
    ExpiresAtUtc = expiresAtUtc;
    NextRevalidationUtc = nextRevalidationUtc;
    LastAccessedUtc = lastAccessedUtc;
  }

  public string SourceKey { get; }
  public string SourceUrl { get; }
  public string FilePath { get; }
  public string ContentHash { get; }
  public long LengthBytes { get; internal set; }
  public string? ETag { get; internal set; }
  public DateTimeOffset? LastModifiedUtc { get; internal set; }
  internal PlaylistAnalysisResult? Analysis { get; set; }
  internal DateTimeOffset ExpiresAtUtc { get; set; }
  internal DateTimeOffset NextRevalidationUtc { get; set; }
  internal DateTimeOffset LastAccessedUtc { get; set; }
}

public sealed class PlaylistOutputFile
{
  internal PlaylistOutputFile(
    string outputKey,
    string variantKey,
    string filePath,
    long lengthBytes,
    int totalChannels,
    int keptChannels,
    DateTimeOffset expiresAtUtc,
    DateTimeOffset lastAccessedUtc)
  {
    OutputKey = outputKey;
    VariantKey = variantKey;
    FilePath = filePath;
    LengthBytes = lengthBytes;
    TotalChannels = totalChannels;
    KeptChannels = keptChannels;
    ExpiresAtUtc = expiresAtUtc;
    LastAccessedUtc = lastAccessedUtc;
  }

  public string OutputKey { get; }
  internal string VariantKey { get; }
  internal string FilePath { get; }
  internal long LengthBytes { get; }
  public int TotalChannels { get; }
  public int KeptChannels { get; }
  internal DateTimeOffset ExpiresAtUtc { get; }
  internal DateTimeOffset LastAccessedUtc { get; set; }
  internal int ActiveLeases { get; set; }
}

public sealed record DownloadedPlaylistFile(
  string TemporaryPath,
  string ContentHash,
  long LengthBytes,
  string? ETag,
  DateTimeOffset? LastModifiedUtc,
  PlaylistDiskReservation? DiskReservation = null) : IDisposable
{
  public void Dispose() => DiskReservation?.Dispose();
}

public sealed record PlaylistGenerationResult(int TotalChannels, int KeptChannels, long OutputBytes);

public sealed class PlaylistFileLease : IDisposable, IAsyncDisposable
{
  private Action? _release;

  internal PlaylistFileLease(string filePath, string? fileName, Action release)
  {
    FilePath = filePath;
    FileName = fileName;
    _release = release;
  }

  public string FilePath { get; }
  public string? FileName { get; }

  public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();

  public ValueTask DisposeAsync()
  {
    Dispose();
    return ValueTask.CompletedTask;
  }
}

public sealed class PlaylistSizeExceededException : InvalidOperationException
{
  public PlaylistSizeExceededException(long maxBytes)
    : base($"Playlist source exceeds the configured {maxBytes / (1024 * 1024)} MiB download limit. Increase PlaylistCache__MaxSourceBytes to support this source.")
  {
    MaxBytes = maxBytes;
  }

  public long MaxBytes { get; }
}

public sealed class PlaylistDiskCapacityExceededException : InvalidOperationException
{
  public PlaylistDiskCapacityExceededException(string operation, long requestedBytes, long maxDiskBytes)
    : base($"Insufficient PlaylistCache disk capacity for {operation}: it needs {requestedBytes / (1024 * 1024)} MiB of scratch space within the configured {maxDiskBytes / (1024 * 1024)} MiB budget. Increase PlaylistCache__MaxDiskBytes or lower PlaylistCache__MaxSourceBytes.")
  {
  }
}

/// <summary>One-time release handle for capacity reserved before a temporary file is written.</summary>
public sealed class PlaylistDiskReservation : IDisposable
{
  private Action? _release;

  internal PlaylistDiskReservation(long reservedBytes, Action release)
  {
    ReservedBytes = reservedBytes;
    _release = release;
  }

  internal static PlaylistDiskReservation Empty { get; } = new(0, () => { });

  internal long ReservedBytes { get; }

  public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal sealed record PlaylistSession(string SourceKey, DateTimeOffset ExpiresAtUtc);

internal sealed record PendingDeleteFile(string FilePath, long LengthBytes);
