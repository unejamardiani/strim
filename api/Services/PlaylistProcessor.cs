using System.Buffers;
using System.Text;
using Api.Models;

namespace Api.Services;

public static class PlaylistProcessor
{
  private const int DefaultMaxLineLengthChars = 64 * 1024;
  private const int DefaultMaxGroupCount = 10_000;
  private const int DefaultMaxGroupTitleLengthChars = 512;
  private const long DefaultMaxGroupMetadataBytes = 8L * 1024 * 1024;
  // Approximate dictionary + string overhead for one distinct group. This is intentionally
  // conservative: the cap protects the JSON response and process heap, not just raw text.
  private const int EstimatedGroupEntryOverheadBytes = 128;
  private const string GroupKeySeparator = " \u00B7 ";
  private static readonly int ContentTypeCount = Enum.GetValues<PlaylistContentType>().Length;
  private static readonly int MaxGroupKeySuffixLength =
    GroupKeySeparator.Length + Enum.GetNames<PlaylistContentType>().Max(name => name.Length);
  private static readonly string[] ExpirationKeys = new[] { "exp", "expires", "expire", "expiration" };

  /// <summary>
  /// Key for one content type of a group-title that the provider reuses across live channels,
  /// movies and series (e.g. "Germany · Series"). Must match groupKey() in main.js.
  /// </summary>
  public static string GroupKey(string groupTitle, PlaylistContentType contentType) =>
    $"{groupTitle}{GroupKeySeparator}{contentType}";

  public static int MaxGroupKeyLength(int maxGroupTitleLengthChars) =>
    Math.Max(1, maxGroupTitleLengthChars) + MaxGroupKeySuffixLength;

  /// <summary>
  /// Legacy in-memory helper kept for small callers/tests. HTTP endpoints use the file-backed
  /// methods below so a remote source is never split into a second full in-memory representation.
  /// </summary>
  public static (Dictionary<string, int> Groups, int Total) CountGroups(string text)
  {
    using var reader = new StringReader(text ?? string.Empty);
    var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var total = 0;

    while (ReadNextContentLine(reader) is { } line)
    {
      if (!line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) continue;

      total++;
      var groupTitle = ExtractGroupTitle(line);
      groups[groupTitle] = groups.TryGetValue(groupTitle, out var existing) ? existing + 1 : 1;
      _ = ReadNextContentLine(reader); // Match the historical "metadata followed by URL" parser.
    }

    return (groups, total);
  }

  public static async Task<PlaylistAnalysisResult> AnalyzeFileAsync(
    string filePath,
    CancellationToken cancellationToken,
    int maxLineLengthChars = DefaultMaxLineLengthChars,
    int maxGroupCount = DefaultMaxGroupCount,
    int maxGroupTitleLengthChars = DefaultMaxGroupTitleLengthChars,
    long maxGroupMetadataBytes = DefaultMaxGroupMetadataBytes)
  {
    // Counts per group-title, indexed by PlaylistContentType.
    var groups = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
    var total = 0;
    var groupVariantCount = 0;
    long groupMetadataBytes = 0;

    await using var stream = new FileStream(
      filePath,
      FileMode.Open,
      FileAccess.Read,
      FileShare.Read,
      128 * 1024,
      FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 128 * 1024, leaveOpen: false);
    using var lines = new BoundedLineReader(reader, maxLineLengthChars);

    while (await lines.ReadNextContentLineAsync(cancellationToken) is { } line)
    {
      if (!line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) continue;

      total++;
      var groupTitle = ExtractGroupTitle(line);
      EnsureGroupTitleWithinLimit(groupTitle, maxGroupTitleLengthChars);
      var url = await lines.ReadNextContentLineAsync(cancellationToken);
      IncrementGroup(
        groups,
        groupTitle,
        DetectContentType(url),
        maxGroupCount,
        ref groupVariantCount,
        ref groupMetadataBytes,
        maxGroupMetadataBytes);
    }

    return new PlaylistAnalysisResult(BuildGroups(groups), total);
  }

