namespace Api.Services;

/// <summary>
/// The page file to serve for an error status code. When <see cref="Text"/> is null the file's own
/// text fits; otherwise the page's code, title, heading and lead are replaced with it.
/// </summary>
public sealed record ErrorPage(string File, ErrorPageText? Text);

/// <summary>Constant, plain text for an error page; HTML-encode it before putting it into a page.</summary>
public sealed record ErrorPageText(string Code, string Title, string Heading, string Lead);

/// <summary>Picks the error page and, where the page's own copy does not fit, the text per status code.</summary>
public static class ErrorPages
{
  public const string NotFoundFile = "404.html";
  public const string ServerErrorFile = "500.html";
  public const string UnavailableFile = "50x.html";

  public static ErrorPage For(int statusCode) => statusCode switch
  {
    404 => new ErrorPage(NotFoundFile, null),
    500 => new ErrorPage(ServerErrorFile, null),
    > 500 and < 600 => new ErrorPage(UnavailableFile, null),
    429 => new ErrorPage(UnavailableFile, new ErrorPageText(
      "429",
      "Too many requests",
      "Slow down a little.",
      "Too many requests came in at once. Wait a minute, then try again.")),
    // Any other 4xx and codes outside 400–599 get a neutral message with the real code.
    _ => new ErrorPage(NotFoundFile, new ErrorPageText(
      statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
      "Something went wrong",
      "That did not work.",
      "The request could not be handled. Go back and try again.")),
  };
}
