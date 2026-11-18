namespace Api.Services;

/// <summary>Duplicate sets as entry indices, in the order of each set's first entry, and their counts.</summary>
public sealed record DuplicateIndexSets(IReadOnlyList<IReadOnlyList<int>> Sets, int SetCount, int EntryCount);

/// <summary>
/// Counts the bytes the streaming finders hold, so a caller can compare them with its budget. A caller may set
/// <see cref="Added"/> to check the total after each addition and stop a finder while it is still reading.
/// </summary>
public sealed class HeldBytesCounter
{
  public long Bytes { get; private set; }

  /// <summary>Called with the new total after each addition; an exception it throws stops the finder.</summary>
  internal Action<long>? Added { get; set; }

  internal void Add(long bytes)
  {
    Bytes += bytes;
    Added?.Invoke(Bytes);
  }
}

/// <summary>
/// Finds the same duplicate sets as <see cref="DuplicateFinder.Find"/> without holding the entries. The first pass
/// keeps a fingerprint per distinct trimmed tvg-id and address and flags entries whose fingerprint occurs more than
/// once. The second pass confirms each flagged entry on its real trimmed value, so a fingerprint collision never
/// joins entries. The source is read exactly <see cref="PassCount"/> times and must yield the same entries each time.
/// </summary>
public static class StreamingDuplicateFinder
{
  public const int PassCount = 2;

  private const byte TvgIdFlag = 1;
  private const byte AddressFlag = 2;
  // Conservative estimates of the 64-bit heap held, with room for the growth slack of lists and dictionaries.
  private const long PerEntryBytes = 16;   // parent index (4) and flags (1), tripled for a list resize in progress
  private const long PerKeyBytes = 64;     // one fingerprint dictionary slot (entry 24, bucket 4), doubled for growth
  private const long PerPrintBytes = 192;  // a confirmed fingerprint's dictionary slot, its list and a 4-slot array
  private const long PerValueBytes = 64;   // a value tuple slot (16, doubled) and the string's header and padding
  private const long PerGroupBytes = 160;  // a member list with a 4-slot array, its dictionary slot and order slot
  private const long PerMemberBytes = 8;   // one index in a member list, doubled for list growth
  private const long PerSetBytes = 16;     // one slot in the result list, doubled for growth

  public static async Task<DuplicateIndexSets> FindAsync(
    Func<CancellationToken, IAsyncEnumerable<PlaylistEntry>> source,
    CancellationToken cancellationToken,
    Func<string, ulong>? fingerprint = null,
    HeldBytesCounter? counter = null)
  {
    fingerprint ??= Fnv1a64;
    counter ??= new HeldBytesCounter();

    var parent = new List<int>();
    var flags = new List<byte>();
    var firstByTvgId = new Dictionary<ulong, int>();
    var firstByAddress = new Dictionary<ulong, int>();
    await foreach (var entry in source(cancellationToken).WithCancellation(cancellationToken))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var index = parent.Count;
      parent.Add(index);
      flags.Add(0);
      counter.Add(PerEntryBytes);
      Flag(firstByTvgId, entry.TvgId, TvgIdFlag, index, flags, fingerprint, counter);
      Flag(firstByAddress, entry.Address, AddressFlag, index, flags, fingerprint, counter);
    }
    firstByTvgId = firstByAddress = null; // Pass 2 confirms the flagged entries on their real values.

    var tvgIdValues = new Dictionary<ulong, List<(string Value, int First)>>();
    var addressValues = new Dictionary<ulong, List<(string Value, int First)>>();
    var position = 0;
    await foreach (var entry in source(cancellationToken).WithCancellation(cancellationToken))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (position >= parent.Count) throw new InvalidOperationException("The source yielded more entries than in the first pass.");
      if ((flags[position] & TvgIdFlag) != 0) Confirm(tvgIdValues, entry.TvgId!.Trim(), position, parent, fingerprint, counter);
      if ((flags[position] & AddressFlag) != 0) Confirm(addressValues, entry.Address.Trim(), position, parent, fingerprint, counter);
      position++;
    }
    if (position != parent.Count) throw new InvalidOperationException("The source yielded fewer entries than in the first pass.");

    var members = new Dictionary<int, List<int>>();
    var order = new List<int>();
    for (var i = 0; i < parent.Count; i++)
    {
      if (flags[i] == 0) continue; // never joined
      var root = Root(parent, i);
      if (!members.TryGetValue(root, out var list))
      {
        members[root] = list = new List<int>();
        order.Add(root);
        counter.Add(PerGroupBytes); // counted even when it stays a singleton and is dropped below
      }
      list.Add(i);
      counter.Add(PerMemberBytes);
    }

    var sets = new List<IReadOnlyList<int>>();
    var entryCount = 0;
    foreach (var root in order)
    {
      var list = members[root];
      if (list.Count < 2) continue;
      sets.Add(list);
      entryCount += list.Count;
      counter.Add(PerSetBytes);
    }
    return new DuplicateIndexSets(sets, sets.Count, entryCount);
  }

  private static void Flag(Dictionary<ulong, int> firstByKey, string? value, byte flag, int index, List<byte> flags, Func<string, ulong> fingerprint, HeldBytesCounter counter)
  {
    var key = value?.Trim();
    if (string.IsNullOrEmpty(key)) return;
    var print = fingerprint(key);
    if (firstByKey.TryGetValue(print, out var first))
    {
      flags[first] |= flag;
      flags[index] |= flag;
    }
    else
    {
      firstByKey[print] = index;
      counter.Add(PerKeyBytes);
    }
  }

  private static void Confirm(Dictionary<ulong, List<(string Value, int First)>> valuesByPrint, string value, int index, List<int> parent, Func<string, ulong> fingerprint, HeldBytesCounter counter)
  {
    var print = fingerprint(value);
    if (!valuesByPrint.TryGetValue(print, out var values))
    {
      valuesByPrint[print] = values = new List<(string, int)>();
      counter.Add(PerPrintBytes);
    }
    foreach (var (seen, first) in values)
    {
      if (string.Equals(seen, value, StringComparison.Ordinal))
      {
        parent[Root(parent, index)] = Root(parent, first);
        return;
      }
    }
    values.Add((value, index));
    counter.Add(PerValueBytes + 2L * value.Length);
  }

  private static int Root(List<int> parent, int index)
  {
    while (parent[index] != index)
    {
      parent[index] = parent[parent[index]];
      index = parent[index];
    }
    return index;
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