  /// <summary>Writes a filtered M3U file while reading the source one line at a time.</summary>
  /// <param name="splitGroupTitles">
  /// <see cref="PlaylistAnalysisResult.SplitGroupTitles"/>: movies and series of these titles are written
  /// under their <see cref="GroupKey"/> so players show them apart from the live channels.
  /// </param>
  public static async Task<PlaylistGenerationResult> GenerateFilteredFileAsync(
    string inputPath,
    string outputPath,
    ISet<string> disabledGroups,
    CancellationToken cancellationToken,
    int maxLineLengthChars = DefaultMaxLineLengthChars,
    long maxOutputBytes = 0,
    IReadOnlySet<string>? splitGroupTitles = null)
  {
    var total = 0;
    var kept = 0;
    var outputBytes = new OutputByteCounter(maxOutputBytes);

    await using var input = new FileStream(
      inputPath,
      FileMode.Open,
      FileAccess.Read,
      FileShare.Read,
      128 * 1024,
      FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 128 * 1024, leaveOpen: false);
    using var lines = new BoundedLineReader(reader, maxLineLengthChars);
    await using var output = new FileStream(
      outputPath,
      FileMode.CreateNew,
      FileAccess.Write,
      FileShare.None,
      128 * 1024,
      FileOptions.Asynchronous | FileOptions.SequentialScan);
    PlaylistFileCache.TrySetPrivateFile(outputPath);
    await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 128 * 1024, leaveOpen: false);

    await WriteLineAsync(writer, "#EXTM3U", cancellationToken, outputBytes);
    await WriteLineAsync(writer, "# Created with Strim (https://strim.plis.dev)", cancellationToken, outputBytes);

    while (await lines.ReadNextContentLineAsync(cancellationToken) is { } line)
    {
      if (!line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) continue;

      total++;
      var url = await lines.ReadNextContentLineAsync(cancellationToken) ?? string.Empty;
      var groupTitle = ExtractGroupTitle(line);
      var contentType = DetectContentType(url);
      if (IsDisabled(disabledGroups, groupTitle, contentType)) continue;

      kept++;
      var outputGroupTitle = OutputGroupTitle(groupTitle, contentType, splitGroupTitles);
      await WriteLineAsync(writer, WithGroupTitle(line, groupTitle, outputGroupTitle), cancellationToken, outputBytes);
      await WriteLineAsync(writer, url, cancellationToken, outputBytes);
    }

