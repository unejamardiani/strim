using Api.Endpoints;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class LibrarySuggestionsTests
{
  private static readonly LibraryEndpoints.FilmMark[] NoMarks = [];
  private static readonly Dictionary<string, LibraryEndpoints.FilmMark> NoMarksByKey = new();

  private static TmdbMovieSummary Film(int id, string title, string? date = "2020-01-01", string? original = null, bool adult = false) =>
    new(id, title, date, $"/f{id}.jpg", 7) { OriginalTitle = original, Adult = adult };

  private static TmdbTvSummary Show(int id, string name, string? date = "2021-01-01", string? original = null, bool adult = false) =>
    new(id, name, date, $"/s{id}.jpg", 7) { OriginalName = original, Adult = adult };

  private static string Key(string name, MarkKind kind) =>
    MarkKey.From(name, kind)?.Key ?? throw new Xunit.Sdk.XunitException($"No key for '{name}'");

  private static PlaylistTitle Title(string key, string group = "Movies") =>
    new(key, key, null, PlaylistContentType.Movies, key, group, group.ToLowerInvariant(), true, 1, 1, null, null, null);

  private static SuggestionCandidate Candidate(int id, params string[] keys) => new(id, null, keys);

  private static LibraryEndpoints.FilmMark Mark(string key, bool seen = false, int rating = 0, bool watchlist = false, string? id = null) =>
    new(key, seen, rating, watchlist, id);

  [Fact]
  public void Trending_InterleavesFilmsAndSeriesByRank()
  {
    var result = LibrarySuggestions.Trending(
      [Film(1, "A"), Film(2, "B"), Film(3, "C"), Film(4, "D")], [Show(11, "X"), Show(12, "Y")], NoMarks);

    Assert.Equal([1, 11, 2, 12, 3, 4], result.Select(c => c.TmdbId));
    Assert.Equal("/f1.jpg", result[0].PosterPath);
    Assert.Equal("/s11.jpg", result[1].PosterPath);
  }

  [Fact]
  public void Trending_InterleavesByRank_BeforeSkippingAdultItems()
  {
    var result = LibrarySuggestions.Trending([Film(1, "A", adult: true), Film(2, "B")], [Show(11, "X")], NoMarks);

    Assert.Equal([11, 2], result.Select(c => c.TmdbId));
  }

  [Fact]
  public void Trending_SkipsAdultUnknownAndRepeatedItems()
  {
    var result = LibrarySuggestions.Trending(
      [Film(0, "Zero"), Film(5, "Five"), Film(6, "Adult", adult: true), Film(5, "Five again"), Film(7, "Seven")],
      [Show(-1, "Minus"), Show(5, "Five show"), Show(5, "Five show again")], NoMarks);

    Assert.Equal([5, 5, 7], result.Select(c => c.TmdbId));
    Assert.Equal(Key("Five (2020)", MarkKind.Film), result[0].Keys[0]);
    Assert.Equal(Key("Five show (2021)", MarkKind.Series), result[1].Keys[0]);
  }

  [Fact]
  public void FilmKeys_NeedTheYear_FromTitleAndOriginalTitle()
  {
    var film = LibrarySuggestions.Trending([Film(1, "The Thief", "1999-05-01", "Le Voleur")], [], NoMarks).Single();

    Assert.Equal([Key("The Thief (1999)", MarkKind.Film), Key("Le Voleur (1999)", MarkKind.Film)], film.Keys);
  }

  [Fact]
  public void FilmWithoutAYear_IsNotMatched_ToAYearlessListTitleOfTheSameName()
  {
    var candidates = LibrarySuggestions.Trending([Film(1, "The Thief", date: null, original: "  ")], [], NoMarks);
    var listTitle = Title(Key("The Thief", MarkKind.Film));

    Assert.Empty(candidates.Single().Keys);
    Assert.Empty(LibrarySuggestions.Match(candidates, [listTitle], NoMarksByKey));
  }

  [Fact]
  public void SeriesKeys_WithAndWithoutTheYear_FromNameAndOriginalName()
  {
    var show = LibrarySuggestions.Trending([], [Show(3, "The House", "2019-09-09", "La Casa")], NoMarks).Single();

    Assert.Equal(
      [
        Key("The House (2019)", MarkKind.Series), Key("The House", MarkKind.Series),
        Key("La Casa (2019)", MarkKind.Series), Key("La Casa", MarkKind.Series),
      ],
      show.Keys);
  }

  [Fact]
  public void Keys_IncludeTheCallersMarksStoringTheId_OfTheSameKindOnly()
  {
    var filmMark = Mark(Key("Thief", MarkKind.Film), id: "9");
    var otherFilm = Mark(Key("Other (2001)", MarkKind.Film), id: "8");
    var result = LibrarySuggestions.Trending([Film(9, "The Thief", "1999")], [Show(9, "Stranger", null)], [filmMark, otherFilm]);

    Assert.Equal([Key("The Thief (1999)", MarkKind.Film), filmMark.TitleKey], result[0].Keys);
    Assert.Equal([Key("Stranger", MarkKind.Series)], result[1].Keys);
  }

  [Fact]
  public void Match_ChecksTheStoredIdOnTheFirstKeyFound_AndDoesNotMoveOnToALaterKey()
  {
    var translated = Key("Der Dieb (1999)", MarkKind.Film);
    var original = Key("The Thief (1999)", MarkKind.Film);
    var candidate = LibrarySuggestions.Trending([Film(1, "Der Dieb", "1999", "The Thief")], [], NoMarks).Single();
    var marks = new Dictionary<string, LibraryEndpoints.FilmMark> { [translated] = Mark(translated, id: "42") };

    Assert.Equal([translated, original], candidate.Keys);
    Assert.Empty(LibrarySuggestions.Match([candidate], [Title(original), Title(translated)], marks));

    var sameId = new Dictionary<string, LibraryEndpoints.FilmMark> { [translated] = Mark(translated, id: "1") };
    Assert.Equal(translated, LibrarySuggestions.Match([candidate], [Title(translated)], sameId).Single().Title.MarkKey);
  }

  [Fact]
  public void Match_LeavesOutSeenThumbsDownAndAdultGroups_KeepsWatchlisted()
  {
    var candidates = new[] { Candidate(1, "seen"), Candidate(2, "down"), Candidate(3, "adult"), Candidate(4, "watch"), Candidate(5, "up") };
    var found = new[] { Title("seen"), Title("down"), Title("adult", "XXX Movies"), Title("watch"), Title("up") };
    var marks = new Dictionary<string, LibraryEndpoints.FilmMark>
    {
      ["seen"] = Mark("seen", seen: true),
      ["down"] = Mark("down", rating: -1),
      ["watch"] = Mark("watch", watchlist: true),
      ["up"] = Mark("up", rating: 1),
    };

    Assert.Equal([4, 5], LibrarySuggestions.Match(candidates, found, marks).Select(m => m.Candidate.TmdbId));
  }

  [Fact]
  public void Match_FirstCandidateWinsAListTitle()
  {
    var first = new PlaylistTitle("First", "first", null, PlaylistContentType.Movies, "k", "Movies", "movies", true, 1, 1, null, null, null);
    var duplicate = first with { Name = "Second" };

    var result = LibrarySuggestions.Match([Candidate(1, "k"), Candidate(2, "k", "free")], [first, duplicate, Title("free")], NoMarksByKey);

    var match = Assert.Single(result);
    Assert.Equal(1, match.Candidate.TmdbId);
    Assert.Equal("First", match.Title.Name);
  }

  [Fact]
  public void Match_StopsAtTwelve()
  {
    var candidates = Enumerable.Range(1, 20).Select(i => Candidate(i, $"k{i}")).ToList();
    var found = Enumerable.Range(1, 20).Select(i => Title($"k{i}")).ToList();

    var result = LibrarySuggestions.Match(candidates, found, NoMarksByKey);

    Assert.Equal(LibrarySuggestions.MaxResults, result.Count);
    Assert.Equal(Enumerable.Range(1, 12), result.Select(m => m.Candidate.TmdbId));
  }
}
