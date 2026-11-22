using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Api.Services;

/// <summary>
/// Stores only playlist metadata in process memory. Source and generated files live in a
/// private temporary directory and are evicted by TTL and a disk budget.
/// </summary>
public sealed class PlaylistFileCache
{
  /// <summary>
  /// Upper bound of a source sidecar, shared by write and read. It sits well above the response
  /// headers the HTTP client accepts (64 KiB by default) even after JSON escaping, so any validator
  /// the upstream sends is stored in full rather than truncated.
  /// </summary>
  internal const int MaxSourceSidecarBytes = 1024 * 1024;

  private readonly object _sync = new();
  private readonly Dictionary<string, PlaylistSourceFile> _sources = new(StringComparer.Ordinal);
  private readonly Dictionary<string, PlaylistSession> _sessions = new(StringComparer.Ordinal);
  // Library keys (lib-…) live apart from the editor's sessions, so no editor route ever resolves one.
  private readonly Dictionary<string, LibrarySession> _librarySessions = new(StringComparer.Ordinal);
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
  private readonly TimeProvider _time;
  // Pinned last-good output per player link, independent of the output TTL. Superseded or
  // forgotten copies that are still leased stay accounted until their last lease is released.
  // Keyed by the hashed playlist key, so copies restored after a restart (whose sidecar holds
  // only that hash) are found again by playlist id.
  private readonly Dictionary<string, LastGoodFile> _lastGood = new(StringComparer.Ordinal);
  private readonly TimeSpan _lastGoodTtl;
  private readonly List<LastGoodFile> _retiredLastGood = new();
  private long _reservedBytes;

  public PlaylistFileCache(IOptions<PlaylistCacheOptions> options, ILogger<PlaylistFileCache> logger)
    : this(options, logger, TryDelete)
  {
  }

  internal PlaylistFileCache(
    IOptions<PlaylistCacheOptions> options,
    ILogger<PlaylistFileCache> logger,
    Func<string, bool> deleteFile)
    : this(options, logger, deleteFile, TimeProvider.System)
  {
  }

  internal PlaylistFileCache(
    IOptions<PlaylistCacheOptions> options,
    ILogger<PlaylistFileCache> logger,
    Func<string, bool> deleteFile,
    TimeProvider time)
  {
    _options = options.Value;
    _time = time ?? throw new ArgumentNullException(nameof(time));
    _logger = logger;
    _deleteFile = deleteFile ?? throw new ArgumentNullException(nameof(deleteFile));
    _entryTtl = TimeSpan.FromMinutes(Math.Max(1, _options.EntryTtlMinutes));
    _sourceTtl = TimeSpan.FromMinutes(Math.Max(1, _options.SourceTtlMinutes));
    _revalidationInterval = TimeSpan.FromMinutes(Math.Max(0, _options.RevalidationIntervalMinutes));
    _lastGoodTtl = TimeSpan.FromMinutes(Math.Max(1, _options.LastGoodTtlMinutes));
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

  public long MaxSearchIndexBytes => Math.Max(0, _options.MaxSearchIndexBytes);

  public long MaxReportBytes => Math.Max(0, _options.MaxReportBytes);

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

  public PlaylistSourceFile StoreDownloaded(string sourceUrl, DownloadedPlaylistFile downloaded) =>
    StoreSource(sourceUrl, downloaded, persist: true);

  private PlaylistSourceFile StoreSource(string sourceUrl, DownloadedPlaylistFile downloaded, bool persist)
  {
    var now = _time.GetUtcNow();
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
          if (persist) WriteSourceSidecarNoLock(existing, now);
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
          DeleteSourceFilesNoLock(existing);
        }

        if (persist)
        {
          WriteSourceSidecarNoLock(source, now);
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
    // It gets no sidecar, so it is dropped at restart like the session it belongs to.
    return StoreSource($"raw://{cacheIdentity}", downloaded, persist: false);
  }

  public void TouchNotModified(PlaylistSourceFile source, string? eTag, DateTimeOffset? lastModifiedUtc)
  {
    lock (_sync)
    {
      source.ETag = eTag ?? source.ETag;
      source.LastModifiedUtc = lastModifiedUtc ?? source.LastModifiedUtc;
      source.ExpiresAtUtc = _time.GetUtcNow().Add(_sourceTtl);
      source.NextRevalidationUtc = _time.GetUtcNow().Add(_revalidationInterval);
      source.LastAccessedUtc = _time.GetUtcNow();
      if (_sources.TryGetValue(source.SourceKey, out var current) && ReferenceEquals(current, source))
      {
        WriteSourceSidecarNoLock(source, source.LastAccessedUtc);
      }
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

      source.LastAccessedUtc = _time.GetUtcNow();
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
          source.NextRevalidationUtc <= _time.GetUtcNow())
      {
        return null;
      }

      source.LastAccessedUtc = _time.GetUtcNow();
      return source;
    }
  }

