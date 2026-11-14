using System.Text.Json;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class ProviderAccountInfoTests
{
  [Theory]
  [InlineData(
    "http://iptv.example.com:8080/get.php?username=u&password=p&type=m3u_plus",
    "http://iptv.example.com:8080/player_api.php?username=u&password=p")]
  [InlineData(
    "https://iptv.example.com/panel/get.php?type=m3u_plus&output=ts&password=p&username=u",
    "https://iptv.example.com/panel/player_api.php?username=u&password=p")]
  [InlineData(
    "http://iptv.example.com/get.php?username=a%20b&password=p%26q#frag",
    "http://iptv.example.com/player_api.php?username=a%20b&password=p%26q")]
  public void BuildEndpoint_KeepsSchemeHostPortAndDirectoryAndDropsOtherParameters(string link, string expected)
  {
    Assert.Equal(expected, ProviderAccountInfo.BuildEndpoint(link)?.AbsoluteUri);
  }

  [Theory]
  [InlineData("http://iptv.example.com/playlist.m3u?username=u&password=p")]
  [InlineData("http://iptv.example.com/get.php?username=u")]
  [InlineData("http://iptv.example.com/get.php?password=p")]
  [InlineData("http://iptv.example.com/get.php?username=&password=p")]
  [InlineData("http://iptv.example.com/get.php")]
  [InlineData("ftp://iptv.example.com/get.php?username=u&password=p")]
  [InlineData("not a link")]
  [InlineData("")]
  [InlineData(null)]
  public void BuildEndpoint_ReturnsNullWithoutGetPhpOrCredentials(string? link)
  {
    Assert.Null(ProviderAccountInfo.BuildEndpoint(link));
  }

  [Theory]
  [InlineData("1767225600")]
  [InlineData("\"1767225600\"")]
  public void Parse_ReadsExpiryAsNumberOrNumericString(string expDate)
  {
    var result = ProviderAccountInfo.Parse($$$"""{"user_info":{"auth":1,"exp_date":{{{expDate}}}}}""");

    Assert.True(result.IsKnown);
    Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), result.ExpiresAt);
    Assert.Equal(TimeSpan.Zero, result.ExpiresAt!.Value.Offset);
  }

  [Theory]
  [InlineData("null")]
  [InlineData("\"\"")]
  [InlineData("\"never\"")]
  [InlineData("-1")]
  [InlineData("\"-86400\"")]
  [InlineData("253402300800")]
  [InlineData("99999999999999")]
  [InlineData("1.5e300")]
  [InlineData("true")]
  public void Parse_GivesNoExpiryForMissingOrOutOfRangeValues(string expDate)
  {
    var result = ProviderAccountInfo.Parse($$$"""{"user_info":{"auth":1,"exp_date":{{{expDate}}}}}""");

    Assert.True(result.IsKnown);
    Assert.Null(result.ExpiresAt);
  }

  [Theory]
  [InlineData("0")]
  [InlineData("\"0\"")]
  public void Parse_AcceptsUnixTimeZero(string expDate)
  {
    var result = ProviderAccountInfo.Parse($$$"""{"user_info":{"auth":1,"exp_date":{{{expDate}}}}}""");

    Assert.Equal(new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), result.ExpiresAt);
  }

  [Fact]
  public void Parse_AcceptsTheLastSecondOfYear9999()
  {
    var result = ProviderAccountInfo.Parse("""{"user_info":{"exp_date":253402300799}}""");

    Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(253402300799), result.ExpiresAt);
  }

  [Fact]
  public void Parse_ReadsStatusAndConnectionsAsStrings()
  {
    var result = ProviderAccountInfo.Parse(
      """{"user_info":{"auth":"1","status":"Active","max_connections":"2","active_cons":"1"}}""");

    Assert.True(result.IsKnown);
    Assert.Equal("Active", result.Status);
    Assert.Equal(2, result.MaxConnections);
    Assert.Equal(1, result.ActiveConnections);
  }

  [Fact]
  public void Parse_ReadsStatusAndConnectionsAsNumbers()
  {
    var result = ProviderAccountInfo.Parse(
      """{"user_info":{"auth":1,"status":1,"max_connections":3,"active_cons":0}}""");

    Assert.Equal("1", result.Status);
    Assert.Equal(3, result.MaxConnections);
    Assert.Equal(0, result.ActiveConnections);
  }

  [Theory]
  [InlineData("""{"user_info":{}}""")]
  [InlineData("""{"user_info":{"status":"","max_connections":"many","active_cons":1.5}}""")]
  [InlineData("""{"user_info":{"status":null,"max_connections":null,"active_cons":"x"}}""")]
  [InlineData("""{"user_info":{"status":{},"max_connections":[],"active_cons":true}}""")]
  public void Parse_GivesNullForMissingOrUnparsableFields(string body)
  {
    var result = ProviderAccountInfo.Parse(body);

    Assert.True(result.IsKnown);
    Assert.Null(result.Status);
    Assert.Null(result.MaxConnections);
    Assert.Null(result.ActiveConnections);
    Assert.Null(result.ExpiresAt);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("<!DOCTYPE html><html><body>502 Bad Gateway</body></html>")]
  [InlineData("{\"user_info\":")]
  [InlineData("{}")]
  [InlineData("""{"server_info":{}}""")]
  [InlineData("""{"user_info":{"auth":0,"status":"Active"}}""")]
  [InlineData("""{"user_info":{"auth":"0"}}""")]
  [InlineData("[]")]
  [InlineData("""[{"user_info":{"auth":1}}]""")]
  [InlineData("""{"user_info":[]}""")]
  [InlineData("""{"user_info":"x"}""")]
  [InlineData("null")]
  [InlineData("42")]
  public void Parse_GivesUnknownForUnusableAnswers(string? body)
  {
    var result = ProviderAccountInfo.Parse(body);

    Assert.False(result.IsKnown);
    Assert.Same(ProviderAccountResult.Unknown, result);
  }

  [Theory]
  [InlineData("status", "\\uD800")]
  [InlineData("status", "\\uDC00")]
  [InlineData("exp_date", "\\uD800")]
  [InlineData("exp_date", "\\uDC00")]
  [InlineData("max_connections", "\\uD800")]
  [InlineData("max_connections", "\\uDC00")]
  [InlineData("active_cons", "\\uD800")]
  [InlineData("active_cons", "\\uDC00")]
  public void Parse_GivesNullForAStringFieldWithAnUnpairedSurrogateAndKeepsTheOthers(string field, string escape)
  {
    var fields = new Dictionary<string, string>
    {
      ["status"] = "\"Active\"",
      ["exp_date"] = "\"1767225600\"",
      ["max_connections"] = "\"2\"",
      ["active_cons"] = "\"1\"",
    };
    fields[field] = $"\"{escape}\"";
    var body = "{\"user_info\":{\"auth\":1," + string.Join(",", fields.Select(f => $"\"{f.Key}\":{f.Value}")) + "}}";

    var result = ProviderAccountInfo.Parse(body);

    Assert.True(result.IsKnown);
    Assert.Equal(field == "status" ? null : "Active", result.Status);
    Assert.Equal(field == "exp_date" ? null : new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), result.ExpiresAt);
    Assert.Equal(field == "max_connections" ? null : 2, result.MaxConnections);
    Assert.Equal(field == "active_cons" ? null : 1, result.ActiveConnections);
  }

  [Theory]
  [InlineData("\\uD800")]
  [InlineData("\\uDC00")]
  public void Parse_TreatsAnAuthWithAnUnpairedSurrogateAsUnreadable(string escape)
  {
    var result = ProviderAccountInfo.Parse($$$"""{"user_info":{"auth":"{{{escape}}}","status":"Active"}}""");

    Assert.True(result.IsKnown);
    Assert.Equal("Active", result.Status);
  }

  [Fact]
  public void Parse_NeverThrowsOnMalformedAnswers()
  {
    string[] bodies =
    [
      """{"user_info":{"status":"\u12"}}""",
      """{"user_info":{"status":"\u"}}""",
      """{"user_info":{"status":"\uZZZZ"}}""",
      """{"user_info":{"status":"\""",
      """{"user_info":{"status":"\uD800\uD800"}}""",
      """{"user_info":{"status":"\uDC00\uD800"}}""",
      """{"user_info":{"status":"a\uD800b","max_connections":"\uDFFF","active_cons":"\uD83D"}}""",
      """{"user_info":{"auth":"\uDBFF","exp_date":"\uDC00"}}""",
      """{"user_info":{"\uD800":"Active","status":"Active"}}""",
      """{"\uD800":{},"user_info":{"status":"Active"}}""",
      """{"user_\uD800info":{"status":"Active"}}""",
      """{"\uDC00user_info":{"status":"Active"},"user_info":{"st\uD800atus":1}}""",
      """{"user_info":{"status":"Active"},"\u""",
      "{\"user_info\":{\"status\":\"\ud800\"}}",
      "{\"user_info\":{\"\udc00\":\"x\",\"status\":\"Active\"}}",
    ];

    foreach (var body in bodies)
    {
      var exception = Record.Exception(() => ProviderAccountInfo.Parse(body));
      Assert.True(exception is null, $"Parse threw {exception?.GetType().Name} for {body}");
    }
  }

  [Fact]
  public void Parse_NeverCarriesTheUsernameOrPassword()
  {
    const string body = """
      {"user_info":{"username":"secret-user","password":"secret-pass","auth":1,"status":"Active",
        "exp_date":"1767225600","max_connections":"1","active_cons":"0","message":"secret-user"},
       "server_info":{"url":"iptv.example.com","port":"8080"}}
      """;

    var result = ProviderAccountInfo.Parse(body);
    var rendered = result.ToString() + JsonSerializer.Serialize(result);

    Assert.True(result.IsKnown);
    Assert.DoesNotContain("secret-user", rendered);
    Assert.DoesNotContain("secret-pass", rendered);
  }
}
