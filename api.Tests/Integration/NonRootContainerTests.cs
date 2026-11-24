using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Tests.Integration;

/// <summary>
/// The image runs as a non-root user: the keys that sign cookies live in a directory that user owns,
/// and the Dockerfile's final stage names that user and every path the app writes.
/// </summary>
public sealed class NonRootContainerTests : IDisposable
{
  private readonly string _keys = Path.Combine(Path.GetTempPath(), $"strim-keys-{Guid.NewGuid():N}");

  public void Dispose()
  {
    if (Directory.Exists(_keys)) Directory.Delete(_keys, recursive: true);
  }

  [Fact]
  public async Task Keys_are_written_to_the_configured_directory()
  {
    using var factory = new StrimAppFactory();
    using var host = factory.WithWebHostBuilder(b => b.UseSetting("DATA_PROTECTION_KEYS_PATH", _keys));

    using var response = await host.CreateClient().GetAsync("/api/auth/csrf-token");

    response.EnsureSuccessStatusCode();
    Assert.NotEmpty(Directory.GetFiles(_keys, "key-*.xml"));
  }

  [Fact]
  public void Keys_already_in_the_directory_are_read_after_a_restart()
  {
    string protectedText;
    using (var first = new StrimAppFactory())
    using (var host = first.WithWebHostBuilder(b => b.UseSetting("DATA_PROTECTION_KEYS_PATH", _keys)))
    {
      protectedText = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("session");
    }
    var keysBefore = Directory.GetFiles(_keys, "key-*.xml");

    using var second = new StrimAppFactory();
    using var restarted = second.WithWebHostBuilder(b => b.UseSetting("DATA_PROTECTION_KEYS_PATH", _keys));

    Assert.Equal("session", restarted.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedText));
    Assert.Equal(keysBefore, Directory.GetFiles(_keys, "key-*.xml"));
  }

  [Fact]
  public void Final_stage_runs_as_the_image_user_with_explicit_writable_paths()
  {
    var dockerfile = File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), "Dockerfile"));
    var finalStage = dockerfile[dockerfile.IndexOf("FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final", StringComparison.Ordinal)..];

    var lines = finalStage.Split('\n').Select(l => l.Trim()).ToList();
    var user = lines.FindIndex(l => l == "USER $APP_UID");
    Assert.True(user > lines.FindLastIndex(l => l.StartsWith("RUN ", StringComparison.Ordinal)), "USER must follow every RUN step");
    Assert.True(user < lines.FindIndex(l => l.StartsWith("ENTRYPOINT ", StringComparison.Ordinal)), "USER must precede ENTRYPOINT");

    // App files are copied without --chown, so they stay root's; only data and keys go to the user.
    Assert.DoesNotContain("--chown", finalStage);
    Assert.Contains("chown -R $APP_UID:$APP_UID /app/data /home/app/.aspnet", finalStage);
    Assert.Contains("DATA_PROTECTION_KEYS_PATH=/home/app/.aspnet/DataProtection-Keys", finalStage);
    Assert.Contains("PlaylistCache__Directory=/tmp/strim-playlists", finalStage);
    Assert.Contains("TMPDIR=/tmp", finalStage);
    Assert.Matches(new Regex(@"apt-get install[^\n]*\bcurl\b"), finalStage);
  }
}
