using System.Text;

namespace Api.Services;

/// <summary>
/// A compact index of every entry, recorded while the source is analysed, so a search never reads
/// the playlist again. Each entry is one record "folded name \t display name \n" in fixed-size text
/// chunks plus three ints; group data is shared per (group title, content type, written title). Nothing is copied
/// or resized as the index grows.
/// </summary>
public sealed class PlaylistSearchIndex
{
  // Below the large object heap threshold; a longer record gets a chunk of its own.
  private const int ChunkChars = 32 * 1024;
  private const int BlockEntries = 4 * 1024;
  // Per entry: record offset in its chunk, slot, 1-based position in its output group.
  private const int Fields = 3;
  private const int SlotOverheadBytes = 128;

  private readonly long _maxBytes;
  private readonly List<char[]> _chunks = new();
  private readonly List<int> _chunkUsed = new();
  private readonly List<int> _chunkFirstEntry = new();
  private readonly List<int[]> _blocks = new();
  private readonly List<Slot> _slots = new();
  private readonly Dictionary<(string, PlaylistContentType, string, bool), int> _slotIds = new();
  private readonly StringBuilder _folded = new();

  internal PlaylistSearchIndex(long maxBytes) => _maxBytes = maxBytes;
  public int Count { get; private set; }
  /// <summary>
  /// Approximate heap size including the unused capacity of allocated chunks and blocks, as counted
  /// against <c>PlaylistCache:MaxSearchIndexBytes</c>.
  /// </summary>
  public long EstimatedBytes { get; private set; }

  /// <summary>
  /// Entries whose folded name contains the folded query, in source order: at most
  /// <paramref name="limit"/>, with <c>HasMore</c> when further matches exist.
  /// </summary>
  public PlaylistSearchResult Search(string? query, ISet<string> disabledGroups, int limit)
  {
    var folded = PlaylistSearchText.Fold(query).AsSpan();
    var matches = new List<PlaylistSearchMatch>();
    if (folded.IsEmpty || limit <= 0) return new PlaylistSearchResult(matches, false);

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
        if (matches.Count == limit) return new PlaylistSearchResult(matches, true);

        var slot = _slots[Field(entry, 1)];
        var enabled = !PlaylistProcessor.IsDisabled(disabledGroups, slot.GroupTitle, slot.ContentType);
        matches.Add(new PlaylistSearchMatch(
          new string(text[(tab + 1)..(at - 1)]), slot.OutputGroup, slot.GroupKey, slot.ContentType, Field(entry, 2), enabled));
      }
    }

    return new PlaylistSearchResult(matches, false);
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
    // Counts what is allocated, whole chunks and blocks, not only the characters and fields in use.
    // A new slot keeps the title, its output group, its group key and a written title that differs.
    var writtenChars = ReferenceEquals(written, groupTitle) ? 0 : written.Length;
    EstimatedBytes += (newChunk ? (long)Math.Max(ChunkChars, length) * sizeof(char) : 0)
      + (newBlock ? BlockEntries * Fields * sizeof(int) : 0)
      + (known ? 0 : SlotOverheadBytes + (((long)groupTitle.Length * 3) + writtenChars) * sizeof(char));
    if (_maxBytes <= 0 || EstimatedBytes > _maxBytes) return false;

    if (!known)
    {
      slotId = _slots.Count;
      _slots.Add(new Slot(groupTitle, contentType, written, keepsLine));
      _slotIds[slotKey] = slotId;
    }

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
    (_blocks[^1][field], _blocks[^1][field + 1]) = (used, slotId);
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
    return this;
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

public sealed record PlaylistSearchResult(IReadOnlyList<PlaylistSearchMatch> Matches, bool HasMore);

/// <param name="Name">The entry's display name.</param>
/// <param name="OutputGroup">The group-title the generated file writes for the entry.</param>
/// <param name="GroupKey">The group key the editor toggles.</param>
/// <param name="Position">1-based position within <paramref name="OutputGroup"/> in source order.</param>
/// <param name="Enabled">False when the plain group title or the group key is disabled.</param>
public sealed record PlaylistSearchMatch(
  string Name, string OutputGroup, string GroupKey, PlaylistContentType ContentType, int Position, bool Enabled);
