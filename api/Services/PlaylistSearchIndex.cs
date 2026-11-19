using System.Text;

namespace Api.Services;

/// <summary>
/// A compact index of every entry, recorded while the source is analysed, so a search never reads
/// the playlist again. Each entry is one record "folded name \t display name \n" in fixed-size text
/// chunks plus four ints; group data is shared per (group title, content type, written title) and language tags
/// per distinct combination, so an entry pays one int for its tags. Nothing is copied or resized as the index grows.
/// </summary>
public sealed class PlaylistSearchIndex
{
  // Below the large object heap threshold; a longer record gets a chunk of its own.
  private const int ChunkChars = 32 * 1024;
  private const int BlockEntries = 4 * 1024;
  // Per entry: record offset in its chunk, slot, 1-based position in its output group, language combination.
  private const int Fields = 4;
  private const int SlotOverheadBytes = 128;
  // A string object without its characters, plus the array slot that refers to it.
  private const int ValueOverheadBytes = 40;

  private readonly long _maxBytes;
  private readonly List<char[]> _chunks = new();
  private readonly List<int> _chunkUsed = new();
  private readonly List<int> _chunkFirstEntry = new();
  private readonly List<int[]> _blocks = new();
  private readonly List<Slot> _slots = new();
  private readonly Dictionary<(string, PlaylistContentType, string, bool), int> _slotIds = new();
  private readonly StringBuilder _folded = new();
  // Combination 0 is "no tags", so an untagged entry needs no table row.
  private readonly List<PlaylistSearchLanguages> _languages = new() { PlaylistSearchLanguages.None };
  private readonly Dictionary<string, int> _languageIds = new(StringComparer.Ordinal);
  // Group title to its combination in the shared table, so the group cache keeps no copy of its own.
  private readonly Dictionary<string, int> _groupLanguages = new(StringComparer.Ordinal);

  internal PlaylistSearchIndex(long maxBytes) => _maxBytes = maxBytes;
  public int Count { get; private set; }
  /// <summary>
  /// Approximate heap size including the unused capacity of allocated chunks and blocks, as counted
  /// against <c>PlaylistCache:MaxSearchIndexBytes</c>.
  /// </summary>
  public long EstimatedBytes { get; private set; }

