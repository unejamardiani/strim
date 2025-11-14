using System.Collections.Generic;
using Strim.Api.Domain;

namespace Strim.Api.Application.Playlists;

public sealed class PlaylistIngestionResult
{
    public required Playlist Playlist { get; init; }

    public required IReadOnlyCollection<Channel> Channels { get; init; }
}
