using System;

namespace Strim.Api.Contracts.Playlists;

public sealed class PlaylistSummaryResponse
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public int ChannelCount { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
