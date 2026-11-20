using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Pins the invitation page and the "Shared with you" cards (#382) in main.js, app.css, theme.css and DESIGN.md at
/// their source, as the other UI tests do (api.Tests has no JS engine). The accept, list and leave endpoints are
/// covered by ShareAcceptTests and ShareManagementTests.
/// </summary>
public class SharedWithMeUiTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file));

  private static string Between(string source, string start, string end)
  {
    var from = source.IndexOf(start, StringComparison.Ordinal);
    Assert.True(from >= 0, $"'{start}' not found.");
    var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
    Assert.True(to > from, $"'{end}' not found after '{start}'.");
    return source[from..to];
  }

  private static string Invite() => Between(Read("main.js"), "// ---------- Invitation (#382) ----------", "// ---------- Shared with you (#382) ----------");

  private static string Shared() => Between(Read("main.js"), "// ---------- Shared with you (#382) ----------", "// ---------- Editor ----------");

  [Fact]
  public void InviteRoute_RemovesTokenFromAddress()
  {
    var main = Read("main.js");
    Assert.Contains("const invite = /^\\/invite(?:\\/([^/?#]+))?$/.exec(path);", Between(main, "function parseRoute()", "function navigate("));

    // The token goes to sessionStorage and the address keeps only #/invite, before anything is rendered.
    var route = Between(main, "function route()", "function closeSheets()");
    Assert.Contains("if (target.name === 'invite' && target.token) {", route);
    Assert.Contains("setInviteToken(target.token);", route);
    Assert.Contains("history.replaceState(null, '', '#/invite');", route);
    Assert.Contains("currentHash = '#/invite';", route);
    Assert.True(route.IndexOf("history.replaceState(null, '', '#/invite');", StringComparison.Ordinal)
      < route.IndexOf("return showInvite();", StringComparison.Ordinal));

    var invite = Invite();
    Assert.Contains("sessionStorage.setItem(INVITE_KEY, token)", invite);
    Assert.DoesNotContain("localStorage", invite);
    // Accepting sends the token in the body (api() adds the CSRF token), never in the address.
    Assert.Contains("api('/shares/accept', { method: 'POST', body: { token } })", invite);
    Assert.Contains("h('h1', { text: 'Someone shared a list with you' })", invite);
    Assert.Contains("'aria-hidden': 'true'", Between(invite, "function inviteTicket(", "function showInvite("));
  }

  [Fact]
  public void SignedOut_KeepsTokenThroughSignIn()
  {
    var main = Read("main.js");
    var route = Between(main, "function route()", "function closeSheets()");
    // The invitation page is shown before the guest redirect, so a signed-out person sees it too.
    Assert.True(route.IndexOf("return showInvite();", StringComparison.Ordinal) < route.IndexOf("if (!state.user) {", StringComparison.Ordinal));
    // An external sign-in comes back without the hash: a stored token brings the invitation back.
    Assert.Contains("if (target.name === 'home' && state.user && inviteToken()) return navigate('#/invite', { replace: true });", route);
    // Signing out ("Not you? Switch") keeps the page and the token.
    Assert.Contains("location.hash !== '#/invite'", Between(main, "function afterAuthChange()", "// ---------- Router ----------"));
    Assert.DoesNotContain("setInviteToken", Between(main, "async function signOut()", "function afterAuthChange()"));

    var invite = Invite();
    Assert.Contains("'Sign in to accept'", invite);
    Assert.Contains("'Create account', () => openAccount()", invite);
    Assert.Contains("h('a', { href: '#/invite', onclick: (e) => { e.preventDefault(); signOut(); } }, 'Not you? Switch')", invite);
    // Only "Not now", an answer from the server and a finished acceptance drop the token.
    Assert.Contains("function notNow() {\n  setInviteToken('');", invite);
  }

  [Fact]
  public void InvalidLink_ShowsOneMessage()
  {
    var invite = Invite();
    Assert.Contains("if (err.status === 404 || err.status === 400) {", invite);
    Assert.Contains("showInvite({ kind: err.status === 404 ? 'void' : 'own' });", invite);
    Assert.Contains("if (!outcome && !inviteToken()) outcome = { kind: 'void' };", invite);
    // Used, withdrawn and expired share one message.
    const string message = "'This invitation does not work any more'";
    Assert.Equal(1, Count(invite, message));
    Assert.Contains("'It was already used, withdrawn or is older than 7 days. Ask the person who sent it for a new link.'", invite);
    Assert.DoesNotContain("expired'", invite);
    Assert.Contains("'This is your own playlist'", invite);

    Assert.Contains(".ticket.is-void { filter: grayscale(1); opacity: .62; }", Read("app.css"));
  }

  [Fact]
  public void SharedCards_HaveNoEditorActions()
  {
    var main = Read("main.js");
    Assert.Contains("loadSharedWithMe(shared);", Between(main, "function showHome()", "async function loadOverview()"));

    var shared = Shared();
    Assert.Contains("api('/shares/mine')", shared);
    Assert.Contains("api(`/shares/mine/${item.playlistId}`, { method: 'DELETE' })", shared);
    Assert.Contains("`cover cover-${pick(item.playlistId, 6) + 1}`", shared);
    Assert.Contains("'Read-only'", shared);
    Assert.Contains("`Shared by ${owner}${since}`", shared);
    Assert.Contains("'Remove from my lists'", shared);
    // Viewer copy: no entry count, no editor or player-link action, no Off badge, never "your list".
    foreach (var editorOnly in new[] { "totalChannels", "Edit groups", "shareUrl", "linkBox", "playlistMeta", "playlistFacts",
      "Copy player link", "Download", "switched-off", "'Off'", "your list'", "`#/p/${item.playlistId}`" })
      Assert.DoesNotContain(editorOnly, shared);
    // Dynamic values are classes, never inline styles.
    Assert.DoesNotContain("style", shared);
    Assert.DoesNotContain("style", Invite());

    var css = Read("app.css");
    for (var n = 1; n <= 6; n++) Assert.Contains($".cover-{n} {{ --cover: radial-gradient(", css);
    Assert.Contains(".btn-xs::after, .icon-btn.is-sm::after { content: \"\"; position: absolute; inset: -6px -2px; }", css);
    Assert.Contains(".btn-danger { background: var(--danger); color: var(--on-danger); }", css);
    Assert.Equal(2, Count(Read("theme.css"), "--on-danger:"));
    Assert.Contains("32–36px", Read("DESIGN.md"));
  }

  private static int Count(string source, string value) => source.Split(value).Length - 1;
}
