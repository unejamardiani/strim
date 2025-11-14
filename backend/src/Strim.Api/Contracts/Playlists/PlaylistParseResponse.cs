using System.Collections.Generic;

namespace Strim.Api.Contracts.Playlists;

public sealed class PlaylistParseResponse
{
    public PlaylistSummaryResponse Playlist { get; init; } = new();

    public IReadOnlyCollection<PlaylistChannelResponse> Channels { get; init; } = new List<PlaylistChannelResponse>();
}
