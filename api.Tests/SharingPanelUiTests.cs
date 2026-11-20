using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the owner's sharing page (#381) in main.js and app.css (no JS engine); endpoints: ShareManagementTests.</summary>
public class SharingPanelUiTests
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

  private static string Sharing() => Between(Read("main.js"), "// ---------- Sharing (#381) ----------", "// ---------- Invitation (#382) ----------");

  [Fact]
  public void ShowsLinkOnce_WithCopy()
  {
    var main = Read("main.js");
    Assert.Contains("if (target.name === 'sharing') return showSharing(playlist);", Between(main, "function route()", "function closeSheets()"));
    Assert.Contains("item('people', 'Share', () => navigate(`#/p/${playlist.id}/sharing`))", main);
    Assert.Contains("sharingLine(playlist.id)", Between(main, "function playlistCard(", "function menuIcon("));
    Assert.Contains("state.shareAccessNote = config.status === 'fulfilled' ? String(config.value?.shareAccessNote || '').trim() : '';", main);

    // The token comes back once from the create call and lives only in the page's state, never in storage.
    var sharing = Sharing();
    Assert.Contains("api(`/playlists/${sh.playlist.id}/shares`, { method: 'POST', body: { label } })", sharing);
    Assert.Contains("#/invite/${result.inviteToken}", sharing);
    Assert.DoesNotContain("Storage", sharing);
    Assert.Contains("onclick: () => copyText(link.url) }, icon('copy'), 'Copy')", sharing);
    Assert.Contains("h('b', { text: 'Shown only now.' }),\n          ' Copy it before you close this. If it gets lost, withdraw the invite and create a new one.'", sharing);
    Assert.Contains("sharing.link = null; sharing.newId = null; renderSharing(); } }, 'Invite someone else'", sharing);
    Assert.Contains("toast('Invite created');", sharing);
    Assert.Contains("'Give a name first'", sharing);
    Assert.Contains("if (err.status === 409) sh.full = true;", sharing);
    Assert.Contains("'All 10 places are taken. Remove someone or delete an old invite to invite someone else.'", sharing);
    Assert.Contains("state.shareAccessNote ? h('p', { class: 'share-hint', text: state.shareAccessNote }) : null", sharing);
    Assert.DoesNotContain("style", sharing);
  }

  [Fact]
  public void ListsPeopleWithStatus()
  {
    var sharing = Sharing();
    Assert.Contains("accepted: ['is-active', 'Active', 'Remove', 'Access removed'], invited: ['is-invited', 'Invited', 'Withdraw', 'Invite withdrawn'], expired: ['is-expired', 'Expired', 'Delete', 'Invite deleted']", sharing);
    Assert.Contains("['Person', 'Status', 'Access', 'Last opened']", sharing);
    Assert.Contains("`${shares.length} of ${MAX_SHARES} places`", sharing);
    Assert.Contains("'Only you so far'", sharing);
    // "Last opened" is written at most once an hour and never shown more precisely.
    var opened = Between(sharing, "function lastOpened(", "function shareAvatar(");
    Assert.Contains("if (!value) return 'Not yet';", opened);
    Assert.Contains("if (hours < 1) return 'Within the hour';", opened);
    Assert.DoesNotContain("min", opened);

    var css = Read("app.css");
    Assert.Contains(".share-tag.is-active::before { content: \"\"; width: 7px; height: 7px; border-radius: 50%; background: var(--ok); }", css);
    // On a phone the table gives way to the people rows.
    Assert.Contains("@media (min-width: 600px) { .share-list { display: none; } }", css);
    Assert.Contains("  .sharing-head .btn, .share-table { display: none; }", css);
  }

  [Fact]
  public void Revoke_AsksFirst()
  {
    var sharing = Sharing();
    // Only an accepted share asks first; withdrawing or deleting an unused invite takes effect at once.
    Assert.Contains("onclick: () => (s.status === 'accepted' ? confirmRemove(s) : deleteShare(s))", sharing);
    var confirm = Between(sharing, "function confirmRemove(", "async function deleteShare(");
    Assert.Contains("menuTitle.textContent = `Remove ${s.label}?`;", confirm);
    Assert.Contains("at once, also in a Library that is open right now. Their own marks stay with them. To let them back in, create a new invite.", confirm);
    Assert.Contains("menuDialog.close(); deleteShare(s); } }, 'Remove access')", confirm);
    Assert.Contains("keep.focus();", confirm);
    Assert.DoesNotContain("api(", confirm);
    Assert.Contains("api(`/playlists/${sh.playlist.id}/shares/${s.id}`, { method: 'DELETE' })", sharing);
    Assert.Contains(".sheet.confirm { max-width: 400px; }", Read("app.css"));
  }
}
