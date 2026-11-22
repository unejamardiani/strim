namespace Api.Services;

/// <summary>
/// The watchlist <see cref="PlaylistProcessor.GenerateFilteredFileAsync"/> copies into a generated group at the top
/// of the output. Built from the <see cref="MarkKey.Key"/> strings in watchlist order, newest watchlisted first;
/// a repeated key counts once, at its first place.
/// </summary>
public sealed class WatchlistGroup
{
  /// <summary>The group-title of the film copies.</summary>
  public const string FilmsTitle = "Watchlist";

  /// <summary>The group-title of the series copies, with the separator of <see cref="PlaylistProcessor.GroupKey"/>.</summary>
  public const string SeriesTitle = "Watchlist · Series";

  public const int MaxEntriesPerTitle = 500;
  public const int MaxEntries = 2_000;
  public const int MaxBufferBytes = 4 * 1024 * 1024;

  private readonly Dictionary<string, int> _ranks = new(StringComparer.Ordinal);

  public WatchlistGroup(IReadOnlyList<string> markKeys)
  {
    foreach (var key in markKeys)
    {
      if (!string.IsNullOrEmpty(key)) _ranks.TryAdd(key, _ranks.Count);
    }
  }

  public bool IsEmpty => _ranks.Count == 0;

  /// <summary>The key's place in the watchlist order (0 is the newest), or false when it is not on the list.</summary>
  internal bool TryGetRank(string key, out int rank) => _ranks.TryGetValue(key, out rank);

  /// <summary>
  /// The collected copies, bounded by <see cref="MaxEntriesPerTitle"/>, <see cref="MaxEntries"/> and
  /// <see cref="MaxBufferBytes"/>. When the buffer is full, the last entry of the key that comes last in the
  /// watchlist order is dropped first, so the newest watchlisted titles stay complete.
  /// </summary>
  internal sealed class Buffer(WatchlistGroup watchlist)
  {
    private readonly List<Entry>?[] _byRank = new List<Entry>?[watchlist._ranks.Count];
    private readonly HashSet<string> _addresses = new(StringComparer.Ordinal);
    private int _lastRank = -1;
    private int _entries;
    private long _bytes;

    public void Add(int rank, bool series, string address, string[] lines)
    {
      var entries = _byRank[rank] ??= [];
      if (entries.Count >= MaxEntriesPerTitle || !_addresses.Add(address)) return;

      var bytes = 0L;
      foreach (var line in lines) bytes += 2L * line.Length;
      while (_entries + 1 > MaxEntries || _bytes + bytes > MaxBufferBytes)
      {
        if (_lastRank <= rank)
        {
          _addresses.Remove(address);
          return;
        }
        RemoveLastOf(_lastRank);
      }

      entries.Add(new Entry(series, address, lines, bytes));
      _entries++;
      _bytes += bytes;
      _lastRank = Math.Max(_lastRank, rank);
    }

    /// <summary>Film copies first, then series copies; titles in watchlist order, entries in source order.</summary>
    public IEnumerable<string[]> Ordered() =>
      new[] { false, true }.SelectMany(series =>
        _byRank.SelectMany(entries => entries ?? []).Where(entry => entry.Series == series).Select(entry => entry.Lines));

    private void RemoveLastOf(int rank)
    {
      var entries = _byRank[rank]!;
      var last = entries[^1];
      entries.RemoveAt(entries.Count - 1);
      _addresses.Remove(last.Address);
      _entries--;
      _bytes -= last.Bytes;
      while (_lastRank >= 0 && (_byRank[_lastRank]?.Count ?? 0) == 0) _lastRank--;
    }

    private sealed record Entry(bool Series, string Address, string[] Lines, long Bytes);
  }
}
