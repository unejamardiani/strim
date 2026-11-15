using Api.Services;

namespace Api.Tests;

/// <summary>Reads a small test playlist through the same bounded file path the report endpoint uses.</summary>
internal static class PlaylistEntryFixture
{
  public static List<PlaylistEntry> Read(string playlist)
  {
    var path = Path.GetTempFileName();
    try
    {
      File.WriteAllText(path, playlist);
      return PlaylistProcessor.ReadEntriesFromFileAsync(path, long.MaxValue, CancellationToken.None)
        .GetAwaiter().GetResult();
    }
    finally
    {
      File.Delete(path);
    }
  }
}
