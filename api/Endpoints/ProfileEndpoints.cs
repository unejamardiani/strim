using System.Security.Claims;
using System.Text.Json;
using Api.Configuration;
using Api.Data;
using Microsoft.AspNetCore.Identity;

namespace Api.Endpoints;

/// <summary>A person's display name, avatar and theme, stored on their account (#579).</summary>
public static class ProfileEndpoints
{
  public const int MaxDisplayNameLength = 40;

  private static readonly string[] AvatarTones = ["violet", "tangerine", "gold", "signal", "ink", "paper"];
  private static readonly string[] AvatarSymbols = ["initial", "tv", "signal", "spark", "eye", "bookmark", "star", "home", "trend", "marks"];
  private static readonly string[] Themes = ["system", "light", "dark"];

  public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
  {
    // Fields the body leaves out stay as they are; JsonElement tells an omitted field from a null one.
    app.MapPut("/api/profile", async (JsonElement body, UserManager<IdentityUser> userManager, AppDbContext db, ClaimsPrincipal principal) =>
    {
      var user = await userManager.GetUserAsync(principal);
      if (user is null) return Results.Unauthorized();
      if (body.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { error = "Send a JSON object." });

      var account = db.Entry(user);
      if (body.TryGetProperty("displayName", out var displayName))
      {
        if (!TryReadDisplayName(displayName, out var value)) return FieldError("displayName", $"Give a display name of 1 to {MaxDisplayNameLength} characters without control characters, or none.");
        account.Property<string?>(AppDbContext.DisplayNameProperty).CurrentValue = value;
      }
      if (body.TryGetProperty("avatar", out var avatar))
      {
        if (!TryReadAvatar(avatar, out var value)) return FieldError("avatar", "Give an avatar as tone:symbol from the fixed lists, or none.");
        account.Property<string?>(AppDbContext.AvatarProperty).CurrentValue = value;
      }
      if (body.TryGetProperty("theme", out var theme))
      {
        if (theme.ValueKind != JsonValueKind.String || !Themes.Contains(theme.GetString())) return FieldError("theme", "Theme must be \"system\", \"light\" or \"dark\".");
        account.Property<string?>(AppDbContext.ThemeProperty).CurrentValue = theme.GetString();
      }

      await db.SaveChangesAsync();
      return Results.Ok(Describe(user, db, principal));
    }).RequireAuthorization().RequireCsrfToken().RequireRateLimiting(Security.RateLimits.General);

    return app;
  }

  /// <summary>The account and profile fields that <c>/api/auth/me</c> and <c>PUT /api/profile</c> return.</summary>
  public static object Describe(IdentityUser user, AppDbContext db, ClaimsPrincipal principal)
  {
    var account = db.Entry(user);
    var displayName = account.Property<string?>(AppDbContext.DisplayNameProperty).CurrentValue;
    return new
    {
      userName = user.UserName,
      email = user.Email,
      displayName,
      name = ShownName(displayName, user.UserName),
      avatar = account.Property<string?>(AppDbContext.AvatarProperty).CurrentValue,
      theme = account.Property<string?>(AppDbContext.ThemeProperty).CurrentValue ?? "system",
      signIn = new { method = SignInMethod(principal), identity = user.Email ?? user.UserName },
    };
  }

  /// <summary>The name Strim shows for a person: the display name, else the user name without an e-mail domain.</summary>
  public static string ShownName(string? displayName, string? userName)
  {
    if (!string.IsNullOrWhiteSpace(displayName)) return displayName;
    var name = userName ?? "";
    var at = name.IndexOf('@');
    return at > 0 ? name[..at] : name;
  }

  /// <summary>
  /// How this session signed in. External sign-ins carry the provider's scheme as the authentication method;
  /// the provider names are the lower-cased schemes (see <see cref="Authentication"/>). Password sign-in and
  /// registration carry none.
  /// </summary>
  private static string SignInMethod(ClaimsPrincipal principal) =>
    principal.FindFirstValue(ClaimTypes.AuthenticationMethod) switch
    {
      null => "password",
      CloudflareAccess.LoginProvider => "access",
      var scheme => scheme.ToLowerInvariant(),
    };

  /// <summary>Null and "" clear the name; anything else must be 1 to 40 characters after trimming.</summary>
  private static bool TryReadDisplayName(JsonElement element, out string? value)
  {
    value = null;
    if (element.ValueKind == JsonValueKind.Null) return true;
    if (element.ValueKind != JsonValueKind.String) return false;
    var raw = element.GetString()!;
    if (raw.Length == 0) return true;
    value = raw.Trim();
    return value.Length is > 0 and <= MaxDisplayNameLength && !raw.Any(char.IsControl);
  }

  /// <summary>Null means the default avatar; otherwise "tone:symbol".</summary>
  private static bool TryReadAvatar(JsonElement element, out string? value)
  {
    value = null;
    if (element.ValueKind == JsonValueKind.Null) return true;
    if (element.ValueKind != JsonValueKind.String) return false;
    var parts = element.GetString()!.Split(':');
    if (parts.Length != 2 || !AvatarTones.Contains(parts[0]) || !AvatarSymbols.Contains(parts[1])) return false;
    value = element.GetString();
    return true;
  }

  private static IResult FieldError(string field, string error) => Results.BadRequest(new { error, field });
}
