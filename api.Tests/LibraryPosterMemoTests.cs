using Api.Services;
using Xunit;

namespace Api.Tests;

public class LibraryPosterMemoTests
{
  private readonly ManualTime _time = new();
  [Fact]
  public void Memo_ExpiresAfterThirtyDays()
  {
    var memo = new LibraryPosterMemo(_time);
    memo.Set("film:1995:little motel", "/p.jpg");
    _time.Now += TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1);
    Assert.True(memo.TryGet("film:1995:little motel", out var path));
    Assert.Equal("/p.jpg", path);
    _time.Now += TimeSpan.FromSeconds(1);
    Assert.False(memo.TryGet("film:1995:little motel", out _));
    Assert.Equal(0, memo.Count);
  }
  [Fact]
  public void Memo_EvictsTheOldestPastItsLimit()
  {
    var memo = new LibraryPosterMemo(_time) { MaxEntries = 2 };
    foreach (var key in new[] { "a", "b", "c" }) memo.Set(key, $"/{key}.jpg");
    Assert.False(memo.TryGet("a", out _));
    Assert.True(memo.TryGet("b", out _) && memo.TryGet("c", out _));
    Assert.Equal(20_000, new LibraryPosterMemo(_time).MaxEntries);
  }
  [Fact]
  public void Memo_RemembersNone()
  {
    var memo = new LibraryPosterMemo(_time);
    memo.Set("series:harbour lights", null);
    Assert.True(memo.TryGet("series:harbour lights", out var path));
    Assert.Null(path);
    Assert.False(memo.TryGet("series:unknown", out _));
  }

  private sealed class ManualTime : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
  }
}
