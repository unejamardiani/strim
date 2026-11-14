using Api.Services;
using Xunit;

namespace Api.Tests;

public class VariantFinderTests
{
  private static List<PlaylistEntry> Entries(params (string Name, string Group)[] items) =>
    items.Select((item, i) => new PlaylistEntry(i, item.Name, item.Group, null, $"http://example.invalid/live/{i}.ts"))
      .ToList();

  [Fact]
  public void QualityWords_GroupVariants()
  {
    var groups = VariantFinder.Find(Entries(("Sport 1 HD", "Sport"), ("News", "News"), ("Sport 1 FHD", "Sport"), ("Sport 1 4K", "Sport")));

    var group = Assert.Single(groups);
    Assert.Equal("Sport 1", group.BaseName);
    Assert.Equal(new[] { 0, 2, 3 }, group.Entries.Select(v => v.Entry.Index));
    Assert.Equal(new[] { "HD", "FHD", "4K" }, group.Entries.Select(v => Assert.Single(v.QualityWords)));
    Assert.Equal(new[] { "Sport" }, group.Groups);
  }

  [Theory]
  [InlineData("Sport 1 sd", "SD")]
  [InlineData("Sport 1 Hd", "HD")]
  [InlineData("Sport 1 fHD", "FHD")]
  [InlineData("Sport 1 4k", "4K")]
  [InlineData("Sport 1 uhd", "UHD")]
  [InlineData("Sport 1 HeVc", "HEVC")]
  [InlineData("Sport 1 h265", "H265")]
  [InlineData("Sport 1 h.265", "H265")]
  [InlineData("Sport 1 BACKUP", "backup")]
  [InlineData("Sport 1 Raw", "raw")]
  public void EachQualityWord_IsRecognised(string name, string word)
  {
    var (baseName, words) = VariantFinder.Detect(name);
    Assert.Equal("Sport 1", baseName);
    Assert.Equal(new[] { word }, words);

    var group = Assert.Single(VariantFinder.Find(Entries(("Sport 1", "A"), (name, "A"))));
    Assert.Equal("Sport 1", group.BaseName);
  }

  [Fact]
  public void BracketAndSeparatorForms_AreRecognised()
  {
    var groups = VariantFinder.Find(Entries(
      ("Sport 1 (HD)", "A"), ("Sport 1 [FHD]", "A"), ("Sport 1 | HEVC", "A"), ("  Sport   1 - UHD ", "A"), ("Sport 1 HD (backup)", "A")));

    var group = Assert.Single(groups);
    Assert.Equal("Sport 1", group.BaseName);
    Assert.Equal(5, group.Entries.Count);
    Assert.Equal(new[] { "HD", "backup" }, group.Entries[4].QualityWords);
  }

  [Theory]
  [InlineData("HDTV Classics")]
  [InlineData("Shadow")]
  [InlineData("4Kids")]
  [InlineData("Rawhide")]
  [InlineData("Backup Plan Movie")]
  public void NearMisses_AreNotQualityWords(string name)
  {
    var (baseName, words) = VariantFinder.Detect(name);
    Assert.Equal(name, baseName);
    Assert.Empty(words);
  }

  [Fact]
  public void NearMisses_AreNotGrouped()
  {
    Assert.Empty(VariantFinder.Find(Entries(
      ("HDTV Classics", "A"), ("TV Classics", "A"), ("Shadow", "A"), ("Shadow", "A"), ("4Kids", "A"), ("Kids", "A"),
      ("Rawhide", "A"), ("hide", "A"), ("Backup Plan Movie", "A"), ("Plan Movie", "A"))));
  }

  [Fact]
  public void DifferentBaseNames_AreNotGrouped()
  {
    Assert.Empty(VariantFinder.Find(Entries(("Sport 1 HD", "A"), ("Sport 10 HD", "A"))));
  }

  [Fact]
  public void NoQualityWord_NoVariantGroup()
  {
    Assert.Empty(VariantFinder.Find(Entries(("News", "A"), ("News", "B"))));
  }

  [Fact]
  public void VariantsAcrossGroups_ListAllGroups()
  {
    var groups = VariantFinder.Find(Entries(("Sport 1 HD", "DE HD"), ("Sport 1", "DE"), ("Sport 1 4K", "DE 4K"), ("Sport 1 SD", "DE")));

    var group = Assert.Single(groups);
    Assert.Equal(new[] { "DE HD", "DE", "DE 4K" }, group.Groups);
    Assert.Equal(new[] { "DE HD", "DE", "DE 4K", "DE" }, group.Entries.Select(v => v.Entry.GroupTitle));
    Assert.Empty(group.Entries[1].QualityWords);
  }

  [Fact]
  public void InputList_IsNotModified()
  {
    var entries = Entries(("Sport 1 HD", "A"), ("Sport 1 FHD", "B"));
    var before = entries.ToList();

    VariantFinder.Find(entries);

    Assert.Equal(before, entries);
  }
}
