using System;

namespace Strim.Api.Contracts.Playlists;

public sealed class PlaylistChannelResponse
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string? GroupTitle { get; init; }

    public string? TvgId { get; init; }

    public string? TvgName { get; init; }

    public string? TvgLogo { get; init; }

    public int SortOrder { get; init; }
}
