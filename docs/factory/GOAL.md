# Goal

> Agents read this file before every run and may not change it (`docs/factory/*` is protected). The owner
> stated this goal on 14 Nov 2026; it replaces the first draft. On 18 Nov 2026 he added target 3, the library;
> cleaning moved from 3 to 4 and smart cleaning from 4 to 5, so issues written before that day use the old numbers.

## Product

Strim is an open-source (MIT) editor for IPTV (M3U/M3U8) playlists. A person pastes a provider's link, switches off
the groups they never watch and gets either a cleaned `.m3u` download or a player link that applies their choices
each time the player refreshes. It is used on a phone or on the sofa, so it must stay quick and simple. Strim never
hosts, stores or proxies streams; it only edits the playlist.

## Current target

Strim should be good enough that one would pay for it, without anyone being asked to: the best possible experience
for the owner's own daily use. It stays free. There is no business model, no billing and no pricing.

The owner manages his playlist from a phone or laptop and watches on an LG television (webOS) with the Smart IPTV
app and on an Apple TV with IPTV apps. In that order of importance:

1. **What Strim hands to the players is correct and dependable.** The playlist keeps everything a player needs
   (the header with the programme-guide address, option lines of an entry, attributes, order), and the player link
   answers with the last good list when the provider or Strim has a problem.
2. **Strim is a companion to the television apps.** It answers what the apps do not: in which group a title is,
   when the provider subscription expires, whether the link still works and when it was last fetched, what changed
   in the provider's list.
3. **Strim is a library of what the list holds.** A read-only mode, apart from cleaning, to find films and series
   in the owner's own list (the cleaned list, the full one on demand), see what a film is, walk to similar films,
   and remember what was seen and liked. Nothing in it changes the playlist.
4. **Cleaning up is quick and safe.** Few taps to find and switch groups, no accidental bulk changes, and a way back.
5. **Smart cleaning comes last:** duplicates, best variants, language and country preferences. Report first, remove
   later, because wrong removals are worse than none.

Each improvement fits in one pull request and comes with its tests.

## Out of scope

Never touched without the owner deciding first; a story that needs one of these stops and asks:

- authentication, sign-in providers and CSRF protection;
- the security of player (share) links: their codes, lifetime, access and the anonymous share route;
- the database schema beyond additive columns (no EF migrations, no renamed or dropped columns or tables);
- hosting, Docker, Docker Compose, CI workflows and anything in `tools/`;
- the marketing copy of the landing page, guides and comparison page;
- licences, including the font licences in `fonts/`;
- anything that stores, caches, relays or proxies streams, or plays them;
- new NuGet packages or other dependencies.

## Rules

- `AGENTS.md` and `DESIGN.md` are binding: no EF migrations, CSRF on every state-changing endpoint, playlist work
  through `PlaylistPipeline`, a frontend without a build step and without inline scripts or styles, and the
  routing order in `Program.cs`.
- No real provider URLs, credentials, share links or personal data in code, tests, fixtures or documentation.
- No credentials in the repository; configuration comes from the environment.
- Production changes only through the owner: a merge to `main` publishes an image, but the factory never deploys.

## Quality bar

- Every behaviour change has a test in `api.Tests` that fails without it.
- A frontend-only change still comes with a test wherever the API is involved. A change to the editor files alone
  (`main.js`, `app.html`, `app.css`, `theme.css`), which have no test harness, may come without a test file when the
  build agent writes manual check steps; the reviewer checks them and they appear in the pull request.
- `dotnet test strim.sln` passes (the factory's gate and the CI workflow run it).
- The reviewer finds no blocking issue against the story's acceptance criteria, `AGENTS.md` or `DESIGN.md`.
- A story is done when its pull request is merged with every acceptance criterion covered by a passing test (or, for
  an editor-only change, by manual check steps), and nothing outside the story's scope changed.

## Decisions and merges

The owner delegated on 14 Nov 2026: "It is nothing business critical and i would like to give you some autonomy."

- Questions that are not critical are decided by the factory's advisor (or by the assistant) and recorded on the
  story. The owner can overrule any of them with a comment.
- Pull requests merge automatically once the gate, the review and the checks are green, unless they touch a guarded
  area; those wait for the owner's approval.
- Critical questions stay with the owner: security, authentication, the data model, dependencies, deployment, legal
  matters and licences, money, and anything beyond this goal.
- Guarded areas: sign-in, CSRF and the security helpers, the fetching of provider links, the database schema and the
  data models, the middleware order in `Program.cs`, the file with the public player-link route, CI, Docker,
  dependencies and the factory's own configuration.
