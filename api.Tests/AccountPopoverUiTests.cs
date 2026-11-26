using System.Text.RegularExpressions;
using Api.Tests.Integration;
using Xunit;

namespace Api.Tests;

/// <summary>Pins the account popover (#581) at its source in main.js and app.css, as the other UI tests do (api.Tests
/// has no JS engine): the one avatar helper, the sign-in wording per method, the popover's keyboard and focus rules
/// and the theme sync with the account.</summary>
public class AccountPopoverUiTests
{
  private static string Read(string file) => File.ReadAllText(Path.Combine(StrimAppFactory.RepositoryRoot(), file)).ReplaceLineEndings("\n");

  private static string Js => Read("main.js");

  private static string Function(string name)
  {
    var js = Js;
    var start = js.IndexOf($"function {name}(", StringComparison.Ordinal);
    Assert.True(start >= 0, $"main.js has no function {name}.");
    return js[start..js.IndexOf("\n}\n", start, StringComparison.Ordinal)];
  }

  private static string[] JsList(string constant) =>
    Regex.Match(Js, $@"const {constant} = \[([^\]]*)\];").Groups[1].Value.Split(", ").Select(s => s.Trim('\'')).ToArray();

  private static string[] ServerList(string field) =>
    Regex.Match(Read("api/Endpoints/ProfileEndpoints.cs"), $@"{field} = \[([^\]]*)\];").Groups[1].Value.Split(", ").Select(s => s.Trim('"')).ToArray();

  [Fact]
  public void AvatarLists_MatchTheServer()
  {
    Assert.Equal(ServerList("AvatarTones"), JsList("AVATAR_TONES"));
    Assert.Equal(ServerList("AvatarSymbols"), JsList("AVATAR_SYMBOLS"));
    Assert.Equal(["violet", "tangerine", "gold", "signal", "ink", "paper"], JsList("AVATAR_TONES"));
  }

  [Theory]
  [InlineData("violet", "background: var(--series-soft); color: var(--series-ink);")]
  [InlineData("tangerine", "background: var(--live-soft); color: var(--live-ink);")]
  [InlineData("gold", "background: var(--movies-soft); color: var(--movies-ink);")]
  [InlineData("signal", "background: var(--accent); color: var(--on-accent);")]
  [InlineData("ink", "background: var(--ink); color: var(--bg);")]
  [InlineData("paper", "background: var(--surface-3); color: var(--ink-2); box-shadow: inset 0 0 0 1px var(--edge);")]
  public void EveryTone_HasItsColours(string tone, string rule) => Assert.Contains($".av.t-{tone} {{ {rule} }}", Read("app.css"));

  [Fact]
  public void AvatarHelper_DrawsTheInitialOrASymbol_InTheTone()
  {
    var helper = Function("avatar");
    Assert.Contains("parts.symbol === 'initial' ? initial(user) : icon(parts.symbol)", helper);
    Assert.Contains("class: `av av-${size} t-${parts.tone}`", helper);
    Assert.Contains("const initial = (user) => (user.name || '').trim().charAt(0) || '?';", Js);
    var css = Read("app.css");
    Assert.Contains("font: 800 calc(var(--s) * .43)/1 var(--font-display); text-transform: uppercase;", css);
    Assert.Contains(".av svg { width: 54%; height: 54%; }", css);
  }

  [Fact]
  public void AvatarHelper_ReadsTheSavedAvatar_ElseTheInitialInAHashedTone()
  {
    var parts = Function("avatarParts");
    Assert.Contains("const [tone, symbol] = String(user.avatar || '').split(':');", parts);
    Assert.Contains("if (AVATAR_TONES.includes(tone) && AVATAR_SYMBOLS.includes(symbol)) return { tone, symbol };", parts);
    // The three tones the account button picked from before #581, by the same hash as the share avatars.
    Assert.Contains("return { tone: AVATAR_TONES[pick(`${user.userName}:tone`, 3)], symbol: 'initial' };", parts);
  }