    await writer.FlushAsync(cancellationToken);
    return new PlaylistGenerationResult(total, kept, outputBytes.WrittenBytes);
  }

  public static PlaylistFilterResult GenerateFiltered(string text, HashSet<string> disabledGroups)
  {
    using var reader = new StringReader(text ?? string.Empty);
    using var writer = new StringWriter();
    writer.WriteLine("#EXTM3U");
    writer.WriteLine("# Created with Strim (https://strim.plis.dev)");

    var total = 0;
    var kept = 0;
    while (ReadNextContentLine(reader) is { } line)
    {
      if (!line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) continue;

      total++;
      var url = ReadNextContentLine(reader) ?? string.Empty;
      var groupTitle = ExtractGroupTitle(line);
      if (IsDisabled(disabledGroups, groupTitle, DetectContentType(url))) continue;

      kept++;
      writer.WriteLine(EnsureGroupInExtinf(line, groupTitle));
      writer.WriteLine(url);
    }

    return new PlaylistFilterResult(writer.ToString().TrimEnd(), total, kept);
  }

  public static string DeriveNameFromUrl(string? url)
  {
    if (string.IsNullOrWhiteSpace(url))
    {
      return "playlist";
    }

    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
      var lastSegment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
      return string.IsNullOrWhiteSpace(lastSegment) ? uri.Host : lastSegment;
    }

    return url;
  }

  public static List<GroupResult> ToGroupResults(IEnumerable<PlaylistGroup> groups) =>
    groups.Select(g => new GroupResult(g.Key, g.Count, g.GroupTitle, g.ContentType.ToString())).ToList();

  /// <summary>
  /// Xtream Codes panels serve VOD as /movie/&lt;user&gt;/&lt;pass&gt;/&lt;id&gt;.&lt;ext&gt; and episodes as
  /// /series/...; everything else is treated as live so plain M3U sources keep one group per title.
  /// </summary>
  public static PlaylistContentType DetectContentType(string? url)
  {
    if (string.IsNullOrEmpty(url)) return PlaylistContentType.Live;

    var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
    if (schemeEnd < 0) return PlaylistContentType.Live;

    var pathStart = url.IndexOf('/', schemeEnd + 3);
    if (pathStart < 0) return PlaylistContentType.Live;

    var path = url.AsSpan(pathStart);
    if (path.StartsWith("/movie/", StringComparison.OrdinalIgnoreCase)) return PlaylistContentType.Movies;
    if (path.StartsWith("/series/", StringComparison.OrdinalIgnoreCase)) return PlaylistContentType.Series;
    return PlaylistContentType.Live;
  }

  /// <summary>
  /// A plain group-title disables every entry of that title (unsplit groups and filters saved
  /// before groups were split); a <see cref="GroupKey"/> disables only that content type.
  /// </summary>
  private static bool IsDisabled(ISet<string> disabledGroups, string groupTitle, PlaylistContentType contentType) =>
    disabledGroups.Count > 0 &&
    (disabledGroups.Contains(groupTitle) ||
     disabledGroups.Contains(GroupKey(groupTitle, contentType)));

  /// <summary>
  /// Live channels keep the provider's group-title; movies and series of a title the provider also
  /// uses for another content type move to their own group (e.g. "Germany · Series").
  /// </summary>
  private static string OutputGroupTitle(
    string groupTitle,
    PlaylistContentType contentType,
    IReadOnlySet<string>? splitGroupTitles) =>
    contentType != PlaylistContentType.Live && splitGroupTitles?.Contains(groupTitle) == true
      ? GroupKey(groupTitle, contentType)
      : groupTitle;

  public static DateTimeOffset? TryExtractExpiration(Uri? uri)
  {
    if (uri is null) return null;
    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
    foreach (var keyCandidate in ExpirationKeys)
    {
      if (!query.TryGetValue(keyCandidate, out var values)) continue;
      foreach (var raw in values)
      {
        if (string.IsNullOrWhiteSpace(raw)) continue;

        if (long.TryParse(raw, out var num))
        {
          if (num > 1000000000000) num /= 1000; // likely ms
          if (num > 1000000000)
          {
            return DateTimeOffset.FromUnixTimeSeconds(num);
          }
        }

        if (DateTimeOffset.TryParse(raw, out var parsed))
        {
          return parsed;
        }
      }
    }
    return null;
  }

  private static string? ReadNextContentLine(StringReader reader)
  {
    while (reader.ReadLine() is { } line)
    {
      line = line.Trim();
      if (line.Length > 0) return line;
    }

    return null;
  }

  private static async Task WriteLineAsync(StreamWriter writer, string value, CancellationToken cancellationToken)
  {
    await WriteLineAsync(writer, value, cancellationToken, outputBytes: null);
  }

  private static async Task WriteLineAsync(
    StreamWriter writer,
    string value,
    CancellationToken cancellationToken,
    OutputByteCounter? outputBytes)
  {
    cancellationToken.ThrowIfCancellationRequested();
    outputBytes?.AddLine(value);
    await writer.WriteAsync(value.AsMemory(), cancellationToken);
    await writer.WriteAsync("\n".AsMemory(), cancellationToken);
  }

  private static void IncrementGroup(
    Dictionary<string, int[]> groups,
    string groupTitle,
    PlaylistContentType contentType,
    int maxGroupCount,
    ref int groupVariantCount,
    ref long groupMetadataBytes,
    long maxGroupMetadataBytes)
  {
    var typeIndex = (int)contentType;
    if (groups.TryGetValue(groupTitle, out var counts) && counts[typeIndex] > 0)
    {
      counts[typeIndex] = checked(counts[typeIndex] + 1);
      return;
    }

    // Every title/content-type pair can become its own row in the analysis response.
    if (groupVariantCount >= Math.Max(1, maxGroupCount))
    {
      throw new PlaylistGroupLimitExceededException(Math.Max(1, maxGroupCount));
    }

    var estimatedBytes = EstimatedGroupEntryOverheadBytes + ((long)groupTitle.Length * sizeof(char)) + Encoding.UTF8.GetByteCount(groupTitle);
    if (maxGroupMetadataBytes > 0 && groupMetadataBytes > maxGroupMetadataBytes - estimatedBytes)
    {
      throw new PlaylistGroupMetadataLimitExceededException(maxGroupMetadataBytes);
    }

    groupMetadataBytes += estimatedBytes;
    groupVariantCount++;
    if (counts is null)
    {
      counts = new int[ContentTypeCount];
      groups[groupTitle] = counts;
    }
    counts[typeIndex] = 1;
  }

  private static List<PlaylistGroup> BuildGroups(Dictionary<string, int[]> groups)
  {
    var result = new List<PlaylistGroup>(groups.Count);
    foreach (var (groupTitle, counts) in groups)
    {
      // Only split titles that really mix content types so existing filters keep their names.
      var split = counts.Count(count => count > 0) > 1;
      for (var i = 0; i < counts.Length; i++)
      {
        if (counts[i] == 0) continue;

        var contentType = (PlaylistContentType)i;
        var key = split ? GroupKey(groupTitle, contentType) : groupTitle;
        result.Add(new PlaylistGroup(key, groupTitle, contentType, counts[i]));
      }
    }

    return result;
  }

  private static void EnsureGroupTitleWithinLimit(string groupTitle, int maxGroupTitleLengthChars)
  {
    var limit = Math.Max(1, maxGroupTitleLengthChars);
    if (groupTitle.Length > limit)
    {
      throw new PlaylistGroupTitleTooLongException(limit);
    }
  }

  private static string ExtractGroupTitle(string extinf)
  {
    if (!TryFindGroupTitleValue(extinf, out var start, out var end)) return "Ungrouped";

    var value = extinf[start..end].Trim();
    return string.IsNullOrWhiteSpace(value) ? "Ungrouped" : value;
  }

  /// <summary>Finds the quoted value of the first group-title attribute as [start, end).</summary>
  private static bool TryFindGroupTitleValue(string extinf, out int start, out int end)
  {
    const string attribute = "group-title";
    var searchAt = 0;
    start = end = 0;

    while (searchAt < extinf.Length)
    {
      var attributeAt = extinf.IndexOf(attribute, searchAt, StringComparison.OrdinalIgnoreCase);
      if (attributeAt < 0) break;

      if (attributeAt > 0 && IsAttributeCharacter(extinf[attributeAt - 1]))
      {
        searchAt = attributeAt + attribute.Length;
        continue;
      }

      var cursor = attributeAt + attribute.Length;
      while (cursor < extinf.Length && char.IsWhiteSpace(extinf[cursor])) cursor++;
      if (cursor >= extinf.Length || extinf[cursor] != '=')
      {
        searchAt = cursor;
        continue;
      }

      cursor++;
      while (cursor < extinf.Length && char.IsWhiteSpace(extinf[cursor])) cursor++;
      if (cursor >= extinf.Length || (extinf[cursor] != '"' && extinf[cursor] != '\''))
      {
        searchAt = cursor;
        continue;
      }

      var quote = extinf[cursor++];
      var closingQuote = extinf.IndexOf(quote, cursor);
      if (closingQuote < 0) return false;

      start = cursor;
      end = closingQuote;
      return true;
    }

    return false;
  }

  private static bool HasGroupTitleAttribute(string extinf)
  {
    const string attribute = "group-title";
    var searchAt = 0;
    while (searchAt < extinf.Length)
    {
      var attributeAt = extinf.IndexOf(attribute, searchAt, StringComparison.OrdinalIgnoreCase);
      if (attributeAt < 0) return false;
      if (attributeAt == 0 || !IsAttributeCharacter(extinf[attributeAt - 1]))
      {
        var cursor = attributeAt + attribute.Length;
        while (cursor < extinf.Length && char.IsWhiteSpace(extinf[cursor])) cursor++;
        if (cursor < extinf.Length && extinf[cursor] == '=') return true;
      }
      searchAt = attributeAt + attribute.Length;
    }
    return false;
  }

  private static bool IsAttributeCharacter(char value) => char.IsLetterOrDigit(value) || value is '-' or '_';

  private static string WithGroupTitle(string extinf, string groupTitle, string outputGroupTitle)
  {
    if (string.Equals(groupTitle, outputGroupTitle, StringComparison.Ordinal))
    {
      return EnsureGroupInExtinf(extinf, groupTitle);
    }

    return TryFindGroupTitleValue(extinf, out var start, out var end)
      ? string.Concat(extinf.AsSpan(0, start), outputGroupTitle, extinf.AsSpan(end))
      : EnsureGroupInExtinf(extinf, outputGroupTitle);
  }

  private static string EnsureGroupInExtinf(string extinf, string groupTitle)
  {
    if (HasGroupTitleAttribute(extinf)) return extinf;

    var attribute = $" group-title=\"{groupTitle}\"";
    var titleSeparator = extinf.IndexOf(',');
    return titleSeparator >= 0
      ? extinf.Insert(titleSeparator, attribute)
      : $"{extinf}{attribute}";
  }

  /// <summary>
  /// Reads one logical line without allowing StreamReader.ReadLineAsync to allocate an
  /// attacker-controlled unbounded string. CRLF's second delimiter is treated as an empty line
  /// and discarded by ReadNextContentLineAsync.
  /// </summary>
  private sealed class BoundedLineReader : IDisposable
  {
    private readonly StreamReader _reader;
    private readonly int _maxLineLengthChars;
    private char[]? _buffer;
    private int _position;
    private int _count;

    public BoundedLineReader(StreamReader reader, int maxLineLengthChars)
    {
      _reader = reader;
      _maxLineLengthChars = Math.Max(1, maxLineLengthChars);
      _buffer = ArrayPool<char>.Shared.Rent(4 * 1024);
    }

    public async ValueTask<string?> ReadNextContentLineAsync(CancellationToken cancellationToken)
    {
      while (await ReadLineAsync(cancellationToken) is { } line)
      {
        line = line.Trim();
        if (line.Length > 0) return line;
      }

      return null;
    }

    private async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
      StringBuilder? line = null;
      var length = 0;
      var sawCharacter = false;

      while (true)
      {
        if (_position >= _count)
        {
          var buffer = _buffer ?? throw new ObjectDisposedException(nameof(BoundedLineReader));
          _count = await _reader.ReadAsync(buffer.AsMemory(), cancellationToken);
          _position = 0;
          if (_count == 0)
          {
            return sawCharacter ? line?.ToString() ?? string.Empty : null;
          }
        }

        var character = _buffer![_position++];
        if (character is '\r' or '\n')
        {
          return line?.ToString() ?? string.Empty;
        }

        sawCharacter = true;
        if (++length > _maxLineLengthChars)
        {
          throw new PlaylistLineTooLongException(_maxLineLengthChars);
        }

        (line ??= new StringBuilder(Math.Min(256, _maxLineLengthChars))).Append(character);
      }
    }

    public void Dispose()
    {
      var buffer = Interlocked.Exchange(ref _buffer, null);
      if (buffer is not null)
      {
        ArrayPool<char>.Shared.Return(buffer);
      }
    }
  }

  private sealed class OutputByteCounter
  {
    private readonly long _maxBytes;
    private long _writtenBytes;

    public OutputByteCounter(long maxBytes) => _maxBytes = maxBytes;

    public long WrittenBytes => _writtenBytes;

    public void AddLine(string value)
    {
      var lineBytes = Encoding.UTF8.GetByteCount(value) + 1L;
      if (_maxBytes > 0 && _writtenBytes > _maxBytes - lineBytes)
      {
        throw new PlaylistGeneratedSizeExceededException(_maxBytes);
      }

      _writtenBytes += lineBytes;
    }
  }
}

