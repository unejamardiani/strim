using System.Text;

namespace Api.Services;

/// <summary>
/// A compact index of every entry, recorded while the source is analysed, so a search never reads
/// the playlist again. Each entry is one record "folded name \t display name \n" in fixed-size text
/// chunks plus three ints; group data is shared per (group title, content type). Nothing is copied
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
  private readonly Dictionary<(string, PlaylistContentType), int> _slotIds = new();
  private readonly StringBuilder _folded = new();

  internal PlaylistSearchIndex(long maxBytes) => _maxBytes = maxBytes;
  public int Count { get; private set; }
  /// <summary>Approximate heap size, as counted against <c>PlaylistCache:MaxSearchIndexBytes</c>.</summary>
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
    var known = _slotIds.TryGetValue((groupTitle, contentType), out var slotId);
    // A new slot keeps the title, its output group and its group key.
    EstimatedBytes += Fields * sizeof(int) + (length * sizeof(char))
      + (known ? 0 : SlotOverheadBytes + ((long)groupTitle.Length * sizeof(char) * 3));
    if (_maxBytes <= 0 || EstimatedBytes > _maxBytes) return false;

    if (!known)
    {
      slotId = _slots.Count;
      _slots.Add(new Slot(groupTitle, contentType));
      _slotIds[(groupTitle, contentType)] = slotId;
    }

    if (_chunks.Count == 0 || _chunkUsed[^1] + length > _chunks[^1].Length)
    {
      _chunks.Add(new char[Math.Max(ChunkChars, length)]);
      _chunkUsed.Add(0);
      _chunkFirstEntry.Add(Count);
    }
    if (Count % BlockEntries == 0) _blocks.Add(new int[BlockEntries * Fields]);
    var field = Count % BlockEntries * Fields;
    var used = _chunkUsed[^1];
    // Entries count 1, 2, 3 ... per (exact title, content type), the group a player shows; an
    // unsplit title has a single content type, so this equals counting per output group.
    (_blocks[^1][field], _blocks[^1][field + 1], _blocks[^1][field + 2]) = (used, slotId, ++_slots[slotId].Entries);
    Count++;

    var chunk = _chunks[^1].AsSpan(used);
    _folded.CopyTo(0, chunk, _folded.Length);
    chunk[_folded.Length] = '\t';
    name.CopyTo(chunk[(_folded.Length + 1)..]);
    chunk[length - 1] = '\n';
    _chunkUsed[^1] = used + length;
    return true;
  }

  /// <summary>Sets output groups and group keys from the finished analysis.</summary>
  internal PlaylistSearchIndex Complete(IReadOnlyList<PlaylistGroup> groups, IReadOnlySet<string> splitGroupTitles)
  {
    // Analysis groups titles case-insensitively, under the casing seen first.
    var keys = groups.GroupBy(g => g.GroupTitle, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
    foreach (var slot in _slots)
    {
      // GenerateFilteredFileAsync writes the entry's own title, plus a suffix for movies and series of a split title.
      if (slot.ContentType != PlaylistContentType.Live && splitGroupTitles.Contains(slot.GroupTitle))
      {
        slot.OutputGroup = PlaylistProcessor.GroupKey(slot.GroupTitle, slot.ContentType);
      }
      slot.GroupKey = keys[slot.GroupTitle].First(g => g.ContentType == slot.ContentType).Key;
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

  private sealed record Slot(string GroupTitle, PlaylistContentType ContentType)
  {
    public string OutputGroup { get; set; } = GroupTitle;
    public string GroupKey { get; set; } = GroupTitle;
    public int Entries { get; set; }
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