  public string CreateSession(PlaylistSourceFile source)
  {
    var key = $"pl-{Guid.NewGuid():N}";
    lock (_sync)
    {
      _sessions[key] = new PlaylistSession(source.SourceKey, _time.GetUtcNow().Add(_entryTtl));
      return key;
    }
  }

  /// <summary>A Library key bound to one playlist and one user; the editor's routes do not accept it.</summary>
  public string CreateLibrarySession(PlaylistSourceFile source, Guid playlistId, string userId)
  {
    var key = $"lib-{Guid.NewGuid():N}";
    lock (_sync)
    {
      _librarySessions[key] = new LibrarySession(source.SourceKey, playlistId, userId, _time.GetUtcNow().Add(_entryTtl));
      return key;
    }
  }

  /// <summary>The source of a Library key, only for the playlist and user it was issued to.</summary>
  public PlaylistSourceFile? TryGetLibrarySource(string libraryKey, Guid playlistId, string userId)
  {
    lock (_sync)
    {
      if (!_librarySessions.TryGetValue(libraryKey, out var session) || session.ExpiresAtUtc <= _time.GetUtcNow())
      {
        _librarySessions.Remove(libraryKey);
        return null;
      }

      if (session.PlaylistId != playlistId || !string.Equals(session.UserId, userId, StringComparison.Ordinal) ||
          !_sources.TryGetValue(session.SourceKey, out var source) || !IsAvailable(source))
      {
        return null;
      }

      source.LastAccessedUtc = _time.GetUtcNow();
      return source;
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
      if (!_sessions.TryGetValue(sessionKey, out var session) || session.ExpiresAtUtc <= _time.GetUtcNow())
      {
        _sessions.Remove(sessionKey);
        return null;
      }

      if (!_sources.TryGetValue(session.SourceKey, out var source) || !IsAvailable(source))
      {
        return null;
      }

      source.LastAccessedUtc = _time.GetUtcNow();
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

      output.LastAccessedUtc = _time.GetUtcNow();
      return output;
    }
  }

