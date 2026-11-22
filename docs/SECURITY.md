# Security

What Strim protects, and where in the code each control lives.

## Assets worth protecting

- **Provider URLs.** An IPTV link usually embeds the provider username and password. It is stored
  only for saved playlists and is never part of a player (share) link.
- **Player links.** `GET /api/playlists/{id}/share/{code}` is unauthenticated by design, so IPTV
  players can fetch it. The random code (32 bytes, URL-safe Base64) is the only secret.
- **Accounts.** ASP.NET Core Identity with cookie sessions.

## Controls

| Area | Control | Code |
|------|---------|------|
| SSRF | Only http/https; private, loopback, link-local and reserved addresses are refused before the request and again at connect time (DNS-rebinding safe); every redirect target is re-validated. | `api/Services/SecurityHelpers.cs`, `api/Configuration/PlaylistServices.cs`, `api/Services/PlaylistSourceFetcher.cs` |
| CSRF | Sign-in, registration and every playlist mutation require the `X-CSRF-TOKEN` header from `GET /api/auth/csrf-token`. | `api/Endpoints/EndpointSupport.cs` (`RequireCsrfToken`) |
| Cookies | `__Host-` prefixed, `HttpOnly`, `Secure` in production. `SameSite=None` in production for cross-origin clients, `Lax` in development (browsers reject `None` without HTTPS). | `api/Configuration/Authentication.cs`, `Security.cs` |
| CORS | Credentialed CORS only for origins listed in `ALLOWED_ORIGINS`; none in production otherwise (the UI is same-origin). Development allows localhost origins. | `api/Configuration/Security.cs` |
| Rate limits | Per IP per minute: auth 20, analyze/generate 30, share links 30, general 100; per user: link toggle/regenerate 10. | `api/Configuration/Security.cs` |
| Ownership | Playlists are always queried by owner; another user's playlist answers 404, not 403. Share links answer the same 404 for unknown ID, wrong code and a link that is turned off. | `api/Endpoints/PlaylistEndpoints.cs`, `ProcessingEndpoints.cs` |
| Shared playlists | An accepted viewer reaches only the Library routes of a shared playlist (`library/open`, `search`, `group-entries`, `film-card`, `series-card`), checked against the database on every request, with a `lib-…` key bound to playlist and user that editor routes refuse. The hidden groups come from the database, never the request; provider failures reach a viewer as one neutral message. Every owner route answers a viewer with the stranger's 404, and `GET /api/playlists` and `/overview` list owned playlists only. `ViewerBarrierTests` classifies every route under `/api/playlists` (a new unclassified route fails the tests) and checks that no viewer response carries the source address, its host, credentials, stream URLs or the share code. An accepted viewer's watchlist can appear in the owner's player link when the owner switched the Watchlist group on. | `api/Endpoints/ShareEndpoints.cs`, `LibraryEndpoints.cs`, `ProcessingEndpoints.cs`, `api.Tests/ViewerBarrierTests.cs` |
| Watchlist group | With the playlist's Watchlist group switched on, the player link's output starts with the groups `Watchlist` (films) and `Watchlist · Series`: copies of the entries of switched-on, non-adult groups whose titles the owner or a person with an accepted invitation put on their watchlist. Everyone who holds the player link sees which titles of the list were bookmarked, without names, labels or addresses and without knowing by whom. Contributors are the owner, taken from the playlist row, and the users of the accepted `playlistshares` rows of that playlist, read from the database on every request and never from the request; open and expired invitations and viewers of other playlists do not contribute, and a playlist without an owner id gets no group and reads no marks. A revoked invitation or a person who leaves stops contributing at the next request, while a copy a player already holds and the last good copy keep the titles until they are replaced; a deleted account's marks and shares are deleted with it. Invited people have no switch and are not asked; while the group is on they are told in their Library, and the owner on the Sharing page, and the answer of `library/open` carries the `watchlistGroup` flag for that. The switch is off by default and only the owner changes it, behind the CSRF header. There is no new anonymous route and no new fetch: the share route keeps its code, 404 rule, rate limit and gate, and decides the 404 before any mark is read. Limits: 200 titles of the merged list, 2,000 entries. Nothing logged names a title, a mark key or a person. | `api/Endpoints/ProcessingEndpoints.cs` (share route), `api/Services/WatchlistGroup.cs`, `api.Tests/Integration/WatchlistGroupShareTests.cs` |
| Library posters | `POST /api/playlists/{id}/library/posters` (owner or accepted viewer, else 404; 404 without a TMDB token; general rate limit; read-only, so no CSRF) answers poster addresses for 1 to 24 entries. Only the cleaned title and year of each entry's mark key, or the caller's stored TMDB id, leave Strim, never the entry name, group, season, episode, user or playlist; live channels, other types and adult groups send nothing. One request makes at most 8 TMDB calls; the rest, and everything after TMDB answers rate-limited or unavailable, comes back `pending` without a retry. Search results are remembered in memory only (`LibraryPosterMemo`: by mark key, poster path or "none", at most 20,000 entries for 30 days, oldest out first); stored ids are per user and not remembered. The browser loads the image straight from `image.tmdb.org`; Strim never fetches or proxies it, and nothing logged names a title or user. | `api/Endpoints/LibraryPostersEndpoints.cs`, `api/Services/LibraryPosterMemo.cs` |
| Library suggestions | `GET /api/playlists/{id}/library/suggestions?source=trending` or `source=liked` (owner or accepted viewer, else 404; 404 without a TMDB token; general rate limit; read-only) answers the titles TMDB lists as trending this week, or the titles TMDB recommends for one of the caller's 10 newest thumbs-up marks (rotating by UTC day, at most 3 seeds tried), that are in the caller's list. For trending only the fixed paths `trending/movie/week` and `trending/tv/week` and a page number (2 or 3) leave Strim; for liked only `movie/{id}/recommendations` or `tv/{id}/recommendations` with a TMDB id and a page number (2) and, for a seed without a stored TMDB id, its cleaned title and year in the same search the film and series cards send. Never a group, user or playlist; the matching against the list happens on the server, so only titles in the list reach the browser, and a viewer's hidden groups stay hidden (a viewer's seeds are their own marks). One request makes at most 8 TMDB calls (trending needs at most 6) and none after an answer that is not Ok (for a liked seed's search: rate limited or unavailable), without a retry. TMDB's trending answers are cached by `TmdbCache` for 6 hours and recommendations for 7 days; the suggestions themselves are neither memoised nor stored, and nothing logged names a title or user. | `api/Endpoints/LibrarySuggestionsEndpoints.cs`, `api/Services/LibrarySuggestions.cs` |
| Open redirects | External sign-in only returns to local paths (`//host` and absolute URLs fall back to `/app`). | `AuthEndpoints.SafeReturnUrl` |
| Passwords | At least 10 characters with upper case, lower case and a digit; lockout for 10 minutes after 5 failures. | `api/Configuration/Authentication.cs` |
| Resource use | 50 MiB raw-text bodies, one raw upload and one processing job at a time, bounded downloads, disk-backed cache with size limits. | `ProcessingEndpoints.UseAnalyzeBodyGate`, `PlaylistJobGate`, `PlaylistCacheOptions` |
| Logging | Share codes are logged truncated (first and last four characters). Of a provider URL only the host is logged. | `PlaylistEndpoints` |
| Frontend | No third-party scripts, styles or fonts. Each page carries a CSP without `unsafe-inline`; never add inline `<script>`, `<style>` or `style=""`. | `*.html` |

## Deployment notes

- Behind a reverse proxy, `X-Forwarded-*` is trusted from any hop unless `TRUSTED_PROXIES` lists
  the proxy addresses. Do not expose the container port directly to the internet.
- On the public instance, `/app` and `/api` sit behind Cloudflare Access except the share route,
  which players must reach without a login.
- Set `PUBLIC_SHARE_BASE_URL` when player links must use a different host than the app.

## Reporting

Please report vulnerabilities privately through a GitHub security advisory on
[unejamardiani/strim](https://github.com/unejamardiani/strim/security/advisories/new).
