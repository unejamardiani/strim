using Microsoft.Extensions.Options;

namespace Api.Services;

/// <summary>
/// Keeps heavyweight playlist operations from multiplying memory and CPU usage under load.
/// </summary>
public sealed class PlaylistJobGate
{
  private readonly SemaphoreSlim _semaphore;
  private readonly TimeSpan _queueTimeout;

  public PlaylistJobGate(IOptions<PlaylistCacheOptions> options)
  {
    var value = options.Value;
    _semaphore = new SemaphoreSlim(Math.Max(1, value.MaxConcurrentJobs));
    _queueTimeout = TimeSpan.FromSeconds(Math.Max(1, value.QueueTimeoutSeconds));
  }

  public async ValueTask<PlaylistJobLease?> TryEnterAsync(CancellationToken cancellationToken)
  {
    if (!await _semaphore.WaitAsync(_queueTimeout, cancellationToken))
    {
      return null;
    }

    return new PlaylistJobLease(_semaphore);
  }
}

public sealed class PlaylistJobLease : IDisposable, IAsyncDisposable
{
  private SemaphoreSlim? _semaphore;

  internal PlaylistJobLease(SemaphoreSlim semaphore) => _semaphore = semaphore;

  public void Dispose()
  {
    Interlocked.Exchange(ref _semaphore, null)?.Release();
  }

  public ValueTask DisposeAsync()
  {
    Dispose();
    return ValueTask.CompletedTask;
  }
}