  [Theory]
  [InlineData("microsoft", "Signed in with Microsoft as")]
  [InlineData("google", "Signed in with Google as")]
  [InlineData("password", "Signed in as")]
  [InlineData("access", "Signed in through Cloudflare Access as")]
  public void SignInWording_PerMethod(string method, string line) => Assert.Contains($"  {method}: '{line}',", Js);

  [Fact]
  public void SignInWording_NamesTheUserNameForPassword_ElseTheIdentity()
  {
    Assert.Contains("return [SIGN_IN_LINES[method] || 'Signed in as', method === 'password' ? user.userName : user.signIn?.identity];", Function("signInLines"));
  }

  [Fact]
  public void AccountButton_ShowsTheAvatarAndShownName_NotTheEmail()
  {
    var button = Function("updateAccountButton");
    Assert.Contains("accountButton.replaceChildren(avatar(state.user), h('span', { class: 'name', text: state.user.name }));", button);
    Assert.DoesNotContain("email", button);
    Assert.DoesNotContain("email", Function("showAccountMain"));
  }

  [Fact]
  public void Popover_IsADialog_ThatClosesOnEscOutsideAndTheButton_AndReturnsFocus()
  {
    Assert.Contains("h('div', { class: 'pf-pop', role: 'dialog', 'aria-label': 'Account', hidden: true })", Js);
    var toggle = Function("toggleAccountPop");
    Assert.Contains("if (!accountPop.hidden) return closeAccountPop();", toggle);
    Assert.Contains("accountButton.setAttribute('aria-expanded', 'true');", toggle);
    Assert.Contains("showAccountMain(accountPop).focus();", toggle);
    var close = Function("closeAccountPop");
    Assert.Contains("accountButton.setAttribute('aria-expanded', 'false');", close);
    Assert.Contains("accountButton.focus();", close);
    Assert.Contains("const closeOnEscape = (e) => { if (e.key === 'Escape') closeAccountPop(); };", Js);
    Assert.Contains("if (!accountPop.contains(e.target) && !accountButton.contains(e.target)) closeAccountPop();", Js);
    Assert.Contains("state.user && wideAccount.matches ? toggleAccountPop() : openAccount()", Js);
    Assert.Contains("const wideAccount = window.matchMedia('(min-width: 768px)');", Js);
    Assert.Contains(".account[aria-expanded=\"true\"] { border-color: var(--ink); background: var(--surface-2); }", Read("app.css"));
  }

  [Fact]
  public void AvatarPane_BackDropsAndDoneSaves_AndBothReturnToTheMainPane()
  {
    var pane = Function("showAvatarPane");
    Assert.Contains("const backToMain = () => showAccountMain(root).focus();", pane);
    Assert.Contains("saveProfile({ avatar: `${draft.tone}:${draft.symbol}` }).then(backToMain,", pane);
    Assert.Contains("'aria-label': 'Back', onclick: backToMain", pane);
    Assert.Contains("back.focus();", pane);
    Assert.Contains("'aria-label': 'Change avatar', onclick: () => showAvatarPane(root)", Function("showAccountMain"));
  }

  [Fact]
  public void Name_SavesOnEnterAndBlur_AndEscRestores()
  {
    var main = Function("showAccountMain");
    Assert.Contains("await saveProfile({ displayName: value || null });", main);
    Assert.Contains("name.addEventListener('blur', saveName);", main);
    Assert.Contains("if (e.key === 'Enter') saveName();", main);
    Assert.Contains("e.stopPropagation();\n    name.value = state.user.name;", main);
    Assert.Contains("error.textContent = err.message;", main);
  }

  [Fact]
  public void Theme_AppliesAtOnceAndSaves_AndTheAccountWinsOnLoad()
  {
    var main = Function("showAccountMain");
    Assert.Contains("window.strimTheme.apply(value);", main);
    Assert.Contains("saveProfile({ theme: value })", main);
    Assert.Contains("if (state.user || me.reason?.status === 401) window.strimTheme.apply(state.user?.theme || 'system');", Function("loadSession"));
    Assert.Contains("window.strimTheme.apply(state.user?.theme || 'system');", Function("afterAuthChange"));
  }
}
