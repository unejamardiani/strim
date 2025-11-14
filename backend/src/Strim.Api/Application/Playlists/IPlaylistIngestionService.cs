using System.Threading;
using System.Threading.Tasks;
using Strim.Api.Domain;

namespace Strim.Api.Application.Playlists;

public interface IPlaylistIngestionService
{
    Task<PlaylistIngestionResult> IngestAsync(string source, PlaylistSourceType sourceType, string? name, CancellationToken cancellationToken);
}
