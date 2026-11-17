namespace Api.Services;

/// <summary>A variant group as its base name and the indices of its members, in playlist order.</summary>
public sealed record VariantIndexGroup(string BaseName, IReadOnlyList<int> Members);

/// <summary>
/// Variant groups in the order of the first occurrence of their base name, their counts and the bytes the finder
/// held while finding them, so a caller can compare them with its budget.
/// </summary>
public sealed record VariantIndexGroups(IReadOnlyList<VariantIndexGroup> Groups, int GroupCount, int EntryCount, long HeldBytes);

/// <summary>
/// Finds the same variant groups as <see cref="VariantFinder.Find"/> without holding the entries. The first pass runs
/// <see cref="VariantFinder.Detect"/> per entry and keeps a fingerprint slot per entry, and per fingerprint a count and
/// whether a member has a quality word. The second pass confirms the base names of the entries whose fingerprint can
/// still form a group on the real string, so entries with different base names are never grouped, even under the same
/// fingerprint. The source is read exactly <see cref="PassCount"/> times and must yield the same entries each time.
/// </summary>
public static class StreamingVariantFinder
{
  public const int PassCount = 2;

  // Estimates of the heap held, with room for the growth slack of lists and dictionaries.
  private const long PerEntryBytes = 8;       // the entry's fingerprint slot (4), doubled for list growth
  private const long PerPrintBytes = 80;      // a fingerprint dictionary slot (about 32) and its count and flag, doubled
  private const long PerBaseNameBytes = 160;  // a confirmed base name's holder, member list, string overhead and slots
  private const long PerMemberBytes = 8;      // one index in a member list, doubled for list growth
  private const long PerGroupBytes = 16;      // one slot in the result list, doubled for growth

  public static async Task<VariantIndexGroups> FindAsync(
    Func<CancellationToken, IAsyncEnumerable<PlaylistEntry>> source,
    CancellationToken cancellationToken,
    Func<string, ulong>? fingerprint = null)
  {
    fingerprint ??= Fnv1a64;
    long held = 0;

    // Pass 1: a fingerprint slot per entry (-1 for an empty base name, which never groups).
    var slots = new List<int>();
    var counts = new List<int>();
    var hasQuality = new List<bool>();
    var slotByPrint = new Dictionary<ulong, int>();
    await foreach (var entry in source(cancellationToken).WithCancellation(cancellationToken))
    {
      cancellationToken.ThrowIfCancellationRequested();
      held += PerEntryBytes;
      var (baseName, words) = VariantFinder.Detect(entry.Name);
      if (baseName.Length == 0)
      {
        slots.Add(-1);
        continue;
      }
      var print = fingerprint(baseName);
      if (!slotByPrint.TryGetValue(print, out var slot))
      {
        slotByPrint[print] = slot = counts.Count;
        counts.Add(0);
        hasQuality.Add(false);
        held += PerPrintBytes;
      }
      slots.Add(slot);
      counts[slot]++;
      if (words.Count > 0) hasQuality[slot] = true;
    }
    slotByPrint = null; // Pass 2 confirms the candidates on their real base names.

    // Pass 2: entries under a fingerprint with at least two entries and a quality word, grouped by the real string.
    var byBaseName = new Dictionary<string, Candidate>(StringComparer.Ordinal);
    var order = new List<Candidate>();
    var position = 0;
    await foreach (var entry in source(cancellationToken).WithCancellation(cancellationToken))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (position >= slots.Count) throw new InvalidOperationException("The source yielded more entries than in the first pass.");
      var slot = slots[position];
      if (slot >= 0 && counts[slot] >= 2 && hasQuality[slot])
      {
        var (baseName, words) = VariantFinder.Detect(entry.Name);
        if (!byBaseName.TryGetValue(baseName, out var candidate))
        {
          byBaseName[baseName] = candidate = new Candidate(baseName);
          order.Add(candidate);
          held += PerBaseNameBytes + 2L * baseName.Length;
        }
        candidate.Members.Add(position);
        candidate.HasQuality |= words.Count > 0;
        held += PerMemberBytes;
      }
      position++;
    }
    if (position != slots.Count) throw new InvalidOperationException("The source yielded fewer entries than in the first pass.");

    var groups = new List<VariantIndexGroup>();
    var entryCount = 0;
    foreach (var candidate in order)
    {
      if (candidate.Members.Count < 2 || !candidate.HasQuality) continue;
      groups.Add(new VariantIndexGroup(candidate.BaseName, candidate.Members));
      entryCount += candidate.Members.Count;
      held += PerGroupBytes;
    }
    return new VariantIndexGroups(groups, groups.Count, entryCount, held);
  }

  private sealed class Candidate(string baseName)
  {
    public string BaseName { get; } = baseName;

    public List<int> Members { get; } = new();

    public bool HasQuality { get; set; }
  }

  /// <summary>64-bit FNV-1a over the UTF-16 code units.</summary>
  internal static ulong Fnv1a64(string value)
  {
    var hash = 14695981039346656037UL;
    foreach (var c in value)
    {
      hash = (hash ^ c) * 1099511628211UL;
    }
    return hash;
  }
}
