using System.Security.Claims;
using Api.Configuration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Api.Endpoints;

public record RegisterRequest(string UserName, string Password, string? Email);

public record LoginRequest(string UserName, string Password);

/// <summary>Username/password accounts, external sign-in and the CSRF token.</summary>
public static class AuthEndpoints
{
  /// <summary>Where sign-in flows land when no (safe) return URL is given.</summary>
  public const string AppPath = "/app";

  public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
  {
    var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Strim.Auth");
    var auth = app.MapGroup("/api/auth").RequireRateLimiting(Security.RateLimits.Auth);

    auth.MapGet("/me", async (UserManager<IdentityUser> userManager, ClaimsPrincipal principal) =>
    {
      var user = await userManager.GetUserAsync(principal);
      return user is null ? Results.Unauthorized() : Results.Ok(new { userName = user.UserName, email = user.Email });
    });

    auth.MapPost("/register", async (RegisterRequest request, UserManager<IdentityUser> userManager, SignInManager<IdentityUser> signInManager) =>
    {
      if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
      {
        return Results.BadRequest(new { error = "Username and password are required." });
      }

      try
      {
        var userName = request.UserName.Trim();
        if (await userManager.FindByNameAsync(userName) is not null)
        {
          return Results.BadRequest(new { error = "Username is already taken." });
        }

        var user = new IdentityUser { UserName = userName, Email = request.Email?.Trim(), LockoutEnabled = true };
        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
          return Results.BadRequest(new { error = string.Join("; ", created.Errors.Select(e => e.Description)) });
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return Results.Ok(new { userName = user.UserName, email = user.Email });
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Registration failed");
        return Results.Problem("Registration failed due to an internal error", statusCode: 500);
      }
    }).RequireCsrfToken();

    auth.MapPost("/login", async (LoginRequest request, SignInManager<IdentityUser> signInManager, UserManager<IdentityUser> userManager) =>
    {
      if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
      {
        return Results.BadRequest(new { error = "Username and password are required." });
      }

      try
      {
        var user = await userManager.FindByNameAsync(request.UserName.Trim());
        if (user is null) return Results.Unauthorized();

        var result = await signInManager.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.IsLockedOut) return Results.StatusCode(StatusCodes.Status423Locked);
        if (!result.Succeeded) return Results.Unauthorized();

        return Results.Ok(new { userName = user.UserName, email = user.Email });
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Login failed");
        return Results.Problem("Login failed due to an internal error", statusCode: 500);
      }
    }).RequireCsrfToken();

    auth.MapPost("/logout", async (SignInManager<IdentityUser> signInManager) =>
    {
      await signInManager.SignOutAsync();
      return Results.Ok();
    }).RequireAuthorization();

    auth.MapGet("/providers", (ExternalLoginProviders providers) =>
      Results.Ok(providers.All.Select(p => new { name = p.Name, displayName = p.DisplayName })));

    auth.MapGet("/external/{provider}", (string provider, string? returnUrl, ExternalLoginProviders providers, SignInManager<IdentityUser> signInManager) =>
    {
      var configured = providers.Find(provider);
      if (configured is null) return Results.NotFound();

      var callback = $"/api/auth/external-callback?returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}";
      var properties = signInManager.ConfigureExternalAuthenticationProperties(configured.Scheme, callback);
      return Results.Challenge(properties, new[] { configured.Scheme });
    });

    auth.MapGet("/external-callback", async (string? returnUrl, SignInManager<IdentityUser> signInManager, UserManager<IdentityUser> userManager, HttpContext context) =>
    {
      var failed = $"{AppPath}?auth=failed";
      var info = await signInManager.GetExternalLoginInfoAsync();
      if (info is null) return Results.Redirect(failed);

      var signIn = await signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false);
      if (!signIn.Succeeded)
      {
        // First sign-in with this provider: link to the account with the same e-mail, or create one.
        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        var user = string.IsNullOrWhiteSpace(email) ? null : await userManager.FindByEmailAsync(email);
        if (user is null)
        {
          user = new IdentityUser { UserName = email ?? $"{info.LoginProvider}-{info.ProviderKey}", Email = email };
          if (!(await userManager.CreateAsync(user)).Succeeded) return Results.Redirect(failed);
        }

        await userManager.AddLoginAsync(user, info);
        await signInManager.SignInAsync(user, isPersistent: false);
      }

      await context.SignOutAsync(IdentityConstants.ExternalScheme);
      return Results.Redirect(SafeReturnUrl(returnUrl));
    });

    // Not rate-limited: the app fetches a token before each sign-in and after session changes.
    app.MapGet("/api/auth/csrf-token", (IAntiforgery antiforgery, HttpContext context) =>
      Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));

    return app;
  }

  /// <summary>Only same-site paths; "//host" and absolute URLs would make this an open redirect.</summary>
  public static string SafeReturnUrl(string? returnUrl) =>
    !string.IsNullOrWhiteSpace(returnUrl) &&
    returnUrl.StartsWith('/') &&
    !returnUrl.StartsWith("//") &&
    !returnUrl.StartsWith("/\\") &&
    Uri.TryCreate(returnUrl, UriKind.Relative, out _)
      ? returnUrl
      : AppPath;
}
