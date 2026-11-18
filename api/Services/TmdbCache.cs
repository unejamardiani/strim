namespace Api.Services;

/// <summary>
/// Process-local memory cache of successful TMDB answers, keyed by request path. Entries live for a fixed
/// lifetime (at most 30 days, well within TMDB's six-month limit) and are counted in bytes; past the budget
/// the oldest entries are evicted, and an answer larger than the whole budget is not cached at all.
/// Nothing is written to disk or to the database.
/// </summary>
public sealed class TmdbCache(TimeProvider timeProvider)
{
  private sealed record Entry(string Key, byte[] Body, DateTimeOffset ExpiresAt, long Size);

  private readonly object _gate = new();
  private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new(StringComparer.Ordinal);
  private readonly LinkedList<Entry> _byAge = new();

  /// <summary>The longest an answer is kept; a longer configured lifetime is clamped to it.</summary>
  public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);

  private readonly TimeSpan _lifetime = MaxLifetime;

  public TimeSpan Lifetime
  {
    get => _lifetime;
    init => _lifetime = value > MaxLifetime ? MaxLifetime : value;
  }

  public long ByteBudget { get; init; } = 16 * 1024 * 1024;

  public long Bytes
  {
    get { lock (_gate) { return _bytes; } }
  }

  private long _bytes;

  public bool TryGet(string key, out byte[] body)
  {
    lock (_gate)
    {
      if (_byKey.TryGetValue(key, out var node))
      {
        if (timeProvider.GetUtcNow() < node.Value.ExpiresAt)
        {
          body = node.Value.Body;
          return true;
        }
        Remove(node);
      }
    }
    body = [];
    return false;
  }

  public void Set(string key, byte[] body)
  {
    var size = SizeOf(key, body);
    lock (_gate)
    {
      if (_byKey.TryGetValue(key, out var existing))
      {
        Remove(existing);
      }
      if (size > ByteBudget)
      {
        return;
      }
      while (_bytes + size > ByteBudget && _byAge.First is { } oldest)
      {
        Remove(oldest);
      }
      var node = _byAge.AddLast(new Entry(key, body, timeProvider.GetUtcNow() + Lifetime, size));
      _byKey[key] = node;
      _bytes += size;
    }
  }

  /// <summary>The body plus the key's UTF-16 characters; bookkeeping overhead is not counted.</summary>
  internal static long SizeOf(string key, byte[] body) => body.LongLength + key.Length * sizeof(char);

  private void Remove(LinkedListNode<Entry> node)
  {
    _byAge.Remove(node);
    _byKey.Remove(node.Value.Key);
    _bytes -= node.Value.Size;
  }
}
