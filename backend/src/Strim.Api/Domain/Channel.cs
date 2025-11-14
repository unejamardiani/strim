using System;

namespace Strim.Api.Domain;

public class Channel
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid PlaylistId { get; private set; }

    public string Name { get; private set; }

    public string? GroupTitle { get; private set; }

    public string? TvgId { get; private set; }

    public string? TvgName { get; private set; }

    public string? TvgLogo { get; private set; }

    public string Url { get; private set; }

    public string RawExtinf { get; private set; }

    public int SortOrder { get; private set; }

    public Playlist Playlist { get; private set; } = null!;

    private Channel()
    {
        Name = string.Empty;
        Url = string.Empty;
        RawExtinf = string.Empty;
    }

    private Channel(
        string name,
        string url,
        string rawExtinf,
        string? groupTitle,
        string? tvgId,
        string? tvgName,
        string? tvgLogo)
    {
        Name = name;
        Url = url;
        RawExtinf = rawExtinf;
        GroupTitle = groupTitle;
        TvgId = tvgId;
        TvgName = tvgName;
        TvgLogo = tvgLogo;
    }

    public static Channel Create(
        string name,
        string url,
        string rawExtinf,
        string? groupTitle,
        string? tvgId,
        string? tvgName,
        string? tvgLogo)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Channel name must be provided", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("Channel URL must be provided", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(rawExtinf))
        {
            throw new ArgumentException("Raw EXTINF data must be provided", nameof(rawExtinf));
        }

        return new Channel(name, url, rawExtinf, groupTitle, tvgId, tvgName, tvgLogo);
    }

    internal void AttachToPlaylist(Playlist playlist, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        Playlist = playlist;
        PlaylistId = playlist.Id;
        SortOrder = sortOrder;
    }
}
