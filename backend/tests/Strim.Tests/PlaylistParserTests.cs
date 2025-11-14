using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Strim.Api.Application.Playlists;
using Strim.Api.Domain;
using Strim.Api.Infrastructure.Playlists;
using Xunit;

namespace Strim.Tests;

public class PlaylistParserTests
{
    private readonly IPlaylistParser _parser = new M3uPlaylistParser(NullLogger<M3uPlaylistParser>.Instance);

    [Fact]
    public async Task ParseAsync_PopulatesChannelsFromValidPlaylist()
    {
        const string payload = "#EXTM3U\n#EXTINF:-1 tvg-id=\"channel1\" tvg-name=\"Test Channel\" group-title=\"Group\" tvg-logo=\"logo.png\",Display\nhttp://example.com/stream.m3u8";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var playlist = Playlist.Create("Test", PlaylistSourceType.Url, "http://example.com/list.m3u");

        await _parser.ParseAsync(stream, playlist, CancellationToken.None);

        Assert.Single(playlist.Channels);
        var channel = Assert.Single(playlist.Channels);
        Assert.Equal("Test Channel", channel.Name);
        Assert.Equal("Group", channel.GroupTitle);
        Assert.Equal("channel1", channel.TvgId);
        Assert.Equal("http://example.com/stream.m3u8", channel.Url);
        Assert.Equal(0, channel.SortOrder);
    }

    [Fact]
    public async Task ParseAsync_ThrowsWhenHeaderMissing()
    {
        const string payload = "#EXTINF:-1,Channel\nhttp://example.com";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var playlist = Playlist.Create("Test", PlaylistSourceType.Url, "http://example.com/list.m3u");

        await Assert.ThrowsAsync<PlaylistParseException>(() => _parser.ParseAsync(stream, playlist, CancellationToken.None));
    }

    [Fact]
    public async Task ParseAsync_ThrowsWhenUrlMissing()
    {
        const string payload = "#EXTM3U\n#EXTINF:-1,Channel";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var playlist = Playlist.Create("Test", PlaylistSourceType.Url, "http://example.com/list.m3u");

        await Assert.ThrowsAsync<PlaylistParseException>(() => _parser.ParseAsync(stream, playlist, CancellationToken.None));
    }

    [Fact]
    public async Task ParseAsync_UsesUrlWhenDisplayNameMissing()
    {
        const string payload = "#EXTM3U\n#EXTINF:-1,http://example.com/stream\nhttp://example.com/stream";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var playlist = Playlist.Create("Test", PlaylistSourceType.Url, "http://example.com/list.m3u");

        await _parser.ParseAsync(stream, playlist, CancellationToken.None);

        var channel = Assert.Single(playlist.Channels);
        Assert.Equal("http://example.com/stream", channel.Name);
    }
}
