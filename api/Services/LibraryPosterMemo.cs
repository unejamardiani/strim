namespace Api.Services;

/// <summary>
/// Process-local memory of the posters found by searching TMDB, by mark key: the TMDB poster path, or null for "no
/// poster". Entries live for 30 days (within TMDB's six-month limit); past <see cref="MaxEntries"/> the oldest go
/// first. Matches from a user's stored TMDB id are never kept here, because they are per user. Nothing is written to
/// disk or to the database.
/// </summary>
public sealed class LibraryPosterMemo(TimeProvider timeProvider)
{
  private sealed record Entry(string Key, string? PosterPath, DateTimeOffset ExpiresAt);
  private readonly object _gate = new();
  private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new(StringComparer.Ordinal);
  private readonly LinkedList<Entry> _byAge = new();
  public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
  public int MaxEntries { get; init; } = 20_000;
  public int Count { get { lock (_gate) { return _byKey.Count; } } }

  /// <summary>True when the key is remembered; <paramref name="posterPath"/> is null for "no poster".</summary>
  public bool TryGet(string key, out string? posterPath)
  {
    posterPath = null;
    lock (_gate)
    {
      if (!_byKey.TryGetValue(key, out var node)) return false;
      var alive = timeProvider.GetUtcNow() < node.Value.ExpiresAt;
      if (alive) posterPath = node.Value.PosterPath;
      else Remove(node);
      return alive;
    }
  }

  public void Set(string key, string? posterPath)
  {
    lock (_gate)
    {
      if (_byKey.TryGetValue(key, out var existing)) Remove(existing);
      while (_byKey.Count >= MaxEntries && _byAge.First is { } oldest) Remove(oldest);
      _byKey[key] = _byAge.AddLast(new Entry(key, posterPath, timeProvider.GetUtcNow() + Lifetime));
    }
  }

  private void Remove(LinkedListNode<Entry> node)
  {
    _byAge.Remove(node);
    _byKey.Remove(node.Value.Key);
  }
}
