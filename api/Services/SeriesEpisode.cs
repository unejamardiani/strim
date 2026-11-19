using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Api.Services;

/// <summary>Reads the season and episode of a series entry from its name. Pure.</summary>
public static class SeriesEpisode
{
  /// <summary>
  /// The first season or episode marker in <paramref name="entryName"/>: the forms <see cref="MarkKey"/> cuts
  /// (S01E02, S1 E2, S01, 1x02, Season 2, Staffel 2) plus an episode after a season word ("Staffel 2 Folge 5",
  /// "Season 2 Episode 5"). Both numbers are null without a marker, or when the name cannot be read in time.
  /// </summary>
  public static (int? Season, int? Episode) Parse(string? entryName)
  {
    if (string.IsNullOrWhiteSpace(entryName)) return (null, null);
    try
    {
      var match = Marker.Match(entryName.Normalize(NormalizationForm.FormC));
      if (!match.Success) return (null, null);
      return (Number(match.Groups["s"]), Number(match.Groups["e"]));
    }
    catch (RegexMatchTimeoutException)
    {
      // Entry names are untrusted; a name the pattern cannot read in time has no numbers rather than an error.
      return (null, null);
    }
  }

  private static int? Number(Group group) =>
    group.Success ? int.Parse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture) : null;

  // ASCII digits only, so every number parses; bounded white space keeps the pattern linear on long runs of spaces.
  private static readonly Regex Marker = new(
    @"(?<![\p{L}\p{N}])(?:S(?<s>[0-9]{1,3})\s{0,3}E(?<e>[0-9]{1,4})|(?<s>[0-9]{1,3})x(?<e>[0-9]{1,4})"
    + @"|(?:Season|Staffel|Saison|Temporada)\s{0,3}(?<s>[0-9]{1,3})"
    + @"(?:\s{0,3}[-,|]?\s{0,3}(?:Episode|Épisode|Episodio|Folge|Ep|E)\.?\s{0,3}(?<e>[0-9]{1,4}))?"
    + @"|S(?<s>[0-9]{1,3}))(?![\p{L}\p{N}])",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
