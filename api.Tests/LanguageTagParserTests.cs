using Api.Services;
using Xunit;

namespace Api.Tests;

public class LanguageTagParserTests
{
  private static string[] Sorted(IReadOnlyDictionary<string, LanguageTagSource> set) =>
    set.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

  [Theory]
  [InlineData("DE: Film", "de")]
  [InlineData("EN | Film", "en")]
  [InlineData("FR - Film", "fr")]
  [InlineData("GER: Made Up Film", "de")]
  [InlineData("ITA|Made Up Film", "it")]
  public void PrefixCodeWithSeparator_GivesAudioLanguage(string name, string expected)
  {
    var result = LanguageTagParser.Parse(name, null);

    Assert.Equal(new[] { expected }, Sorted(result.AudioLanguages));
    Assert.Empty(result.SubtitleLanguages);
    Assert.Empty(result.Markers);
    Assert.Empty(result.PrefixTags);
  }

  [Fact]
  public void GroupPrefixCode_GivesAudioLanguageFromGroup()
  {
    var result = LanguageTagParser.Parse("Film", "FR | Films");

    Assert.Equal(new[] { "fr" }, Sorted(result.AudioLanguages));
    Assert.Equal(LanguageTagSource.Group, result.AudioLanguages["fr"]);
  }

  [Theory]
  [InlineData("Film [EN]", "en")]
  [InlineData("Film (DE)", "de")]
  [InlineData("Film ( FR ) 2020", "fr")]
  public void BracketedCode_GivesAudioLanguage(string name, string expected)
  {
    var result = LanguageTagParser.Parse(name, null);

    Assert.Equal(new[] { expected }, Sorted(result.AudioLanguages));
  }

  [Theory]
  [InlineData("Film MULTI")]
  [InlineData("MULTI: Film")]
  [InlineData("Film [MULTI]")]
  public void Multi_GivesMarkerAndNoLanguage(string name)
  {
    var result = LanguageTagParser.Parse(name, null);

    Assert.Equal(new[] { LanguageTagParser.Multi }, Sorted(result.Markers));
    Assert.Empty(result.AudioLanguages);
    Assert.Empty(result.SubtitleLanguages);
  }

  [Theory]
  [InlineData("Film VOSTFR")]
  [InlineData("VOSTFR: Film")]
  public void Vostfr_GivesFrenchSubtitlesAndNoAudio(string name)
  {
    var result = LanguageTagParser.Parse(name, null);

    Assert.Equal(new[] { "fr" }, Sorted(result.SubtitleLanguages));
    Assert.Empty(result.AudioLanguages);
  }

  [Theory]
  [InlineData("Film GER SUB")]
  [InlineData("Film DE SUB")]
  [InlineData("Film [DE SUB]")]
  [InlineData("DE SUB: Film")]
  public void CodeSub_GivesSubtitles(string name)
  {
    var result = LanguageTagParser.Parse(name, null);

    Assert.Equal(new[] { "de" }, Sorted(result.SubtitleLanguages));
    Assert.Empty(result.AudioLanguages);
  }

  [Theory]
  [InlineData("Den of Thieves")]
  [InlineData("Fresh")]
  [InlineData("Enola Holmes")]
  [InlineData("It")]
  [InlineData("Us")]
  [InlineData("DEADPOOL")]
  [InlineData("The Last of Us")]
  [InlineData("Frozen II")]
  [InlineData("Made Up Film")]
  [InlineData("DE Film")]
  [InlineData("UK Film")]
  [InlineData("Up: Film")]
  [InlineData("The - Film")]
  [InlineData("ABCDEF: Film")]
  [InlineData("DE2: Film")]
  [InlineData("Film (HD)")]
  [InlineData("Spider-Man: Made Up")]
  [InlineData("")]
  [InlineData(null)]
  public void OrdinaryNames_GiveEmptyResult(string? name)
  {
    Assert.True(LanguageTagParser.Parse(name, name).IsEmpty);
  }

  [Theory]
  [InlineData("UK: Film", "UK")]
  [InlineData("US | Film", "US")]
  [InlineData("EX-YU - Film", "EX-YU")]
  [InlineData("EX-YU: Film", "EX-YU")]
  [InlineData("TR - Film", "TR")]
  [InlineData("[UK] Film", "UK")]
  [InlineData("Film [UK]", "UK")]
  [InlineData("Film (EX-YU)", "EX-YU")]
  [InlineData("Made Up Film [UK] 2020", "UK")]
  public void CountryPrefix_GivesTagAsWrittenAndNoLanguage(string name, string expected)
  {
    var result = LanguageTagParser.Parse(name, null);

    Assert.Equal(new[] { expected }, Sorted(result.PrefixTags));
    Assert.Equal(LanguageTagSource.Name, result.PrefixTags[expected]);
    Assert.Empty(result.AudioLanguages);
    Assert.Empty(result.SubtitleLanguages);
    Assert.Empty(result.Markers);
  }

