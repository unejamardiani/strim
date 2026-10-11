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
