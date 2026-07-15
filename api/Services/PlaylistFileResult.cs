using Microsoft.AspNetCore.Http;

namespace Api.Services;

/// <summary>Streams a leased cache file to the client and releases/deletes it when complete.</summary>
public sealed class PlaylistFileResult : IResult
{
  private PlaylistFileLease? _lease;
  private readonly string _contentType;
  private readonly bool _download;

  public PlaylistFileResult(PlaylistFileLease lease, string contentType, bool download = true)
  {
    _lease = lease;
    _contentType = contentType;
    _download = download;
  }

  public async Task ExecuteAsync(HttpContext httpContext)
  {
    var lease = Interlocked.Exchange(ref _lease, null);
    if (lease is null)
    {
      throw new InvalidOperationException("Playlist file result has already been executed.");
    }

    try
    {
      var response = httpContext.Response;
      response.ContentType = _contentType;
      // Output keys are short-lived bearer capabilities and shared playlists can contain private
      // provider URLs. Do not let browsers or intermediary proxies retain the response.
      response.Headers["Cache-Control"] = "private, no-store, max-age=0";
      if (_download && !string.IsNullOrWhiteSpace(lease.FileName))
      {
        var safeName = PlaylistFileCache.SanitizeFileName(lease.FileName);
        response.Headers["Content-Disposition"] = $"attachment; filename=\"{safeName}\"; filename*=UTF-8''{Uri.EscapeDataString(safeName)}";
      }

      await using var file = new FileStream(
        lease.FilePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
      response.ContentLength = file.Length;
      await file.CopyToAsync(response.Body, 128 * 1024, httpContext.RequestAborted);
    }
    finally
    {
      await lease.DisposeAsync();
    }
  }
}
