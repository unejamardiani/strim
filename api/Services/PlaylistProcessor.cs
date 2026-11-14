using System.Buffers;
using System.Text;
using Api.Models;

namespace Api.Services;

public static class PlaylistProcessor
{
  private const int DefaultMaxLineLengthChars = 64 * 1024;
  private const int DefaultMaxGroupCount = 10_000;
  private const int DefaultMaxGroupTitleLengthChars = 512;

  /// <summary>Title given to entries without a group-title.</summary>
  private const string UngroupedTitle = "Ungrouped";
  private const long DefaultMaxGroupMetadataBytes = 8L * 1024 * 1024;
  // Approximate dictionary + string overhead for one distinct group. This is intentionally
  // conservative: the cap protects the JSON response and process heap, not just raw text.
  private const int EstimatedGroupEntryOverheadBytes = 128;
  /// <summary>Most #EXTVLCOPT/#KODIPROP/other '#' lines kept between one #EXTINF and its address.</summary>
  public const int MaxOptionLinesPerEntry = 32;
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
    // Per content type: true once an entry carries a real group title that equals the fallback title.
    var explicitUngrouped = new bool[ContentTypeCount];

    await using var stream = new FileStream(
      filePath,
      FileMode.Open,
      FileAccess.Read,
      FileShare.Read,
      128 * 1024,
      FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 128 * 1024, leaveOpen: false);
    using var lines = new BoundedLineReader(reader, maxLineLengthChars);
    string? pending = null;

    while ((pending ?? await lines.ReadNextContentLineAsync(cancellationToken)) is { } line)
    {
      pending = null;
      if (!IsExtinf(line)) continue;

      total++;
      (var url, pending, var extGrp) = await ReadEntryTailAsync(lines, optionLines: null, cancellationToken);
      var groupTitle = ExtractGroupTitle(line, extGrp, out var hasTitle);
      var contentType = DetectContentType(url);
      if (hasTitle && IsUngroupedTitle(groupTitle)) explicitUngrouped[(int)contentType] = true;
      EnsureGroupTitleWithinLimit(groupTitle, maxGroupTitleLengthChars);
      IncrementGroup(
        groups,
        groupTitle,
        contentType,
        maxGroupCount,
        ref groupVariantCount,
        ref groupMetadataBytes,
        maxGroupMetadataBytes);
    }

    return new PlaylistAnalysisResult(BuildGroups(groups, explicitUngrouped), total);
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

    // The source's header carries the programme-guide address (url-tvg, x-tvg-url, ...), so it is passed
    // through as is; any other first line is processed as content.
    string? pending = await lines.ReadNextContentLineAsync(cancellationToken);
    var header = "#EXTM3U";
    if (pending is not null && IsHeader(pending))
    {
      if (pending.Length > header.Length) header = pending;
      pending = null;
    }

    await WriteLineAsync(writer, header, cancellationToken, outputBytes);
    await WriteLineAsync(writer, "# Created with Strim (https://strim.plis.dev)", cancellationToken, outputBytes);

    // Each GroupKey is built at most once per (title, content type), whatever the casing.
    var decisions = new GroupDecisions(disabledGroups, splitGroupTitles);

    var optionLines = new List<string>();
    while ((pending ?? await lines.ReadNextContentLineAsync(cancellationToken)) is { } line)
    {
      pending = null;
      if (!IsExtinf(line)) continue;

      total++;
      (var address, pending, var extGrp) = await ReadEntryTailAsync(lines, optionLines, cancellationToken);
      var url = address ?? string.Empty;
      var groupTitle = ExtractGroupTitle(line, extGrp);
      var contentType = DetectContentType(url);
      var decision = decisions.Decide(groupTitle, contentType);
      if (decision.Disabled) continue;

      kept++;
      await WriteLineAsync(writer, WithGroupTitle(line, groupTitle, decision.Suffix), cancellationToken, outputBytes);
      foreach (var optionLine in optionLines)
      {
        await WriteLineAsync(writer, optionLine, cancellationToken, outputBytes);
      }
      await WriteLineAsync(writer, url, cancellationToken, outputBytes);
    }

