using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Xunit;

namespace Strim.Tests;

public class PlaylistIngestionApiTests : IClassFixture<PlaylistApplicationFactory>
{
    private readonly PlaylistApplicationFactory _factory;

    public PlaylistIngestionApiTests(PlaylistApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ParseEndpoint_PersistsPlaylistAndReturnsPreview()
    {
        const string payload = "#EXTM3U\n#EXTINF:-1 tvg-name=\"Channel One\",Channel One\nhttp://example.com/one";
        var tempFile = Path.Combine(Path.GetTempPath(), $"playlist-{Guid.NewGuid()}.m3u");
        await File.WriteAllTextAsync(tempFile, payload);

        try
        {
            using var client = _factory.CreateClient();

            var response = await client.PostAsJsonAsync("/api/playlists/parse", new { filePath = tempFile, name = "Local Test" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var parseResult = await response.Content.ReadFromJsonAsync<PlaylistParseResponseDto>();
            Assert.NotNull(parseResult);
            Assert.Equal("Local Test", parseResult!.Playlist.Name);
            Assert.Single(parseResult.Channels);
            Assert.Equal("Channel One", parseResult.Channels[0].Name);

            var playlists = await client.GetFromJsonAsync<List<PlaylistSummaryDto>>("/api/playlists");
            Assert.NotNull(playlists);
            var playlist = Assert.Single(playlists!);
            Assert.Equal("Local Test", playlist.Name);
            Assert.Equal(1, playlist.ChannelCount);
            Assert.Equal("FilePath", playlist.SourceType);
            Assert.Equal(tempFile, playlist.Source);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ParseEndpoint_ReturnsBadRequestWhenSourceMissing()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/playlists/parse", new { name = "No Source" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(error);
        Assert.True(error!.ContainsKey("error"));
    }

    private sealed record PlaylistParseResponseDto(PlaylistSummaryDto Playlist, IReadOnlyList<PlaylistChannelDto> Channels);

    private sealed record PlaylistSummaryDto(Guid Id, string Name, string Source, string SourceType, int ChannelCount, DateTimeOffset CreatedAt);

    private sealed record PlaylistChannelDto(Guid Id, string Name, string Url, string? GroupTitle, string? TvgId, string? TvgName, string? TvgLogo, int SortOrder);
}
