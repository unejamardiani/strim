using Api.Services;
using Xunit;

namespace Api.Tests;

public class GroupSearchWordsTests
{
  [Fact]
  public void PipeSeparatedPrefix_GivesBothWords()
  {
    Assert.Equal(new[] { "de", "sport" }, GroupSearchWords.From("DE | Sport"));
  }

  [Theory]
  [InlineData("|DE| Kino")]
  [InlineData("DE: Kino")]
  [InlineData("DE ▎ Kino")]
  [InlineData("DE ┃ Kino")]
  [InlineData("DE │ Kino")]
  [InlineData("[DE] (Kino)")]
  [InlineData("DE-Kino")]
  [InlineData("DE/Kino")]
  public void PrefixSeparators_SplitWords(string title)
  {
    Assert.Equal(new[] { "de", "kino" }, GroupSearchWords.From(title));
  }

  [Theory]
  [InlineData("Comedy", "comedy")]
  [InlineData("Video", "video")]
  [InlineData("Kids", "kids")]
  public void LettersInsideAWord_DoNotYieldDe(string title, string word)
  {
    var words = GroupSearchWords.From(title);
    Assert.DoesNotContain("de", words);
    Assert.Equal(new[] { word }, words);
  }

  [Fact]
  public void Series_DoesNotYieldEs()
  {
    var words = GroupSearchWords.From("Series");
    Assert.DoesNotContain("es", words);
    Assert.Equal(new[] { "series" }, words);
  }

  [Fact]
  public void AccentsAndNonLatinScripts_StayIntact()
  {
    Assert.Equal(new[] { "ελληνικά" }, GroupSearchWords.From("Ελληνικά"));
    Assert.Equal(new[] { "télé" }, GroupSearchWords.From("Télé"));
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("\t \n")]
  [InlineData(null)]
  public void EmptyWhitespaceOrNull_GivesEmptyList(string? title)
  {
    Assert.Empty(GroupSearchWords.From(title));
  }

  [Fact]
  public void RepeatedWords_AppearOnce()
  {
    Assert.Equal(new[] { "de", "sport" }, GroupSearchWords.From("DE | Sport | de sport"));
  }
}
