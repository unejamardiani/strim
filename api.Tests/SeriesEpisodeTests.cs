using System.Diagnostics;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class SeriesEpisodeTests
{
  [Theory]
  [InlineData("Dark S01E01", 1, 1)]
  [InlineData("DE - Dark S03E08 FHD", 3, 8)]
  [InlineData("Dark s2e10", 2, 10)]
  [InlineData("Dark S1 E2", 1, 2)]
  [InlineData("Dark 1x02", 1, 2)]
  [InlineData("Dark S03", 3, null)]
  [InlineData("Dark - Season 2", 2, null)]
  [InlineData("Dark Staffel 2", 2, null)]
  [InlineData("DE: Dark Staffel 2 Folge 5", 2, 5)]
  [InlineData("Dark Season 2 Episode 5", 2, 5)]
  [InlineData("Dark Season 2 - Episode 5", 2, 5)]
  [InlineData("Dark Saison 1 Épisode 4", 1, 4)]
  [InlineData("Dark Temporada 3 Episodio 7", 3, 7)]
  [InlineData("Dark Season 2 Ep. 6", 2, 6)]
  [InlineData("Dark S00E01 Special", 0, 1)]
  public void ReadsMarkerForms(string name, int? season, int? episode)
  {
    Assert.Equal((season, episode), SeriesEpisode.Parse(name));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("Dark")]
  [InlineData("Dark (2017)")]
  [InlineData("Area 51")]
  [InlineData("Dark 1080x720")]
  [InlineData("Seasons Of Love")]
  [InlineData("Dark S01E01x")]
  [InlineData("Dark S١E٢")]
  public void NoMarker_NoNumbers(string? name)
  {
    Assert.Equal(((int?)null, (int?)null), SeriesEpisode.Parse(name));
  }

  [Fact]
  public void HostileName_NoTimeout()
  {
    var names = new[]
    {
      string.Concat(Enumerable.Repeat("Season ", 20_000)) + "x",
      string.Concat(Enumerable.Repeat("S1 ", 30_000)) + "E",
      "Dark " + new string('1', 50_000) + "x" + new string('2', 50_000),
      "Staffel 2" + new string(' ', 50_000) + "Folge",
    };

    var watch = Stopwatch.StartNew();
    foreach (var name in names)
    {
      var (season, episode) = SeriesEpisode.Parse(name);
      Assert.Null(episode);
      Assert.True(season is null or >= 0);
    }
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Parsing took {watch.Elapsed}");
  }
}
