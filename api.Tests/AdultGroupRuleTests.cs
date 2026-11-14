using Api.Services;
using Xunit;

namespace Api.Tests;

public class AdultGroupRuleTests
{
  [Theory]
  [InlineData("XXX")]
  [InlineData("xxx")]
  [InlineData("XXX Movies")]
  [InlineData("Adult")]
  [InlineData("ADULTS")]
  [InlineData("Adults VOD")]
  [InlineData("18+")]
  [InlineData("+18")]
  [InlineData("Channels 18+")]
  [InlineData("For Adults")]
  [InlineData("US | XXX")]
  [InlineData("[18+] Night")]
  [InlineData("VOD: Adult")]
  [InlineData("Movies (Adult)")]
  [InlineData("UK/XXX")]
  [InlineData("VOD.Adults,Night")]
  [InlineData("Adult Swim | XXX")]
  public void IsAdult_MatchesAdultTitles(string title)
  {
    Assert.True(AdultGroupRule.IsAdultGroupTitle(title));
  }

  [Theory]
  [InlineData("Porn")]
  [InlineData("DE: Erwachsene")]
  [InlineData("Für Erwachsene")]
  [InlineData("FSK18 Filme")]
  [InlineData("FR | Adulte")]
  [InlineData("Films Adultes")]
  [InlineData("ES Adultos")]
  [InlineData("PT - Adultos")]
  [InlineData("IT Adulti")]
  [InlineData("TR Yetişkin")]
  [InlineData("TR YETİŞKİN")]
  [InlineData("TR Yetiskin")]
  [InlineData("RU Для взрослых")]
  [InlineData("ДЛЯ ВЗРОСЛЫХ")]
  public void IsAdult_MatchesOtherLanguageMarkers(string title)
  {
    Assert.True(AdultGroupRule.IsAdultGroupTitle(title));
  }

  [Theory]
  [InlineData("Adultswim Kids")]
  [InlineData("Essex")]
  [InlineData("XXXL Sports")]
  [InlineData("Sport 180")]
  [InlineData("118+ Channels")]
  [InlineData("Kids 8+")]
  [InlineData("Adulthood Docs")]
  [InlineData("News")]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData(null)]
  [InlineData("Pornography Docs")]
  [InlineData("FSK16")]
  [InlineData("Erwachsenenbildung")]
  [InlineData("Adultery Drama")]
  [InlineData("RU Для детей")]
  [InlineData("+180")]
  public void IsAdult_IgnoresNearMisses(string? title)
  {
    Assert.False(AdultGroupRule.IsAdultGroupTitle(title));
  }

  [Theory]
  [InlineData("Adult Swim")]
  [InlineData("adult-swim")]
  [InlineData("Adult Education")]
  [InlineData("ADULT-EDUCATION")]
  [InlineData("Young Adult")]
  [InlineData("young-adult")]
  [InlineData("US | Adult Swim")]
  public void IsAdult_SkipsKnownHarmlessPhrases(string title)
  {
    Assert.False(AdultGroupRule.IsAdultGroupTitle(title));
  }

  [Fact]
  public void IsAdult_HandlesThousandsOfGroups()
  {
    var flagged = 0;
    for (var i = 0; i < 10_000; i++)
    {
      if (AdultGroupRule.IsAdultGroupTitle(i % 2 == 0 ? $"Group {i} | XXX" : $"Group {i} | Sports"))
      {
        flagged++;
      }
    }

    Assert.Equal(5_000, flagged);
  }
}
