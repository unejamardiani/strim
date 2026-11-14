namespace Api.Services;

/// <summary>One parsed playlist entry. <c>Index</c> is its 0-based position among the playlist's entries.</summary>
public sealed record PlaylistEntry(int Index, string Name, string GroupTitle, string? TvgId, string Address);

/// <summary>Entries that are duplicates of each other, in playlist order, and the distinct groups they sit in.</summary>
public sealed record DuplicateSet(IReadOnlyList<PlaylistEntry> Entries, IReadOnlyList<string> Groups);

/// <summary>
/// Finds duplicate entries. Two entries are duplicates when they share a non-empty tvg-id or the same
/// stream address; sets are joined transitively, so an entry appears in one set only.
/// Values are compared after trimming whitespace, case-sensitively and without any other normalisation.
/// Only reads: no entry and not the input list is changed.
/// </summary>
public static class DuplicateFinder
{
  public static IReadOnlyList<DuplicateSet> Find(IReadOnlyList<PlaylistEntry> entries)
  {
    var parent = new int[entries.Count];
    for (var i = 0; i < parent.Length; i++) parent[i] = i;

    var firstByTvgId = new Dictionary<string, int>(StringComparer.Ordinal);
    var firstByAddress = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var i = 0; i < entries.Count; i++)
    {
      Join(firstByTvgId, entries[i].TvgId, i, parent);
      Join(firstByAddress, entries[i].Address, i, parent);
    }

    var members = new Dictionary<int, List<PlaylistEntry>>();
    var order = new List<int>();
    for (var i = 0; i < entries.Count; i++)
    {
      var root = Root(parent, i);
      if (!members.TryGetValue(root, out var list))
      {
        members[root] = list = new List<PlaylistEntry>();
        order.Add(root);
      }
      list.Add(entries[i]);
    }

    var sets = new List<DuplicateSet>();
    foreach (var root in order)
    {
      var list = members[root];
      if (list.Count < 2) continue;
      var groups = list.Select(e => e.GroupTitle).Distinct(StringComparer.Ordinal).ToList();
      sets.Add(new DuplicateSet(list, groups));
    }
    return sets;
  }

  private static void Join(Dictionary<string, int> firstByKey, string? value, int index, int[] parent)
  {
    var key = value?.Trim();
    if (string.IsNullOrEmpty(key)) return;
    if (firstByKey.TryGetValue(key, out var first))
    {
      parent[Root(parent, index)] = Root(parent, first);
    }
    else
    {
      firstByKey[key] = index;
    }
  }

  private static int Root(int[] parent, int index)
  {
    while (parent[index] != index)
    {
      parent[index] = parent[parent[index]];
      index = parent[index];
    }
    return index;
  }
}