  /// <summary>
  /// Entries whose folded name contains the folded query, in source order: at most
  /// <paramref name="limit"/>, with <c>HasMore</c> when further matches exist. With
  /// <paramref name="hideDisabled"/>, matches in a disabled group take no slot and are counted in
  /// <c>HiddenCount</c> instead, which counts every such match, also those past the limit. With
  /// <paramref name="contentTypes"/> or <paramref name="languages"/> not empty, matches of other types, or without
  /// one of the languages or prefix tags (compared case-insensitively), are skipped before anything else.
  /// <c>Total</c> counts every match that is neither skipped nor hidden, also those past the limit.
  /// <paramref name="markFilter"/> (the Library's marks) runs last: it skips a match or hides it as seen, counted in
  /// <c>SeenHiddenCount</c>.
  /// </summary>
  public PlaylistSearchResult Search(
    string? query, ISet<string> disabledGroups, int limit, bool hideDisabled = false,
    IReadOnlySet<PlaylistContentType>? contentTypes = null, IReadOnlyCollection<string>? languages = null,
    Func<string, PlaylistContentType, MarkVerdict>? markFilter = null)
  {
    var folded = PlaylistSearchText.Fold(query).AsSpan();
    var matches = new List<PlaylistSearchMatch>();
    var hidden = 0;
    var seenHidden = 0;
    var total = 0;
    if (folded.IsEmpty || limit <= 0) return new PlaylistSearchResult(matches, false);
    Interlocked.Increment(ref _passCount);
    // Decided once per combination in the shared table, so the scan tests one bool per entry.
    var wanted = languages is { Count: > 0 } ? new HashSet<string>(languages, StringComparer.OrdinalIgnoreCase) : null;
    var keep = wanted is null ? null : _languages.Select(l => l.FilterValues().Any(wanted.Contains)).ToArray();

    for (var c = 0; c < _chunks.Count; c++)
    {
      var text = _chunks[c].AsSpan(0, _chunkUsed[c]);
      var first = _chunkFirstEntry[c];
      var end = c + 1 < _chunks.Count ? _chunkFirstEntry[c + 1] : Count;
      var at = 0;
      while (at < text.Length && text[at..].IndexOf(folded) is var hit and >= 0)
      {
        hit += at;
        var entry = EntryAt(first, end, hit);
        var start = Field(entry, 0);
        var tab = start + text[start..].IndexOf('\t');
        at = entry + 1 < end ? Field(entry + 1, 0) : text.Length;
        // The search resumes at a record start, so a first hit past the tab lies in the display name.
        if (hit + folded.Length > tab) continue;

        var slot = _slots[Field(entry, 1)];
        if (contentTypes is { Count: > 0 } && !contentTypes.Contains(slot.ContentType)) continue;
        if (keep is not null && !keep[Field(entry, 3)]) continue;
        var enabled = !PlaylistProcessor.IsDisabled(disabledGroups, slot.GroupTitle, slot.ContentType);
        if (hideDisabled && !enabled)
        {
          hidden++;
          continue;
        }
        var name = markFilter is null ? null : new string(text[(tab + 1)..(at - 1)]);
        var verdict = name is null ? MarkVerdict.Keep : markFilter!(name, slot.ContentType);
        if (verdict == MarkVerdict.HideSeen) seenHidden++;
        if (verdict != MarkVerdict.Keep) continue;
        // Matches past the cap are still counted, so Total and HiddenCount stay complete.
        if (total++ >= limit) continue;

        matches.Add(new PlaylistSearchMatch(
          name ?? new string(text[(tab + 1)..(at - 1)]), slot.OutputGroup, slot.GroupKey, slot.ContentType, Field(entry, 2), enabled)
        {
          Languages = _languages[Field(entry, 3)],
        });
      }
    }

    return new PlaylistSearchResult(matches, total > limit, hidden) { Total = total, SeenHiddenCount = seenHidden };
  }

  /// <summary>Rarer prefix tags still filter but are not offered, so odd prefixes do not clutter the choice.</summary>
  public const int MinPrefixTagEntries = 20;

  /// <summary>
  /// The languages and prefix tags the language filter accepts, with the number of entries carrying each, most
  /// frequent first. A prefix tag is listed only from <see cref="MinPrefixTagEntries"/> entries on. Set by Complete.
  /// </summary>
  public IReadOnlyList<PlaylistSearchLanguageCount> LanguageCounts { get; private set; } = [];

