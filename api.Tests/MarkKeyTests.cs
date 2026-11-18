using Api.Services;
using Xunit;

namespace Api.Tests;

public class MarkKeyTests
{
  private static MarkKey Film(string name) => MarkKey.From(name, MarkKind.Film) ?? throw new Xunit.Sdk.XunitException($"No key for '{name}'");

  private static MarkKey Series(string name) => MarkKey.From(name, MarkKind.Series) ?? throw new Xunit.Sdk.XunitException($"No key for '{name}'");

  [Theory]
  [InlineData("DE - The Matrix (1999) 4K")]
  [InlineData("EN| The Matrix (1999) [FHD]")]
  [InlineData("|FR| The Matrix (1999) UHD")]
  [InlineData("The Matrix (1999) HD [Multi-Sub]")]
  [InlineData("The Matrix [1999] HEVC")]
  [InlineData("The Matrix 1999 H265 SD")]
  public void LanguagePrefixesQualityWordsAndBracketNoise_AreRemoved(string name)
  {
    var key = Film(name);

    Assert.Equal(Film("The Matrix (1999)").Key, key.Key);
    Assert.Equal("the matrix", key.Title);
    Assert.Equal(1999, key.Year);
    Assert.Equal("The Matrix", key.DisplayTitle);
    Assert.Equal(MarkKind.Film, key.Kind);
  }

  [Fact]
  public void Year_BelongsToTheKey()
  {
    Assert.NotEqual(Film("Dune (1984)").Key, Film("Dune (2021)").Key);
    Assert.Equal(1984, Film("Dune (1984)").Year);
    Assert.Equal(2021, Film("Dune 2021").Year);
    Assert.Equal(Film("Dune (2021)").Key, Film("Dune 2021").Key);
  }

  [Fact]
  public void NameWithoutYear_IsKeyedByTitleAlone()
  {
    var key = Film("DE - Dune");

    Assert.Null(key.Year);
    Assert.Equal("Dune", key.DisplayTitle);
    Assert.Equal("dune", key.Title);
    Assert.NotEqual(Film("Dune (2021)").Key, key.Key);
  }

  [Fact]
  public void YearAloneIsTheTitle_NotAYear()
  {
    var key = Film("1917");

    Assert.Equal("1917", key.Title);
    Assert.Null(key.Year);
  }

  [Theory]
  [InlineData("DE - Dark S01E01")]
  [InlineData("Dark S03E08 FHD")]
  [InlineData("Dark S1 E2")]
  [InlineData("Dark 1x02")]
  [InlineData("Dark - Season 2")]
  [InlineData("DE: Dark Staffel 2 Folge 3")]
  public void SeasonAndEpisodeMarkers_AreRemovedForSeries(string name)
  {
    var key = Series(name);

    Assert.Equal(Series("Dark").Key, key.Key);
    Assert.Equal("Dark", key.DisplayTitle);
    Assert.Equal(MarkKind.Series, key.Kind);
  }

  [Fact]
  public void SeriesMarkersOnly_GiveNoKey()
  {
    Assert.Null(MarkKey.From("S01E01", MarkKind.Series));
  }

  [Fact]
  public void Kind_IsPartOfTheKey()
  {
    Assert.NotEqual(Film("Dark").Key, Series("Dark").Key);
    Assert.Equal(Film("Dark").Title, Series("Dark").Title);
  }

  [Fact]
  public void CaseDiacriticsAndWhitespace_DoNotChangeTheKey()
  {
    var first = Film("Amélie   And   The  Café (2001)");
    var second = Film("AMELIE and the cafe (2001)");

    Assert.Equal(first.Key, second.Key);
    Assert.Equal("amelie and the cafe", first.Title);
    Assert.Equal("Amélie And The Café", first.DisplayTitle);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("4K")]
  [InlineData("DE - 4K")]
  [InlineData("[Multi-Sub] FHD")]
  [InlineData("EN| (2020)")]
  [InlineData("- | -")]
  public void EmptyOrNoiseOnlyName_GivesNoKey(string? name)
  {
    Assert.Null(MarkKey.From(name, MarkKind.Film));
    Assert.Null(MarkKey.From(name, MarkKind.Series));
  }

  [Fact]
  public void LongTitle_IsCutDeterministically()
  {
    var name = string.Join(' ', Enumerable.Repeat("Endless Story", 40)) + " (2005)";

    var first = Film(name);
    var second = Film(name);

    Assert.Equal(first.Key, second.Key);
    Assert.True(first.Title.Length <= MarkKey.MaxTitleLength);
    Assert.True(first.DisplayTitle.Length <= MarkKey.MaxTitleLength);
    Assert.True(first.Key.Length <= MarkKey.MaxKeyLength);
    Assert.Equal(2005, first.Year);
    Assert.StartsWith("endless story endless story", first.Title);
    Assert.Equal(first.Key, Film(name + " Extra Words Past The Cut").Key);
  }

  [Fact]
  public void LongTitle_PrecomposedAndDecomposed_GiveTheSameKey()
  {
    var name = string.Join(' ', Enumerable.Repeat("Café Nocturne", 30)) + " (2011)";

    var composed = Film(name.Normalize(System.Text.NormalizationForm.FormC));
    var decomposed = Film(name.Normalize(System.Text.NormalizationForm.FormD));

    Assert.Equal(composed.Key, decomposed.Key);
    Assert.Equal(composed.DisplayTitle, decomposed.DisplayTitle);
  }

  [Fact]
  public void ManyUnclosedBrackets_DoNotThrow()
  {
    var name = "Lantern " + new string('[', 50_000);

    var key = Film(name);

    Assert.StartsWith("lantern", key.Title);
  }

  [Fact]
  public void LowerCaseWordsAtTheStart_AreNotPrefixes()
  {
    Assert.Equal("Up", Film("Up (2009)").DisplayTitle);
    Assert.Equal("It Follows", Film("It Follows (2014)").DisplayTitle);
  }
}
