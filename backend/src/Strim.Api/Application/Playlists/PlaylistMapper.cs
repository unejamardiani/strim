using System.Collections.Generic;
using System.Linq;
using Strim.Api.Contracts.Playlists;
using Strim.Api.Domain;

namespace Strim.Api.Application.Playlists;

public static class PlaylistMapper
{
    public static PlaylistSummaryResponse ToSummary(Playlist playlist) => new()
    {
        Id = playlist.Id,
        Name = playlist.Name,
        Source = playlist.Source,
        SourceType = playlist.SourceType.ToString(),
        ChannelCount = playlist.Channels.Count,
        CreatedAt = playlist.CreatedAt
    };

    public static IReadOnlyCollection<PlaylistChannelResponse> ToChannelResponses(IEnumerable<Channel> channels)
    {
        return channels
            .OrderBy(c => c.SortOrder)
            .Select(c => new PlaylistChannelResponse
            {
                Id = c.Id,
                Name = c.Name,
                Url = c.Url,
                GroupTitle = c.GroupTitle,
                TvgId = c.TvgId,
                TvgName = c.TvgName,
                TvgLogo = c.TvgLogo,
                SortOrder = c.SortOrder
            })
            .ToList();
    }
}
