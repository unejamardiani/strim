using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the API base override (#540): main.js reads the <c>?api=</c> parameter and the stored
/// <c>strim.apiBase</c> only on a local host and removes the stored value on any other host.
/// </summary>
public class ApiBaseOverrideTests
{
  private static string ApiBaseBlock()
  {
    var js = File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), "main.js"));
    var match = Regex.Match(js, @"const API_BASE = \(\(\) => \{(.*?)\n\}\)\(\);", RegexOptions.Singleline);
    Assert.True(match.Success, "main.js defines API_BASE as an immediately invoked function");
    return match.Groups[1].Value;
  }

  [Fact]
  public void LocalHostCheck_NamesExactlyTheLoopbackHosts()
  {
    Assert.Contains(
      "const localHost = ['localhost', '127.0.0.1', '[::1]'].includes(window.location.hostname);",
      ApiBaseBlock());
  }

  [Fact]
  public void QueryAndStorage_AreReadOnlyInsideTheLocalHostCheck_AndRemovedOtherwise()
  {
    var block = ApiBaseBlock();
    var branches = Regex.Match(
      block,
      @"if \(localHost\) \{(?<local>.*?)\n    \} else \{(?<other>.*?)\n    \}",
      RegexOptions.Singleline);
    Assert.True(branches.Success, "API_BASE branches on localHost");

    var local = branches.Groups["local"].Value;
    Assert.Contains(".get('api')", local);
    Assert.Contains("localStorage.getItem('strim.apiBase')", local);

    var other = branches.Groups["other"].Value.Trim();
    Assert.Equal("localStorage.removeItem('strim.apiBase');", other);

    // Outside the local branch nothing reads the query parameter or the stored value.
    var outside = block.Replace(local, "");
    Assert.DoesNotContain(".get('api')", outside);
    Assert.DoesNotContain("getItem('strim.apiBase')", outside);
    Assert.DoesNotContain("location.search", outside);
  }

  [Fact]
  public void StrimApiBaseGlobal_AndDefault_StillApply()
  {
    Assert.Contains(
      "return (fromQuery || window.STRIM_API_BASE || fromStorage || defaultApiBase()).replace(/\\/+$/, '');",
      ApiBaseBlock());
  }
}
