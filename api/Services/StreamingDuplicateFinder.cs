namespace Api.Services;

/// <summary>Duplicate sets as entry indices, in the order of each set's first entry, and their counts.</summary>
public sealed record DuplicateIndexSets(IReadOnlyList<IReadOnlyList<int>> Sets, int SetCount, int EntryCount);

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

  public static async Task<DuplicateIndexSets> FindAsync(
    Func<CancellationToken, IAsyncEnumerable<PlaylistEntry>> source,
    CancellationToken cancellationToken,
    Func<string, ulong>? fingerprint = null)
  {
    fingerprint ??= Fnv1a64;

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
      Flag(firstByTvgId, entry.TvgId, TvgIdFlag, index, flags, fingerprint);
      Flag(firstByAddress, entry.Address, AddressFlag, index, flags, fingerprint);
    }
    firstByTvgId = firstByAddress = null; // Pass 2 confirms the flagged entries on their real values.

    var tvgIdValues = new Dictionary<ulong, List<(string Value, int First)>>();
    var addressValues = new Dictionary<ulong, List<(string Value, int First)>>();
    var position = 0;
    await foreach (var entry in source(cancellationToken).WithCancellation(cancellationToken))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (position >= parent.Count) throw new InvalidOperationException("The source yielded more entries than in the first pass.");
      if ((flags[position] & TvgIdFlag) != 0) Confirm(tvgIdValues, entry.TvgId!.Trim(), position, parent, fingerprint);
      if ((flags[position] & AddressFlag) != 0) Confirm(addressValues, entry.Address.Trim(), position, parent, fingerprint);
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
      }
      list.Add(i);
    }

    var sets = new List<IReadOnlyList<int>>();
    var entryCount = 0;
    foreach (var root in order)
    {
      var list = members[root];
      if (list.Count < 2) continue;
      sets.Add(list);
      entryCount += list.Count;
    }
    return new DuplicateIndexSets(sets, sets.Count, entryCount);
  }

  private static void Flag(Dictionary<ulong, int> firstByKey, string? value, byte flag, int index, List<byte> flags, Func<string, ulong> fingerprint)
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
    }
  }

  private static void Confirm(Dictionary<ulong, List<(string Value, int First)>> valuesByPrint, string value, int index, List<int> parent, Func<string, ulong> fingerprint)
  {
    var print = fingerprint(value);
    if (!valuesByPrint.TryGetValue(print, out var values)) valuesByPrint[print] = values = new List<(string, int)>();
    foreach (var (seen, first) in values)
    {
      if (string.Equals(seen, value, StringComparison.Ordinal))
      {
        parent[Root(parent, index)] = Root(parent, first);
        return;
      }
    }
    values.Add((value, index));
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
