using System;
using System.Collections.Generic;

namespace Strim.Api.Domain;

public enum PlaylistSourceType
{
    Url = 1,
    FilePath = 2
}

public class Playlist
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public string Name { get; private set; }

    public PlaylistSourceType SourceType { get; private set; }

    public string Source { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private readonly List<Channel> _channels = new();

    public IReadOnlyCollection<Channel> Channels => _channels;

    private Playlist()
    {
        Name = string.Empty;
        Source = string.Empty;
    }

    private Playlist(string name, PlaylistSourceType sourceType, string source)
    {
        Name = name;
        SourceType = sourceType;
        Source = source;
    }

    public static Playlist Create(string name, PlaylistSourceType sourceType, string source)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Playlist name must be provided", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Playlist source must be provided", nameof(source));
        }

        return new Playlist(name, sourceType, source);
    }

    public void AddChannel(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        channel.AttachToPlaylist(this, _channels.Count);
        _channels.Add(channel);
    }
}
