using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Strim.Api.Application.Playlists;
using Strim.Api.Data;
using Strim.Api.Domain;

namespace Strim.Api.Infrastructure.Playlists;

public class PlaylistIngestionService(
    StrimDbContext dbContext,
    IPlaylistParser playlistParser,
    IHttpClientFactory httpClientFactory,
    ILogger<PlaylistIngestionService> logger) : IPlaylistIngestionService
{
    public async Task<PlaylistIngestionResult> IngestAsync(string source, PlaylistSourceType sourceType, string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Playlist source must be provided", nameof(source));
        }

        source = source.Trim();

        var playlistName = !string.IsNullOrWhiteSpace(name) ? name!.Trim() : DeriveNameFromSource(source, sourceType);
        if (playlistName.Length > 200)
        {
            playlistName = playlistName[..200];
        }

        logger.LogInformation("Ingesting playlist from {SourceType} source {Source}", sourceType, source);

        var playlist = Playlist.Create(playlistName, sourceType, source);

        await using var stream = await RetrieveStreamAsync(source, sourceType, cancellationToken).ConfigureAwait(false);

        try
        {
            await playlistParser.ParseAsync(stream, playlist, cancellationToken).ConfigureAwait(false);
        }
        catch (PlaylistParseException)
        {
            logger.LogWarning("Playlist parsing failed for {SourceType} source {Source}", sourceType, source);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while parsing playlist from {SourceType} source {Source}", sourceType, source);
            throw new PlaylistParseException("An unexpected error occurred while parsing the playlist", ex);
        }

        if (playlist.Channels.Count == 0)
        {
            throw new PlaylistParseException("Playlist did not contain any channels");
        }

        logger.LogInformation("Persisting playlist {PlaylistName} from {SourceType} with {ChannelCount} channels", playlist.Name, sourceType, playlist.Channels.Count);

        await dbContext.Playlists.AddAsync(playlist, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new PlaylistIngestionResult
        {
            Playlist = playlist,
            Channels = playlist.Channels
        };
    }

    private async Task<Stream> RetrieveStreamAsync(string source, PlaylistSourceType sourceType, CancellationToken cancellationToken)
    {
        return sourceType switch
        {
            PlaylistSourceType.Url => await FetchFromUrlAsync(source, cancellationToken).ConfigureAwait(false),
            PlaylistSourceType.FilePath => await FetchFromFileAsync(source).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceType), sourceType, "Unsupported source type")
        };
    }

    private async Task<Stream> FetchFromUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new PlaylistParseException("Only HTTP(S) playlist URLs are supported");
        }

        var client = httpClientFactory.CreateClient("playlist");
        logger.LogDebug("Downloading playlist from {PlaylistUrl}", uri);
        var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new PlaylistParseException($"Failed to download playlist: {response.StatusCode}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResponseStream(stream, response);
    }

    private Task<Stream> FetchFromFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            throw new PlaylistParseException($"Playlist file was not found at path '{path}'");
        }

        logger.LogDebug("Opening playlist from file path {FilePath}", path);

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    private static string DeriveNameFromSource(string source, PlaylistSourceType sourceType)
    {
        if (sourceType == PlaylistSourceType.Url && Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
            return string.IsNullOrWhiteSpace(lastSegment) ? uri.Host : lastSegment!;
        }

        if (sourceType == PlaylistSourceType.FilePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(source);
            return string.IsNullOrWhiteSpace(fileName) ? source : fileName;
        }

        return source;
    }
}

file sealed class HttpResponseStream : Stream
{
    private readonly Stream _inner;
    private readonly HttpResponseMessage _response;

    public HttpResponseStream(Stream inner, HttpResponseMessage response)
    {
        _inner = inner;
        _response = response;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanTimeout => _inner.CanTimeout;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
    public override int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _response.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