  public PlaylistOutputFile StoreOutput(
    PlaylistSourceFile source,
    IReadOnlyCollection<string> disabledGroups,
    string temporaryPath,
    PlaylistGenerationResult result)
  {
    var now = _time.GetUtcNow();
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
      output.LastAccessedUtc = _time.GetUtcNow();
      return new PlaylistFileLease(
        output.FilePath,
        SanitizeFileName(downloadFileName ?? "playlist-filtered.m3u"),
        () => ReleaseOutput(outputKey));
    }
  }

  /// <summary>
  /// Pins a private copy of a generated output as the last good list of a player link. The copy
  /// outlives the output's TTL, replaces any earlier copy for the playlist and counts against
  /// MaxDiskBytes.
  /// Returns false when the output is no longer available.
  /// </summary>
  public bool RememberLastGood(Guid playlistId, PlaylistOutputFile output)
  {
    using var outputLease = TryLeaseOutput(output.OutputKey);
    if (outputLease is null)
    {
      return false;
    }

    var length = GetTrackedLength(outputLease.FilePath, output.LengthBytes);
    using var reservation = ReserveCapacity(length, "last good playlist", new[] { outputLease.FilePath });
    var key = LastGoodKey(playlistId);
    var finalPath = Path.Combine(_directory, $"lastgood-{key}-{Guid.NewGuid():N}.m3u");
    try
    {
      File.Copy(outputLease.FilePath, finalPath);
      TrySetPrivateFile(finalPath);
      WriteLastGoodSidecar(finalPath, key);
    }
    catch
    {
      DeleteTemporaryFile(SidecarPath(finalPath));
      DeleteTemporaryFile(finalPath, length);
      throw;
    }

    lock (_sync)
    {
      if (_lastGood.TryGetValue(key, out var previous))
      {
        RemoveLastGoodNoLock(previous);
      }

      _lastGood[key] = new LastGoodFile(key, finalPath, length, _time.GetUtcNow());
      _pendingDeletes.Remove(finalPath);
      // Hand the reserved bytes over to the tracked copy in the same critical section, so a
      // concurrent Cleanup never counts the copy twice (Monitor locks are re-entrant).
      reservation.Dispose();
    }

    return true;
  }

  public PlaylistFileLease? TryLeaseLastGood(Guid playlistId, string? downloadFileName = null)
  {
    lock (_sync)
    {
      if (!_lastGood.TryGetValue(LastGoodKey(playlistId), out var entry))
      {
        return null;
      }

      if (!File.Exists(entry.FilePath))
      {
        RemoveLastGoodNoLock(entry);
        return null;
      }

      // An expired copy is no longer handed out; Cleanup deletes it.
      if (IsExpired(entry))
      {
        return null;
      }

      entry.ActiveLeases++;
      return new PlaylistFileLease(
        entry.FilePath,
        SanitizeFileName(downloadFileName ?? "playlist-filtered.m3u"),
        () => ReleaseLastGood(entry));
    }
  }

  public void ForgetLastGood(Guid playlistId)
  {
    lock (_sync)
    {
      if (_lastGood.TryGetValue(LastGoodKey(playlistId), out var entry))
      {
        RemoveLastGoodNoLock(entry);
      }
    }
  }

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
        if (output.ActiveLeases == 0 && (output.ExpiresAtUtc <= _time.GetUtcNow() || !isCurrentVariant))
        {
          RemoveOutputNoLock(outputKey);
        }
      }
    }
  }

  private void ReleaseLastGood(LastGoodFile entry)
  {
    lock (_sync)
    {
      entry.ActiveLeases = Math.Max(0, entry.ActiveLeases - 1);
      if (entry.ActiveLeases == 0 && entry.Retired)
      {
        RemoveLastGoodNoLock(entry);
      }
    }
  }

  private bool IsAvailable(PlaylistSourceFile source) =>
    source.ExpiresAtUtc > _time.GetUtcNow() && File.Exists(source.FilePath);

  private bool IsAvailable(PlaylistOutputFile output) =>
    output.ExpiresAtUtc > _time.GetUtcNow() && File.Exists(output.FilePath);

  private bool IsExpired(LastGoodFile entry) =>
    entry.CreatedAtUtc.Add(_lastGoodTtl) <= _time.GetUtcNow();

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

    // Last-good copies keep player links answering when the provider fails, so they go only
    // after every other evictable source and output, oldest first.
    foreach (var entry in _lastGood.Values
      .Where(x => x.ActiveLeases == 0 && !protectedPaths.Contains(x.FilePath))
      .OrderBy(x => x.CreatedAtUtc)
      .ToList())
    {
      var length = GetTrackedLength(entry.FilePath, entry.LengthBytes);
      if (RemoveLastGoodNoLock(entry))
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
    var now = _time.GetUtcNow();
    foreach (var session in _sessions.Where(x => x.Value.ExpiresAtUtc <= now).Select(x => x.Key).ToList())
    {
      _sessions.Remove(session);
    }

    foreach (var session in _librarySessions.Where(x => x.Value.ExpiresAtUtc <= now).Select(x => x.Key).ToList())
    {
      _librarySessions.Remove(session);
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

    foreach (var entry in _lastGood.Values.Where(x => IsExpired(x) && !protectedPaths.Contains(x.FilePath)).ToList())
    {
      RemoveLastGoodNoLock(entry);
    }
  }

  private static ISet<string> ToPathSet(IEnumerable<string>? paths) =>
    paths is null
      ? new HashSet<string>(StringComparer.Ordinal)
      : new HashSet<string>(paths, StringComparer.Ordinal);

  private long GetTrackedBytesNoLock() =>
    _sources.Values.Sum(x => GetTrackedLength(x.FilePath, x.LengthBytes)) +
    _outputs.Values.Sum(x => GetTrackedLength(x.FilePath, x.LengthBytes)) +
    _lastGood.Values.Sum(x => GetTrackedLength(x.FilePath, x.LengthBytes)) +
    _retiredLastGood.Sum(x => GetTrackedLength(x.FilePath, x.LengthBytes)) +
    _pendingDeletes.Values.Sum(x => GetTrackedLength(x.FilePath, x.LengthBytes));

  private bool RemoveSourceNoLock(string sourceKey)
  {
    if (!_sources.Remove(sourceKey, out var source))
    {
      return false;
    }

    return DeleteSourceFilesNoLock(source);
  }

  private bool DeleteSourceFilesNoLock(PlaylistSourceFile source)
  {
    DeleteOrTrackNoLock(SidecarPath(source.FilePath), 0);
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

  private bool RemoveLastGoodNoLock(LastGoodFile entry)
  {
    if (_lastGood.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
    {
      _lastGood.Remove(entry.Key);
    }

    // Without its sidecar the copy is no longer restored after a restart, even while leased.
    DeleteOrTrackNoLock(SidecarPath(entry.FilePath), 0);

    if (entry.ActiveLeases > 0)
    {
      if (!entry.Retired)
      {
        entry.Retired = true;
        _retiredLastGood.Add(entry);
      }
      return false;
    }

    _retiredLastGood.Remove(entry);
    return DeleteOrTrackNoLock(entry.FilePath, entry.LengthBytes);
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
      // Session and output metadata is process-local, so those files from a previous process
      // cannot be safely reused. Restrict deletion to names this service itself generates.
      // Sources and last-good copies with a matching, unexpired sidecar are restored; every
      // other one is deleted. Raw-text sources never get a sidecar, so they are deleted too.
      lock (_sync)
      {
        var restored = new List<LastGoodFile>();
        var restoredSources = new List<PlaylistSourceFile>();
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
          var name = Path.GetFileName(file);
          if (name.StartsWith("source-", StringComparison.Ordinal) && !name.EndsWith(".partial", StringComparison.Ordinal))
          {
            if (name.EndsWith(".m3u", StringComparison.Ordinal))
            {
              if (TryReadSource(file) is { } source)
              {
                restoredSources.Add(source);
              }
              else
              {
                DeleteOrTrackNoLock(SidecarPath(file), 0);
                DeleteOrTrackNoLock(file, _options.MaxDiskBytes);
              }
            }
            else if (!name.EndsWith(".json", StringComparison.Ordinal) || !File.Exists(Path.ChangeExtension(file, ".m3u")))
            {
              DeleteOrTrackNoLock(file, _options.MaxDiskBytes);
            }
          }
          else if (name.EndsWith(".partial", StringComparison.Ordinal) ||
              name.StartsWith("output-", StringComparison.Ordinal) ||
              name.StartsWith("raw-", StringComparison.Ordinal))
          {
            // A stat failure at startup must not make a left-over cache file invisible to the
            // new process. The whole cache budget is the safe fallback until retry succeeds.
            DeleteOrTrackNoLock(file, _options.MaxDiskBytes);
          }
          else if (name.StartsWith("lastgood-", StringComparison.Ordinal))
          {
            if (name.EndsWith(".m3u", StringComparison.Ordinal))
            {
              if (TryReadLastGood(file) is { } entry)
              {
                restored.Add(entry);
              }
              else
              {
                DeleteOrTrackNoLock(SidecarPath(file), 0);
                DeleteOrTrackNoLock(file, _options.MaxDiskBytes);
              }
            }
            else if (!name.EndsWith(".json", StringComparison.Ordinal) || !File.Exists(Path.ChangeExtension(file, ".m3u")))
            {
              // Unknown names and sidecars without their copy. A sidecar next to a copy is
              // handled together with that copy.
              DeleteOrTrackNoLock(file, _options.MaxDiskBytes);
            }
          }
        }

        foreach (var source in restoredSources.OrderByDescending(x => x.LastAccessedUtc))
        {
          if (!_sources.TryAdd(source.SourceKey, source))
          {
            DeleteSourceFilesNoLock(source);
          }
        }

        foreach (var entry in restored.OrderByDescending(x => x.CreatedAtUtc))
        {
          if (_lastGood.ContainsKey(entry.Key))
          {
            RemoveLastGoodNoLock(entry);
          }
          else
          {
            _lastGood[entry.Key] = entry;
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

  // The on-disk name must not reveal the playlist id.
  private static string LastGoodKey(Guid playlistId) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"lastgood:{playlistId:N}")))[..32].ToLowerInvariant();

  private static string SidecarPath(string filePath) => Path.ChangeExtension(filePath, ".json");

  /// <summary>
  /// Writes the restart metadata of a URL source: the hashed URL key, never the URL itself.
  /// A failed write only costs the reuse after a restart, so it is logged, not thrown.
  /// </summary>
  private void WriteSourceSidecarNoLock(PlaylistSourceFile source, DateTimeOffset fetchedAtUtc)
  {
    var sidecarPath = SidecarPath(source.FilePath);
    var temporaryPath = $"{sidecarPath}.partial";
    try
    {
      var sidecar = new SourceSidecar(source.SourceKey, source.ContentHash, source.LengthBytes, source.ETag, source.LastModifiedUtc, fetchedAtUtc);
      var bytes = JsonSerializer.SerializeToUtf8Bytes(sidecar);
      if (bytes.Length > MaxSourceSidecarBytes)
      {
        // The reader would reject it; drop a stale sidecar instead of writing one that cannot be restored.
        DeleteOrTrackNoLock(sidecarPath, 0);
        _logger.LogWarning("Restart metadata of a playlist source exceeds {MaxBytes} bytes and is not kept", MaxSourceSidecarBytes);
        return;
      }

      File.WriteAllBytes(temporaryPath, bytes);
      TrySetPrivateFile(temporaryPath);
      File.Move(temporaryPath, sidecarPath, overwrite: true);
    }
    catch (Exception ex)
    {
      DeleteOrTrackNoLock(temporaryPath, 0);
      DeleteOrTrackNoLock(sidecarPath, 0);
      _logger.LogWarning(ex, "Unable to write the restart metadata of a playlist source");
    }
  }

  /// <summary>Returns the restorable source, or null when the file and its sidecar must be deleted.</summary>
  private PlaylistSourceFile? TryReadSource(string filePath)
  {
    try
    {
      var sidecarPath = SidecarPath(filePath);
      var sidecarInfo = new FileInfo(sidecarPath);
      if (!sidecarInfo.Exists || sidecarInfo.Length > MaxSourceSidecarBytes)
      {
        return null;
      }

      var sidecar = JsonSerializer.Deserialize<SourceSidecar>(File.ReadAllBytes(sidecarPath));
      var length = new FileInfo(filePath).Length;
      if (sidecar is null ||
          string.IsNullOrEmpty(sidecar.Key) ||
          string.IsNullOrEmpty(sidecar.ContentHash) ||
          !string.Equals(Path.GetFileName(filePath), $"source-{sidecar.Key}-{sidecar.ContentHash[..Math.Min(16, sidecar.ContentHash.Length)]}.m3u", StringComparison.Ordinal) ||
          sidecar.LengthBytes != length ||
          sidecar.FetchedAtUtc.Add(_sourceTtl) <= _time.GetUtcNow() ||
          !string.Equals(sidecar.ContentHash, HashFile(filePath), StringComparison.Ordinal))
      {
        return null;
      }

      // The URL is not stored; lookups go by its hash, the source key.
      return new PlaylistSourceFile(
        sidecar.Key,
        string.Empty,
        filePath,
        sidecar.ContentHash,
        length,
        sidecar.ETag,
        sidecar.LastModifiedUtc,
        sidecar.FetchedAtUtc.Add(_sourceTtl),
        sidecar.FetchedAtUtc.Add(_revalidationInterval),
        sidecar.FetchedAtUtc);
    }
    catch (Exception ex)
    {
      _logger.LogDebug(ex, "Unable to restore a playlist source at startup");
      return null;
    }
  }

  private static string HashFile(string path)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
  }

  /// <summary>
  /// Writes the restart metadata of a last-good copy. It holds only the hashed playlist key,
  /// never the playlist id, source URL or share code.
  /// </summary>
  private void WriteLastGoodSidecar(string filePath, string key)
  {
    var sidecar = new LastGoodSidecar(key, HashFile(filePath), new FileInfo(filePath).Length, _time.GetUtcNow());
    var sidecarPath = SidecarPath(filePath);
    var temporaryPath = $"{sidecarPath}.partial";
    try
    {
      File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(sidecar));
      TrySetPrivateFile(temporaryPath);
      File.Move(temporaryPath, sidecarPath);
    }
    catch
    {
      DeleteTemporaryFile(temporaryPath);
      throw;
    }
  }

  /// <summary>Returns the restorable entry of a last-good copy, or null when it must be deleted.</summary>
  private LastGoodFile? TryReadLastGood(string filePath)
  {
    try
    {
      var sidecarPath = SidecarPath(filePath);
      var sidecarInfo = new FileInfo(sidecarPath);
      if (!sidecarInfo.Exists || sidecarInfo.Length > 4096)
      {
        return null;
      }

      var sidecar = JsonSerializer.Deserialize<LastGoodSidecar>(File.ReadAllBytes(sidecarPath));
      var name = Path.GetFileName(filePath);
      var length = new FileInfo(filePath).Length;
      if (sidecar is null ||
          string.IsNullOrEmpty(sidecar.Key) ||
          !name.StartsWith($"lastgood-{sidecar.Key}-", StringComparison.Ordinal) ||
          sidecar.LengthBytes != length ||
          sidecar.CreatedAtUtc.Add(_lastGoodTtl) <= _time.GetUtcNow() ||
          !string.Equals(sidecar.ContentHash, HashFile(filePath), StringComparison.Ordinal))
      {
        return null;
      }

      return new LastGoodFile(sidecar.Key, filePath, length, sidecar.CreatedAtUtc);
    }
    catch (Exception ex)
    {
      _logger.LogDebug(ex, "Unable to restore a last good playlist copy at startup");
      return null;
    }
  }

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

public sealed record PlaylistGenerationResult(int TotalChannels, int KeptChannels, long OutputBytes)
{
  /// <summary>The number of watchlist copies written at the top; not counted in the channel totals.</summary>
  public int WatchlistEntries { get; init; }
}

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

internal sealed record LibrarySession(string SourceKey, Guid PlaylistId, string UserId, DateTimeOffset ExpiresAtUtc);

internal sealed record PendingDeleteFile(string FilePath, long LengthBytes);

internal sealed record SourceSidecar(
  string Key,
  string ContentHash,
  long LengthBytes,
  string? ETag,
  DateTimeOffset? LastModifiedUtc,
  DateTimeOffset FetchedAtUtc);

internal sealed record LastGoodSidecar(string Key, string ContentHash, long LengthBytes, DateTimeOffset CreatedAtUtc);

internal sealed class LastGoodFile
{
  internal LastGoodFile(string key, string filePath, long lengthBytes, DateTimeOffset createdAtUtc = default)
  {
    Key = key;
    FilePath = filePath;
    LengthBytes = lengthBytes;
    CreatedAtUtc = createdAtUtc;
  }

  internal string Key { get; }
  internal DateTimeOffset CreatedAtUtc { get; }
  internal string FilePath { get; }
  internal long LengthBytes { get; }
  internal int ActiveLeases { get; set; }
  internal bool Retired { get; set; }
}
