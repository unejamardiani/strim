using Api.Services;
using Xunit;

namespace Api.Tests;

public class PlaylistSearchTextTests
{
  [Theory]
  [InlineData("#EXTINF:-1 tvg-id=\"x.de\" group-title=\"DE | Kino\",  Das Erste HD  ", "Das Erste HD")]
  [InlineData("#EXTINF:-1,Plain Name", "Plain Name")]
  [InlineData("#EXTINF:-1 group-title=\"News\",First,Second", "Second")]
  public void DisplayName_UsesTextAfterLastComma(string line, string expected)
  {
    Assert.Equal(expected, PlaylistSearchText.DisplayName(line));
  }

  [Theory]
  [InlineData("#EXTINF:-1 tvg-name=\" Arte DE \" group-title=\"Doku\",", "Arte DE")]
  [InlineData("#EXTINF:-1 tvg-name=\"Arte DE\" group-title=\"Doku\",   ", "Arte DE")]
  [InlineData("#EXTINF:-1 group-title=\"Doku\",", "")]
  [InlineData("#EXTINF:-1 my-tvg-name=\"Other\",", "")]
  [InlineData("#EXTINF:-1", "")]
  [InlineData("", "")]
  [InlineData(null, "")]
  public void DisplayName_FallsBackToTvgName(string? line, string expected)
  {
    Assert.Equal(expected, PlaylistSearchText.DisplayName(line));
  }

  [Theory]
  [InlineData("#EXTINF:-1 tvg-name=\"A, B\" group-title=\"X, Y\",A B Channel", "A B Channel")]
  [InlineData("#EXTINF:-1 tvg-name=\"A, B\",", "A, B")]
  public void DisplayName_IgnoresCommaInsideAttribute(string line, string expected)
  {
    Assert.Equal(expected, PlaylistSearchText.DisplayName(line));
  }

  [Theory]
  [InlineData("#EXTINF:-1 group-title=\"tvg-name=\" tvg-name=\"Actual\",", "Actual")]
  [InlineData("#EXTINF:-1 group-title=\"tvg-name=\",", "")]
  [InlineData("#EXTINF:-1 group-title=\"a tvg-name=\" tvg-id=\"b\",", "")]
  public void DisplayName_IgnoresTvgNameInsideAnotherValue(string line, string expected)
  {
    Assert.Equal(expected, PlaylistSearchText.DisplayName(line));
  }

  [Theory]
  [InlineData("#EXTINF:-1 tvg-name=\"group-title=\" group-title=\"Kino\",X", "group-title", "Kino")]
  [InlineData("#EXTINF:-1 tvg-name=\"group-title=\",X", "group-title", null)]
  [InlineData("#EXTINF:-1 tvg-name=\"a group-title=\" tvg-id=\"b\",X", "group-title", null)]
  [InlineData("#EXTINF:-1 group-title=\"tvg-name=\" tvg-name=\"Actual\",X", "tvg-name", "Actual")]
  [InlineData("#EXTINF:-1 tvg-id=\"x.de\" group-title=\"DE | Kino\",X", "group-title", "DE | Kino")]
  [InlineData("#EXTINF:-1 TVG-ID = \"x.de\",X", "tvg-id", "x.de")]
  public void AttributeValue_MatchesNamesOnlyOutsideQuotedValues(string line, string attribute, string? expected)
  {
    Assert.Equal(expected, PlaylistSearchText.AttributeValue(line, attribute));
  }

  [Theory]
  [InlineData("Amélie", "amelie")]
  [InlineData("Ñandú", "nandu")]
  [InlineData("AMÉLIE", "amelie")]
  [InlineData("Çà Va", "ca va")]
  [InlineData("ÜBER", "uber")]
  public void Fold_IsCaseAndAccentInsensitive(string text, string expected)
  {
    Assert.Equal(expected, PlaylistSearchText.Fold(text));
  }

  [Fact]
  public void Fold_GivesSameFormForIndexAndQuery()
  {
    Assert.Equal(PlaylistSearchText.Fold("amelie"), PlaylistSearchText.Fold("Amélie"));
  }

  [Theory]
  [InlineData("DE | Sport  HD", "de sport hd")]
  [InlineData("  Das   Erste\t(HD) ", "das erste hd")]
  [InlineData("Movie: Part-2 / FHD", "movie part 2 fhd")]
  [InlineData("[DE] ▎ Kino", "de kino")]
  [InlineData("--- ...", "")]
  public void Fold_CollapsesSeparators(string text, string expected)
  {
    Assert.Equal(expected, PlaylistSearchText.Fold(text));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  public void Fold_HandlesEmpty(string? text)
  {
    Assert.Equal(string.Empty, PlaylistSearchText.Fold(text));
  }
}
