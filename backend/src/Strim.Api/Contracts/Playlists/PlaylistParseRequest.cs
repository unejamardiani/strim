namespace Strim.Api.Contracts.Playlists;

public sealed class PlaylistParseRequest
{
    public string? Url { get; init; }

    public string? FilePath { get; init; }

    public string? Name { get; init; }
}
