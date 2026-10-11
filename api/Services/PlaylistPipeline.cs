using Api.Models;

namespace Api.Services;

/// <summary>
/// Fetch → analyze → generate, shared by the editor endpoints, public share links and the
/// background refresh. Every step reuses the disk cache: unchanged sources are not downloaded
/// again, analyses are kept per source, and outputs per source hash and filter set.
/// </summary>
public sealed class PlaylistPipeline(PlaylistFileCache cache, PlaylistSourceFetcher fetcher)
{
  public PlaylistFileCache Cache => cache;

  /// <param name="reuseRecent">
  /// Serve a source fetched within the revalidation interval without contacting the provider.
  /// The background refresh passes false so it always revalidates.
  /// </param>
  public async Task<PlaylistSourceFile> GetSourceAsync(string url, CancellationToken cancellationToken, bool reuseRecent = true)
  {
    if (reuseRecent && cache.TryGetFreshSource(url) is { } fresh)
    {
      return fresh;
    }

    // Conditional GET: providers that support validators answer 304 for an unchanged list.
    var existing = cache.TryGetSource(url);
    var fetched = await fetcher.FetchAsync(url, existing, cancellationToken);
    if (fetched.NotModified)
    {
      if (existing is null)
      {
        throw new HttpRequestException("The playlist source returned an unusable cache validation response.");
      }

      cache.TouchNotModified(existing, fetched.ETag, fetched.LastModifiedUtc);
      return existing;
    }

    return cache.StoreDownloaded(url, fetched.DownloadedFile
      ?? throw new InvalidOperationException("Playlist source did not return a body."));
  }

  /// <summary>
  /// Generation needs the analysis to know which group-titles mix content types. It is cached on
  /// the source, so a file is only read twice when that source was never analyzed before.
  /// </summary>
  public async Task<PlaylistAnalysisResult> GetAnalysisAsync(PlaylistSourceFile source, CancellationToken cancellationToken)
  {
    var analysis = cache.TryGetAnalysis(source)
      ?? await PlaylistProcessor.AnalyzeFileAsync(
        source.FilePath,
        cancellationToken,
        cache.MaxLineLengthChars,
        cache.MaxGroupCount,
        cache.MaxGroupTitleLengthChars,
        cache.MaxGroupMetadataBytes);
    cache.StoreAnalysis(source, analysis);
    return analysis;
  }

  public async Task<PlaylistOutputFile> GetOutputAsync(
    PlaylistSourceFile source,
    IReadOnlyCollection<string> disabledGroups,
    CancellationToken cancellationToken)
  {
    var disabled = new HashSet<string>(disabledGroups, StringComparer.OrdinalIgnoreCase);
    if (cache.TryGetOutput(source.ContentHash, disabled) is { } existing)
    {
      return existing;
    }

    var analysis = await GetAnalysisAsync(source, cancellationToken);
    var temporaryOutputPath = cache.CreateOutputTemporaryPath();
    using var outputReservation = cache.ReserveOutputCapacity(source);
    try
    {
      var result = await PlaylistProcessor.GenerateFilteredFileAsync(
        source.FilePath,
        temporaryOutputPath,
        disabled,
        cancellationToken,
        cache.MaxLineLengthChars,
        cache.MaxGeneratedBytes,
        analysis.SplitGroupTitles);
      return cache.StoreOutput(source, disabled, temporaryOutputPath, result);
    }
    catch
    {
      cache.DeleteTemporaryFile(temporaryOutputPath, outputReservation.ReservedBytes);
      throw;
    }
  }

  /// <summary>Records what was fetched so the database shows when and what a playlist last saw.</summary>
  public static void CopySourceMetadata(Playlist playlist, PlaylistSourceFile source)
  {
    playlist.SourceHash = source.ContentHash;
    // A nonconforming upstream ETag must not exceed the column and turn a download into a 5xx.
    playlist.SourceETag = string.IsNullOrEmpty(source.ETag) || source.ETag.Length <= 512 ? source.ETag : source.ETag[..512];
    playlist.SourceLastModifiedUtc = source.LastModifiedUtc;
    playlist.SourceLengthBytes = source.LengthBytes;
    playlist.SourceCheckedUtc = DateTimeOffset.UtcNow;
  }
}