  [Fact]
  public void GroupCountryPrefix_GivesTagFromGroup()
  {
    var result = LanguageTagParser.Parse("Made Up Film", "AL | Filma");

    Assert.Equal(new[] { "AL" }, Sorted(result.PrefixTags));
    Assert.Equal(LanguageTagSource.Group, result.PrefixTags["AL"]);
    Assert.Empty(result.AudioLanguages);
  }

  [Fact]
  public void NameAndGroupTags_AreMergedWithoutDuplicatesAndKeepTheirSource()
  {
    var result = LanguageTagParser.Parse("UK: Film [EX-YU]", "UK | Films");

    Assert.Equal(new[] { "EX-YU", "UK" }, Sorted(result.PrefixTags));
    Assert.Equal(LanguageTagSource.Name | LanguageTagSource.Group, result.PrefixTags["UK"]);
    Assert.Equal(LanguageTagSource.Name, result.PrefixTags["EX-YU"]);
  }

  [Theory]
  [InlineData("DE: Film", "de")]
  [InlineData("Film [EN]", "en")]
  [InlineData("FR - Film", "fr")]
  public void LanguageCode_IsNeverAlsoATag(string name, string expected)
  {
    var result = LanguageTagParser.Parse(name, name);

    Assert.Equal(new[] { expected }, Sorted(result.AudioLanguages));
    Assert.Empty(result.PrefixTags);
  }

  [Theory]
  [InlineData("Up: Film")]
  [InlineData("The - Film")]
  [InlineData("ABCDEF: Film")]
  [InlineData("UK Film")]
  [InlineData("Film [HD]")]
  [InlineData("Film (FHD)")]
  [InlineData("Film [UHD]")]
  [InlineData("Film [4K]")]
  [InlineData("SD: Film")]
  [InlineData("HEVC | Film")]
  [InlineData("Film (2019)")]
  [InlineData("Film [A-B-C]")]
  [InlineData("Film [EX-]")]
  public void OrdinaryWordsAndQualityLabels_GiveNoTag(string name)
  {
    var result = LanguageTagParser.Parse(name, name);

    Assert.Empty(result.PrefixTags);
  }

  [Fact]
  public void PrefixCodeBeforeSeparator_IsAudioNotSubtitleMarker()
  {
    var result = LanguageTagParser.Parse("DE: SUB Orbit", null);

    Assert.Equal(new[] { "de" }, Sorted(result.AudioLanguages));
    Assert.Empty(result.SubtitleLanguages);
  }

  [Fact]
  public void SubtitleMarker_AcceptsAnyWhitespaceBetweenCodeAndSub()
  {
    var result = LanguageTagParser.Parse("Film DE\tSUB", null);

    Assert.Equal(new[] { "de" }, Sorted(result.SubtitleLanguages));
    Assert.Empty(result.AudioLanguages);
  }

  [Fact]
  public void NameAndGroup_AreMergedWithoutDuplicatesAndKeepTheirSource()
  {
    var result = LanguageTagParser.Parse("DE: Film [EN] MULTI", "DE | Films VOSTFR");

    Assert.Equal(new[] { "de", "en" }, Sorted(result.AudioLanguages));
    Assert.Equal(LanguageTagSource.Name | LanguageTagSource.Group, result.AudioLanguages["de"]);
    Assert.Equal(LanguageTagSource.Name, result.AudioLanguages["en"]);
    Assert.Equal(LanguageTagSource.Group, result.SubtitleLanguages["fr"]);
    Assert.Equal(LanguageTagSource.Name, result.Markers[LanguageTagParser.Multi]);
  }

  [Theory]
  [InlineData("XY: Film")]
  [InlineData("Film [XY]")]
  [InlineData("Film [UK]")]
  [InlineData("XY SUB: Film")]
  public void CodesOutsideTheList_AreNeverLanguages(string name)
  {
    var result = LanguageTagParser.Parse(name, null);
    Assert.Empty(result.AudioLanguages);
    Assert.Empty(result.SubtitleLanguages);
  }

  [Fact]
  public void LanguageCodes_AreAFixedListOfUpperCaseCodesMappedToIsoLanguages()
  {
    Assert.Contains("DE", LanguageTagParser.LanguageCodes.Keys);
    Assert.All(LanguageTagParser.LanguageCodes, pair =>
    {
      Assert.Matches("^[A-Z]{2,3}$", pair.Key);
      Assert.Matches("^[a-z]{2}$", pair.Value);
    });
    // Country prefixes stay tags, never languages (owner decision on #273).
    Assert.DoesNotContain("UK", LanguageTagParser.LanguageCodes.Keys);
    Assert.DoesNotContain("US", LanguageTagParser.LanguageCodes.Keys);
    Assert.DoesNotContain("TR", LanguageTagParser.LanguageCodes.Keys);
    Assert.DoesNotContain("AL", LanguageTagParser.LanguageCodes.Keys);
    Assert.DoesNotContain("AR", LanguageTagParser.LanguageCodes.Keys);
  }
}
