using System;

namespace Strim.Api.Application.Playlists;

public class PlaylistParseException : Exception
{
    public PlaylistParseException(string message)
        : base(message)
    {
    }

    public PlaylistParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