  /// <summary>
  /// The display names of the entries under <paramref name="groupKey"/> in source order, skipping
  /// <paramref name="offset"/> and returning at most <paramref name="limit"/>, with the group's total.
  /// Only the page's names are copied; the rest of the group is counted. With <paramref name="markFilter"/>, every
  /// name is read, and entries it skips or hides as seen take no place in the offset, the page or the total.
  /// </summary>
  public PlaylistGroupEntriesResult GroupEntries(
    string groupKey, int offset, int limit, Func<string, PlaylistContentType, MarkVerdict>? markFilter = null)
  {
    var names = new List<string>();
    var types = new List<PlaylistContentType>();
    var groups = new List<string>();
    var seenHidden = 0;
    var inGroup = _slots.Select(s => string.Equals(s.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (!inGroup.Contains(true)) return new PlaylistGroupEntriesResult(names, 0);
    Interlocked.Increment(ref _passCount);

    var total = 0;
    for (var c = 0; c < _chunks.Count; c++)
    {
      var end = c + 1 < _chunks.Count ? _chunkFirstEntry[c + 1] : Count;
      for (var entry = _chunkFirstEntry[c]; entry < end; entry++)
      {
        if (!inGroup[Field(entry, 1)]) continue;
        var type = _slots[Field(entry, 1)].ContentType;
        var name = markFilter is null ? null : NameAt(c, entry);
        var verdict = name is null ? MarkVerdict.Keep : markFilter!(name, type);
        if (verdict == MarkVerdict.HideSeen) seenHidden++;
        if (verdict != MarkVerdict.Keep) continue;
        if (total++ < offset || names.Count >= limit) continue;

        names.Add(name ?? NameAt(c, entry));
        types.Add(type);
        groups.Add(_slots[Field(entry, 1)].OutputGroup);
      }
    }

    return new PlaylistGroupEntriesResult(names, total) { Types = types, Groups = groups, SeenHiddenCount = seenHidden };
  }

  /// <summary>Whether <paramref name="groupKey"/> names a group of the list that <paramref name="disabledGroups"/> leaves on.</summary>
  public bool IsVisibleGroup(string groupKey, ISet<string> disabledGroups) =>
    _slots.Any(s => string.Equals(s.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase)
      && !PlaylistProcessor.IsDisabled(disabledGroups, s.GroupTitle, s.ContentType));

  /// <summary>Scans of the whole index (searches, group pages, film lookups) since it was built.</summary>
  public int PassCount => _passCount;
  private int _passCount;

  /// <summary>
  /// For each wanted film mark key, the first film entry in source order whose <see cref="MarkKey"/> equals it,
  /// preferring an entry in an enabled group, in one pass for all keys. Adult groups are never matched; with
  /// <paramref name="hideDisabled"/>, neither are disabled groups.
  /// </summary>
  public IReadOnlyDictionary<string, PlaylistSearchMatch> FindFilms(
    IReadOnlySet<string> keys, ISet<string> disabledGroups, bool hideDisabled)
  {
    var found = new Dictionary<string, PlaylistSearchMatch>(StringComparer.Ordinal);
    if (keys.Count == 0) return found;
    Interlocked.Increment(ref _passCount);
    var enabled = _slots.Select(s => !PlaylistProcessor.IsDisabled(disabledGroups, s.GroupTitle, s.ContentType)).ToArray();
    var usable = _slots.Select((s, i) => s.ContentType == PlaylistContentType.Movies
      && !AdultGroupRule.IsAdultGroupTitle(s.GroupTitle) && (enabled[i] || !hideDisabled)).ToArray();
    var settled = 0;
    for (var c = 0; c < _chunks.Count && settled < keys.Count; c++)
    {
      var end = c + 1 < _chunks.Count ? _chunkFirstEntry[c + 1] : Count;
      for (var entry = _chunkFirstEntry[c]; entry < end && settled < keys.Count; entry++)
      {
        var slotId = Field(entry, 1);
        if (!usable[slotId]) continue;
        var name = NameAt(c, entry);
        if (MarkKey.From(name, MarkKind.Film)?.Key is not { } key || !keys.Contains(key)) continue;
        if (found.TryGetValue(key, out var known) && (known.Enabled || !enabled[slotId])) continue;
        var slot = _slots[slotId];
        found[key] = new PlaylistSearchMatch(name, slot.OutputGroup, slot.GroupKey, slot.ContentType, Field(entry, 2), enabled[slotId]);
        if (enabled[slotId]) settled++;
      }
    }
    return found;
  }

  /// <summary>
  /// In one pass: the seasons and episodes of every series entry whose series <see cref="MarkKey"/> equals
  /// <paramref name="seriesKey"/>, read with <see cref="SeriesEpisode"/> and distinct across groups and variants, and
  /// for each of <paramref name="similarKeys"/> the first series entry carrying it, preferring an enabled group.
  /// Films, channels and adult groups are never read; with <paramref name="hideDisabled"/>, neither are disabled groups.
  /// </summary>
  public PlaylistSeriesResult FindSeries(
    string seriesKey, IReadOnlySet<string> similarKeys, ISet<string> disabledGroups, bool hideDisabled)
  {
    var seasons = new SortedDictionary<int, SortedSet<int>>();
    var unnumbered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var similar = new Dictionary<string, PlaylistSearchMatch>(StringComparer.Ordinal);
    Interlocked.Increment(ref _passCount);
    var enabled = _slots.Select(s => !PlaylistProcessor.IsDisabled(disabledGroups, s.GroupTitle, s.ContentType)).ToArray();
    var usable = _slots.Select((s, i) => s.ContentType == PlaylistContentType.Series
      && !AdultGroupRule.IsAdultGroupTitle(s.GroupTitle) && (enabled[i] || !hideDisabled)).ToArray();
    for (var c = 0; c < _chunks.Count; c++)
    {
      var end = c + 1 < _chunks.Count ? _chunkFirstEntry[c + 1] : Count;
      for (var entry = _chunkFirstEntry[c]; entry < end; entry++)
      {
        var slotId = Field(entry, 1);
        if (!usable[slotId]) continue;
        var name = NameAt(c, entry);
        if (MarkKey.From(name, MarkKind.Series)?.Key is not { } key) continue;
        if (key == seriesKey)
        {
          var (season, episode) = SeriesEpisode.Parse(name);
          if (season is { } number && !seasons.ContainsKey(number)) seasons[number] = new SortedSet<int>();
          // An entry without an episode number (a season pack, a special) counts once per name across groups.
          if (episode is { } e) seasons[season!.Value].Add(e);
          else unnumbered.Add(name);
        }
        else if (similarKeys.Contains(key) && !(similar.TryGetValue(key, out var known) && (known.Enabled || !enabled[slotId])))
        {
          var slot = _slots[slotId];
          similar[key] = new PlaylistSearchMatch(name, slot.OutputGroup, slot.GroupKey, slot.ContentType, Field(entry, 2), enabled[slotId]);
        }
      }
    }
    return new PlaylistSeriesResult(seasons.ToDictionary(s => s.Key, s => (IReadOnlyCollection<int>)s.Value), unnumbered.Count, similar);
  }

  private string NameAt(int chunk, int entry)
  {
    var text = _chunks[chunk].AsSpan(0, _chunkUsed[chunk]);
    var start = Field(entry, 0);
    var name = text[(start + text[start..].IndexOf('\t') + 1)..];
    return new string(name[..name.IndexOf('\n')]);
  }

  /// <summary>Records one entry; false once the index exceeds its budget and should be dropped.</summary>
  internal bool TryAdd(string extinfLine, string groupTitle, PlaylistContentType contentType)
  {
    var name = PlaylistSearchText.DisplayNameSpan(extinfLine);
    _folded.Clear();
    PlaylistSearchText.AppendFolded(_folded, name);
    var length = _folded.Length + name.Length + 2;
    var written = PlaylistProcessor.WrittenGroupTitle(extinfLine, groupTitle, out var keepsLine);
    var slotKey = (groupTitle, contentType, written, keepsLine);
    var known = _slotIds.TryGetValue(slotKey, out var slotId);
    var newChunk = _chunks.Count == 0 || _chunkUsed[^1] + length > _chunks[^1].Length;
    var newBlock = Count % BlockEntries == 0;
    // A group title is parsed once into the shared table; a name only when it may hold a code.
    var knownGroup = _groupLanguages.TryGetValue(groupTitle, out var groupLanguageId);
    var (groupTags, groupKey) = (PlaylistSearchLanguages.None, "");
    if (!knownGroup) groupTags = PlaylistSearchLanguages.From(LanguageTagParser.Parse(null, groupTitle), out groupKey);
    var (languages, languageKey) = (groupTags, groupKey);
    var nameTagged = MayHoldCode(name);
    if (nameTagged)
    {
      languages = PlaylistSearchLanguages.From(LanguageTagParser.Parse(name.ToString(), groupTitle), out languageKey);
    }
    // Counts what is allocated, whole chunks and blocks, not only the characters and fields in use.
    // A new slot keeps the title, its output group, its group key and a written title that differs.
    var writtenChars = ReferenceEquals(written, groupTitle) ? 0 : written.Length;
    EstimatedBytes += (newChunk ? (long)Math.Max(ChunkChars, length) * sizeof(char) : 0)
      + (newBlock ? BlockEntries * Fields * sizeof(int) : 0)
      + (known ? 0 : SlotOverheadBytes + (((long)groupTitle.Length * 3) + writtenChars) * sizeof(char))
      + (knownGroup ? 0 : SlotOverheadBytes + CombinationBytes(groupTags, groupKey))
      + (nameTagged && languageKey != groupKey ? CombinationBytes(languages, languageKey) : 0);
    if (_maxBytes <= 0 || EstimatedBytes > _maxBytes) return false;

    if (!known)
    {
      slotId = _slots.Count;
      _slots.Add(new Slot(groupTitle, contentType, written, keepsLine));
      _slotIds[slotKey] = slotId;
    }

    if (!knownGroup) _groupLanguages[groupTitle] = groupLanguageId = Intern(groupTags, groupKey);
    var languageId = nameTagged ? Intern(languages, languageKey) : groupLanguageId;

    if (newChunk)
    {
      _chunks.Add(new char[Math.Max(ChunkChars, length)]);
      _chunkUsed.Add(0);
      _chunkFirstEntry.Add(Count);
    }
    if (newBlock) _blocks.Add(new int[BlockEntries * Fields]);
    var field = Count % BlockEntries * Fields;
    var used = _chunkUsed[^1];
    // The position is set by Complete, once the output groups are known.
    (_blocks[^1][field], _blocks[^1][field + 1], _blocks[^1][field + 3]) = (used, slotId, languageId);
    Count++;

    var chunk = _chunks[^1].AsSpan(used);
    _folded.CopyTo(0, chunk, _folded.Length);
    chunk[_folded.Length] = '\t';
    name.CopyTo(chunk[(_folded.Length + 1)..]);
    chunk[length - 1] = '\n';
    _chunkUsed[^1] = used + length;
    return true;
  }

  /// <summary>Sets output groups, group keys and positions from the finished analysis.</summary>
  internal PlaylistSearchIndex Complete(IReadOnlyList<PlaylistGroup> groups, IReadOnlySet<string> splitGroupTitles)
  {
    // Analysis groups titles case-insensitively, under the casing seen first.
    var keys = groups.GroupBy(g => g.GroupTitle, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
    var outputIds = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var slot in _slots)
    {
      // GenerateFilteredFileAsync writes the entry's own title, plus a suffix for movies and series of a split title.
      var split = slot.ContentType != PlaylistContentType.Live && splitGroupTitles.Contains(slot.GroupTitle);
      slot.OutputGroup = split && !slot.KeepsLine
        ? PlaylistProcessor.GroupKey(slot.GroupTitle, slot.ContentType)
        : slot.WrittenTitle;
      slot.GroupKey = keys[slot.GroupTitle].First(g => g.ContentType == slot.ContentType).Key;
      if (!outputIds.TryGetValue(slot.OutputGroup, out var outputId))
      {
        outputId = outputIds.Count;
        outputIds[slot.OutputGroup] = outputId;
      }
      slot.OutputId = outputId;
    }

    // Entries count 1, 2, 3 ... in source order across every entry the file writes under the same group.
    var positions = new int[outputIds.Count];
    for (var entry = 0; entry < Count; entry++)
    {
      _blocks[entry / BlockEntries][(entry % BlockEntries * Fields) + 2] = ++positions[_slots[Field(entry, 1)].OutputId];
    }

    // Entries per combination first, so each value sums a few table rows instead of reading every entry again.
    var perCombination = new int[_languages.Count];
    for (var entry = 0; entry < Count; entry++) perCombination[Field(entry, 3)]++;
    var counts = new Dictionary<string, (bool Tag, int Count)>(StringComparer.Ordinal);
    for (var id = 1; id < _languages.Count; id++)
    {
      foreach (var value in _languages[id].FilterValues())
      {
        counts[value] = (_languages[id].PrefixTags.Contains(value), counts.GetValueOrDefault(value).Count + perCombination[id]);
      }
    }
    LanguageCounts = counts.Where(c => !c.Value.Tag || c.Value.Count >= MinPrefixTagEntries)
      .OrderByDescending(c => c.Value.Count).ThenBy(c => c.Key, StringComparer.Ordinal)
      .Select(c => new PlaylistSearchLanguageCount(c.Key, c.Value.Tag ? "tag" : "language", c.Value.Count))
      .ToList();
    return this;
  }

  /// <summary>
  /// The bytes a combination not yet in the table keeps: the record, its four arrays and table row, the key,
  /// and one string and array reference per value (whose characters the key already counts).
  /// </summary>
  private long CombinationBytes(PlaylistSearchLanguages tags, string key)
  {
    if (key.Length == 0 || _languageIds.ContainsKey(key)) return 0;
    var values = tags.Audio.Count + tags.Subtitles.Count + tags.Markers.Count + tags.PrefixTags.Count;
    return (2 * SlotOverheadBytes) + ((long)key.Length * 2 * sizeof(char)) + ((long)values * ValueOverheadBytes);
  }

  private int Intern(PlaylistSearchLanguages tags, string key)
  {
    if (key.Length == 0) return 0;
    if (_languageIds.TryGetValue(key, out var id)) return id;
    id = _languages.Count;
    _languages.Add(tags);
    _languageIds[key] = id;
    return id;
  }

  // Every code holds two upper-case letters, adjacent or around one hyphen (EX-YU, A-B).
  private static bool MayHoldCode(ReadOnlySpan<char> name)
  {
    for (var i = 1; i < name.Length; i++)
    {
      if (!char.IsAsciiLetterUpper(name[i])) continue;
      if (char.IsAsciiLetterUpper(name[i - 1])) return true;
      if (i >= 2 && name[i - 1] == '-' && char.IsAsciiLetterUpper(name[i - 2])) return true;
    }
    return false;
  }

  private int Field(int entry, int field) => _blocks[entry / BlockEntries][(entry % BlockEntries * Fields) + field];

  /// <summary>The last entry in [first, end) whose record starts at or before the offset.</summary>
  private int EntryAt(int first, int end, int offset)
  {
    int low = first, high = end - 1;
    while (low < high)
    {
      var middle = low + ((high - low + 1) / 2);
      if (Field(middle, 0) <= offset) low = middle;
      else high = middle - 1;
    }
    return low;
  }

  /// <param name="WrittenTitle">The group-title the generated file writes when the title is not split.</param>
  /// <param name="KeepsLine">The line is written unchanged even when the title is split.</param>
  private sealed record Slot(string GroupTitle, PlaylistContentType ContentType, string WrittenTitle, bool KeepsLine)
  {
    public string OutputGroup { get; set; } = WrittenTitle;
    public string GroupKey { get; set; } = GroupTitle;
    public int OutputId { get; set; }
  }
}

/// <param name="HiddenCount">Matches left out because their group is disabled; 0 unless they are hidden.</param>
public sealed record PlaylistSearchResult(IReadOnlyList<PlaylistSearchMatch> Matches, bool HasMore, int HiddenCount = 0)
{
  /// <summary>Every match that passed the filters and is not hidden, also those past the limit.</summary>
  public int Total { get; init; }

  /// <summary>Matches the mark filter hid as seen, also those past the limit.</summary>
  public int SeenHiddenCount { get; init; }
}

/// <summary>What a mark filter does with an entry: keep it, skip it, or hide it as seen (counted).</summary>
public enum MarkVerdict { Keep, Skip, HideSeen }

/// <param name="Value">An ISO 639-1 language (de) or a prefix tag as written (UK, EX-YU).</param>
/// <param name="Kind"><c>language</c> or <c>tag</c>.</param>
/// <param name="Count">Entries of the whole playlist that carry the value.</param>
public sealed record PlaylistSearchLanguageCount(string Value, string Kind, int Count);

/// <param name="Names">The display names of one page of the group's entries, in source order.</param>
/// <param name="Total">The number of entries in the whole group.</param>
public sealed record PlaylistGroupEntriesResult(IReadOnlyList<string> Names, int Total)
{
  /// <summary>The content type of each name.</summary>
  public IReadOnlyList<PlaylistContentType> Types { get; init; } = [];

  /// <summary>The group-title the generated file writes for each name.</summary>
  public IReadOnlyList<string> Groups { get; init; } = [];

  /// <summary>Entries of the group the mark filter hid as seen.</summary>
  public int SeenHiddenCount { get; init; }
}

/// <param name="Seasons">Each season the list holds, with its distinct episode numbers in ascending order.</param>
/// <param name="Unnumbered">Distinct names of the series without an episode number.</param>
/// <param name="Similar">For each similar series key found, its first entry (in an enabled group when there is one).</param>
public sealed record PlaylistSeriesResult(
  IReadOnlyDictionary<int, IReadOnlyCollection<int>> Seasons, int Unnumbered,
  IReadOnlyDictionary<string, PlaylistSearchMatch> Similar)
{
  /// <summary>Distinct numbered episodes across every season.</summary>
  public int Episodes => Seasons.Values.Sum(s => s.Count);
}

/// <param name="Name">The entry's display name.</param>
/// <param name="OutputGroup">The group-title the generated file writes for the entry.</param>
/// <param name="GroupKey">The group key the editor toggles.</param>
/// <param name="Position">1-based position within <paramref name="OutputGroup"/> in source order.</param>
/// <param name="Enabled">False when the plain group title or the group key is disabled.</param>
public sealed record PlaylistSearchMatch(
  string Name, string OutputGroup, string GroupKey, PlaylistContentType ContentType, int Position, bool Enabled)
{
  public PlaylistSearchLanguages Languages { get; init; } = PlaylistSearchLanguages.None;
}

/// <summary>
/// One shared combination of the tags <see cref="LanguageTagParser"/> read from an entry name and its group title.
/// <see cref="Source"/> is <c>title</c> when any tag was read, null when nothing is known (never a guess).
/// </summary>
public sealed record PlaylistSearchLanguages(
  IReadOnlyList<string> Audio, IReadOnlyList<string> Subtitles, IReadOnlyList<string> Markers,
  IReadOnlyList<string> PrefixTags, string? Source)
{
  public const string TitleSource = "title";
  public static readonly PlaylistSearchLanguages None = new([], [], [], [], null);

  /// <summary>The combination of <paramref name="tags"/> and its table key; the key is empty when there are no tags.</summary>
  internal static PlaylistSearchLanguages From(LanguageTags tags, out string key)
  {
    if (tags.IsEmpty)
    {
      key = "";
      return None;
    }

    var result = new PlaylistSearchLanguages(
      Sorted(tags.AudioLanguages), Sorted(tags.SubtitleLanguages), Sorted(tags.Markers), Sorted(tags.PrefixTags), TitleSource);
    key = string.Join('|', string.Join(',', result.Audio), string.Join(',', result.Subtitles),
      string.Join(',', result.Markers), string.Join(',', result.PrefixTags));
    return result;
  }

  /// <summary>The distinct values the language filter matches: audio and subtitle languages and prefix tags.</summary>
  internal IEnumerable<string> FilterValues() => Audio.Concat(Subtitles).Concat(PrefixTags).Distinct(StringComparer.Ordinal);

  private static string[] Sorted(IReadOnlyDictionary<string, LanguageTagSource> set) =>
    set.Keys.Order(StringComparer.Ordinal).ToArray();
}