    await writer.FlushAsync(cancellationToken);
    return new PlaylistGenerationResult(total, kept, outputBytes.WrittenBytes);
  }

  /// <summary>#EXTM3U followed by the end of the line or whitespace; "#EXTM3U8foo" is not a header.</summary>
  private static bool IsHeader(string line) =>
    line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)
    && (line.Length == 7 || char.IsWhiteSpace(line[7]));

  private static bool IsExtinf(string line) => line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Reads the lines after an #EXTINF: every '#' line (#EXTVLCOPT, #KODIPROP, ...) belongs to the entry,
  /// the first other line is its address. A following #EXTINF ends the entry without an address and is
  /// returned as <c>Next</c> so the caller starts a new entry with it. Only this entry's lines are buffered.
  /// <c>ExtGrp</c> is the value of the entry's first #EXTGRP line, if it has one.
  /// </summary>
  private static async ValueTask<(string? Address, string? Next, string? ExtGrp)> ReadEntryTailAsync(
    BoundedLineReader lines,
    List<string>? optionLines,
    CancellationToken cancellationToken)
  {
    optionLines?.Clear();
    var optionCount = 0;
    string? extGrp = null;
    while (await lines.ReadNextContentLineAsync(cancellationToken) is { } line)
    {
      if (IsExtinf(line)) return (null, line, extGrp);
      if (!line.StartsWith('#')) return (line, null, extGrp);
      // A later #EXTM3U is not part of the entry; only the source's first line is written as the header.
      if (IsHeader(line)) continue;

      if (++optionCount > MaxOptionLinesPerEntry)
      {
        throw new PlaylistEntryOptionLinesLimitExceededException(MaxOptionLinesPerEntry);
      }
      if (extGrp is null && line.StartsWith(ExtGrpPrefix, StringComparison.OrdinalIgnoreCase))
      {
        extGrp = line[ExtGrpPrefix.Length..];
      }
      optionLines?.Add(line);
    }

    return (null, null, extGrp);
  }

  private const string ExtGrpPrefix = "#EXTGRP:";

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
    groups.Select(g => new GroupResult(
      g.Key,
      g.Count,
      g.GroupTitle,
      g.ContentType.ToString(),
      GroupSearchWords.From(g.HasTitle ? g.GroupTitle : null))).ToList();

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
  /// Memoises, for one run of <see cref="GenerateFilteredFileAsync"/>, what every entry of one
  /// (group title, content type) gets. Titles that differ only by case share one decision, like the
  /// case-insensitive sets the pipeline passes in. A split title is written as the entry's own title
  /// plus a fixed content-type suffix, so no output key is built for any casing.
  /// </summary>
  internal sealed class GroupDecisions(ISet<string> disabledGroups, IReadOnlySet<string>? splitGroupTitles)
  {
    private static readonly string[] Suffixes =
      Enum.GetValues<PlaylistContentType>().Select(type => $"{GroupKeySeparator}{type}").ToArray();

    // Marks a dropped pair; compared by reference, so no suffix can be mistaken for it.
    private static readonly string DisabledMarker = new('\0', 1);

    // Per content type: null until decided, DisabledMarker, or the output suffix ("" when unsplit).
    private readonly Dictionary<string, string?[]> decisions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many <see cref="GroupKey"/> strings this memo has built.</summary>
    internal int KeysBuilt { get; private set; }

    /// <summary>Whether the entry is dropped and, if kept, the suffix appended to its group title.</summary>
    public (bool Disabled, string Suffix) Decide(string groupTitle, PlaylistContentType contentType)
    {
      if (!decisions.TryGetValue(groupTitle, out var perType))
      {
        perType = new string?[ContentTypeCount];
        decisions[groupTitle] = perType;
      }

      var decision = perType[(int)contentType];
      if (decision is null)
      {
        var disabled = disabledGroups.Count > 0 &&
          (disabledGroups.Contains(groupTitle) || disabledGroups.Contains(BuildKey(groupTitle, contentType)));
        // Live channels keep the provider's group-title; movies and series of a split title move to
        // their own group (e.g. "Germany · Series").
        var split = contentType != PlaylistContentType.Live && splitGroupTitles?.Contains(groupTitle) == true;
        decision = disabled ? DisabledMarker : split ? Suffixes[(int)contentType] : string.Empty;
        perType[(int)contentType] = decision;
      }

      return ReferenceEquals(decision, DisabledMarker) ? (true, string.Empty) : (false, decision);
    }

    private string BuildKey(string groupTitle, PlaylistContentType contentType)
    {
      KeysBuilt++;
      return GroupKey(groupTitle, contentType);
    }
  }

  /// <summary>
  /// A plain group-title disables every entry of that title (unsplit groups and filters saved
  /// before groups were split); a <see cref="GroupKey"/> disables only that content type.
  /// </summary>
  private static bool IsDisabled(ISet<string> disabledGroups, string groupTitle, PlaylistContentType contentType) =>
    disabledGroups.Count > 0 &&
    (disabledGroups.Contains(groupTitle) ||
     disabledGroups.Contains(GroupKey(groupTitle, contentType)));

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

  private static List<PlaylistGroup> BuildGroups(Dictionary<string, int[]> groups, bool[] explicitUngrouped)
  {
    var result = new List<PlaylistGroup>(groups.Count);
    foreach (var (groupTitle, counts) in groups)
    {
      // Only split titles that really mix content types so existing filters keep their names.
      var split = counts.Count(count => count > 0) > 1;
      var isFallbackTitle = IsUngroupedTitle(groupTitle);
      for (var i = 0; i < counts.Length; i++)
      {
        if (counts[i] == 0) continue;

        // Untitled entries share the fallback group; per content type it has a title only if an entry named it so.
        var hasTitle = !isFallbackTitle || explicitUngrouped[i];

        var contentType = (PlaylistContentType)i;
        var key = split ? GroupKey(groupTitle, contentType) : groupTitle;
        result.Add(new PlaylistGroup(key, groupTitle, contentType, counts[i], hasTitle));
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

  /// <summary>The group-title value; without one, the entry's #EXTGRP value; otherwise "Ungrouped".</summary>
  private static string ExtractGroupTitle(string extinf, string? extGrp = null) =>
    ExtractGroupTitle(extinf, extGrp, out _);

  /// <summary>As above; <paramref name="hasTitle"/> is false when the fallback title was used.</summary>
  private static string ExtractGroupTitle(string extinf, string? extGrp, out bool hasTitle)
  {
    hasTitle = true;
    if (TryFindGroupTitleValue(extinf, out var start, out var end))
    {
      var value = extinf[start..end].Trim();
      if (value.Length > 0) return value;
    }

    var group = extGrp?.Trim();
    if (!string.IsNullOrEmpty(group)) return group;

    hasTitle = false;
    return UngroupedTitle;
  }

  private static bool IsUngroupedTitle(string groupTitle) =>
    string.Equals(groupTitle, UngroupedTitle, StringComparison.OrdinalIgnoreCase);

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

  private static string WithGroupTitle(string extinf, string groupTitle, string suffix)
  {
    if (suffix.Length == 0) return EnsureGroupInExtinf(extinf, groupTitle);

    // The same bytes as writing GroupKey(groupTitle, type), without building the key per entry.
    return TryFindGroupTitleValue(extinf, out var start, out var end)
      ? string.Concat(extinf.AsSpan(0, start), groupTitle, suffix, extinf.AsSpan(end))
      : EnsureGroupInExtinf(extinf, groupTitle, suffix);
  }

  private static string EnsureGroupInExtinf(string extinf, string groupTitle, string suffix = "")
  {
    if (HasGroupTitleAttribute(extinf)) return extinf;

    // Title and suffix go straight into the attribute, so no output key is built per entry.
    var attribute = string.Concat(" group-title=\"", groupTitle, suffix, "\"");
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
    private StringBuilder? _carry;
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

    /// <summary>
    /// A line inside the buffer becomes a string straight from its slice; only a line that crosses
    /// a buffer boundary is collected in the reused <see cref="_carry"/> builder.
    /// </summary>
    private async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
      var carried = 0;

      while (true)
      {
        if (_position >= _count)
        {
          var buffer = _buffer ?? throw new ObjectDisposedException(nameof(BoundedLineReader));
          _count = await _reader.ReadAsync(buffer.AsMemory(), cancellationToken);
          _position = 0;
          if (_count == 0)
          {
            return carried > 0 ? _carry!.ToString() : null;
          }
        }

        if (TryTakeLine(ref carried) is { } line) return line;
      }
    }

    /// <summary>Returns the line ending in the buffer, or carries the rest of the buffer over and returns null.</summary>
    private string? TryTakeLine(ref int carried)
    {
      var available = _buffer.AsSpan(_position, _count - _position);
      var end = available.IndexOfAny('\r', '\n');
      var segment = end < 0 ? available : available[..end];
      if (carried + segment.Length > _maxLineLengthChars)
      {
        throw new PlaylistLineTooLongException(_maxLineLengthChars);
      }

      if (end < 0)
      {
        if (carried == 0) (_carry ??= new StringBuilder(Math.Min(256, _maxLineLengthChars))).Clear();
        _carry!.Append(segment);
        carried += segment.Length;
        _position = _count;
        return null;
      }

      _position += end + 1;
      if (carried == 0)
      {
        return segment.Length == 0 ? string.Empty : new string(segment);
      }

      return _carry!.Append(segment).ToString();
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
/// <param name="HasTitle">False for the fallback group of entries without a group title.</param>
public sealed record PlaylistGroup(string Key, string GroupTitle, PlaylistContentType ContentType, int Count, bool HasTitle = true);

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

public sealed class PlaylistEntryOptionLinesLimitExceededException : InvalidOperationException
{
  public PlaylistEntryOptionLinesLimitExceededException(int maxOptionLines)
    : base($"Playlist contains a channel with more than {maxOptionLines:N0} option lines (such as #EXTVLCOPT or #KODIPROP) between #EXTINF and its address.")
  {
  }
}
