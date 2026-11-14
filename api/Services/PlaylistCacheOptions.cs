namespace Api.Services;

/// <summary>
/// Bounds disk-backed playlist work without imposing a small in-memory download limit.
/// Values can be overridden with PlaylistCache__* environment variables.
/// </summary>
public sealed class PlaylistCacheOptions
{
  public const string SectionName = "PlaylistCache";

  /// <summary>Directory for private, disposable source and generated playlist files.</summary>
  public string? Directory { get; set; }

  /// <summary>
  /// Hard safety ceiling for a single downloaded source. The default deliberately supports
  /// very large playlists; set to 0 only when the deployment has an external disk quota.
  /// </summary>
  public long MaxSourceBytes { get; set; } = 2L * 1024 * 1024 * 1024;

  /// <summary>Maximum total disk space used by the ephemeral cache.</summary>
  public long MaxDiskBytes { get; set; } = 8L * 1024 * 1024 * 1024;

  /// <summary>Lifetime of an opaque analyze-session and generated output file.</summary>
  public int EntryTtlMinutes { get; set; } = 30;

  /// <summary>Lifetime of a source file and its revalidation metadata.</summary>
  public int SourceTtlMinutes { get; set; } = 24 * 60;

  /// <summary>
  /// Minimum age before a cached source is checked upstream again. This avoids downloading and
  /// hashing a large source for every share request, including providers without validators.
  /// Set to 0 to revalidate every request when freshness matters more than CPU/network usage.
  /// </summary>
  public int RevalidationIntervalMinutes { get; set; } = 15;

  /// <summary>End-to-end timeout for a streamed upstream body.</summary>
  public int DownloadTimeoutSeconds { get; set; } = 10 * 60;

  /// <summary>Connection and response-header timeout; body reads use DownloadTimeoutSeconds.</summary>
  public int HeaderTimeoutSeconds { get; set; } = 15;

  /// <summary>Maximum characters accepted in a single M3U line.</summary>
  public int MaxLineLengthChars { get; set; } = 64 * 1024;

  /// <summary>Maximum distinct groups returned to the browser for one source.</summary>
  public int MaxGroupCount { get; set; } = 10_000;

  /// <summary>Maximum characters retained for a distinct group title.</summary>
  public int MaxGroupTitleLengthChars { get; set; } = 512;

  /// <summary>Approximate heap budget for all distinct group metadata in one analysis.</summary>
  public long MaxGroupMetadataBytes { get; set; } = 8L * 1024 * 1024;

  /// <summary>Maximum number of expensive fetch/parse/generate jobs in this process.</summary>
  public int MaxConcurrentJobs { get; set; } = 1;

  /// <summary>How long an API request may wait for a playlist job permit.</summary>
  public int QueueTimeoutSeconds { get; set; } = 30;
}