public sealed record PlaylistAnalysisResult(IReadOnlyList<PlaylistGroup> Groups, int TotalChannels)
{
  /// <summary>Group-titles the provider reuses across content types, i.e. the titles that were split.</summary>
  public IReadOnlySet<string> SplitGroupTitles { get; } = new HashSet<string>(
    Groups.Where(g => g.Key != g.GroupTitle).Select(g => g.GroupTitle),
    StringComparer.OrdinalIgnoreCase);
}

public enum PlaylistContentType
{
  Live,
  Movies,
  Series,
}

/// <summary>
/// One selectable group. <see cref="Key"/> is the group-title, or a
/// <see cref="PlaylistProcessor.GroupKey"/> when the provider reuses the title across content types.
/// </summary>
public sealed record PlaylistGroup(string Key, string GroupTitle, PlaylistContentType ContentType, int Count);

public sealed class PlaylistLineTooLongException : InvalidOperationException
{
  public PlaylistLineTooLongException(int maxLineLengthChars)
    : base($"Playlist contains a line longer than the configured {maxLineLengthChars:N0} characters. Increase PlaylistCache__MaxLineLengthChars only if this source is trusted.")
  {
  }
}

public sealed class PlaylistGroupLimitExceededException : InvalidOperationException
{
  public PlaylistGroupLimitExceededException(int maxGroupCount)
    : base($"Playlist contains more than the configured {maxGroupCount:N0} distinct groups. Increase PlaylistCache__MaxGroupCount only if this source is trusted.")
  {
  }
}

public sealed class PlaylistGroupTitleTooLongException : InvalidOperationException
{
  public PlaylistGroupTitleTooLongException(int maxGroupTitleLengthChars)
    : base($"Playlist contains a group title longer than the configured {maxGroupTitleLengthChars:N0} characters.")
  {
  }
}

public sealed class PlaylistGroupMetadataLimitExceededException : InvalidOperationException
{
  public PlaylistGroupMetadataLimitExceededException(long maxGroupMetadataBytes)
    : base($"Playlist group metadata exceeds the configured {maxGroupMetadataBytes / (1024 * 1024):N0} MiB limit. Increase PlaylistCache__MaxGroupMetadataBytes only if this source is trusted.")
  {
  }
}

public sealed class PlaylistGeneratedSizeExceededException : InvalidOperationException
{
  public PlaylistGeneratedSizeExceededException(long maxOutputBytes)
    : base($"Generated playlist exceeds the configured {maxOutputBytes / (1024 * 1024)} MiB output limit. Increase PlaylistCache__MaxSourceBytes and PlaylistCache__MaxDiskBytes together to support it.")
  {
  }
}
