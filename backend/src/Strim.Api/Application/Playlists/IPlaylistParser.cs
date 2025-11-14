using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Strim.Api.Domain;

namespace Strim.Api.Application.Playlists;

public interface IPlaylistParser
{
    Task ParseAsync(Stream stream, Playlist playlist, CancellationToken cancellationToken);
}
