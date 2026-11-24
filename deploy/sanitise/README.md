# Sanitised production copy for staging

Vatra verifies every Strim candidate on staging (`strim-stg.plis.dev`) against a fresh, sanitised copy of the
production database before it promotes the image to production. The copy is made on the Coolify host and never
leaves it. The design is in plis-toolkit `docs/factory/staging-promotion-design.md`, section 4.

These files are owner paths: Vatra does not change them without the owner's approval.

| File | Holds |
|---|---|
| `columns.txt` | Every column of every table, one `<table>.<column> keep\|sanitised` per line. |
| `sanitise.sql` | Rewrites every `sanitised` column. Refuses to run on a database whose name does not end in `_stg`. One transaction. |
| `check-coverage.sql` | Fails when the connected database has a column that `columns.txt` does not list. Prints column names only. |

## What is sanitised

- Accounts (`AspNetUsers`): user name and e-mail become `<id>@example.invalid`; password hash, phone number and
  two-factor flag are cleared; the security stamp is new. No production password or cookie works on staging.
- External logins (`AspNetUserLogins`): the provider key no longer names a Google, Microsoft or GitHub account.
- Stored tokens (`AspNetUserTokens`) and user claim values are cleared or replaced.
- Playlists: the source URL (it carries the IPTV provider's username and password) becomes
  `https://source.example.invalid/playlists/<id>.m3u`; name and source name become neutral text; auto refresh is
  off; share codes are new.
- Shares: the label (the owner's name for a person) becomes neutral text; open invitation token hashes are new.

Kept on purpose: ids and foreign keys, timestamps, counters, provider account facts (status, connection counts),
the provider's group names in `DisabledGroups`, and film and series titles in `titlemarks`. None of them names a
person once the accounts are sanitised.

## Running it

Run as the staging role, connected to `strim_stg`, never as superuser:

```bash
psql -X -v ON_ERROR_STOP=1 -d strim_stg -f sanitise.sql
psql -X -q -v ON_ERROR_STOP=1 -d strim_stg -f check-coverage.sql < columns.txt
```

The coverage check reads `columns.txt` from standard input. It exits with status 3 and lists the missing columns
when the schema has a column the file does not cover. It runs on the restored copy and again after the
candidate's schema update on staging. Strim has no EF migrations: `api/Configuration/Database.cs` adds columns on
start, so a change that adds a column there must add it to `columns.txt` (and to `sanitise.sql` when it can hold
personal data) in the same pull request.

To list the columns of a fresh schema, start Strim once against an empty PostgreSQL database and run the check:
every missing column appears in its output.

## Outbound effects on staging

With sanitised data and the staging contract (`coolify-apps` `apps/strim/staging/`), staging sends nothing on
behalf of a production user:

| Path | On staging |
|---|---|
| `PlaylistRefreshService` (source refresh and provider account lookup every six hours, first run at start) | No playlist has auto refresh on, and every copied source URL is on `.invalid`, which never resolves (RFC 6761). |
| Google, Microsoft and GitHub sign-in | Off: staging has no OAuth client secrets, so the providers are not registered. Testers use a local password account (`/api/auth/register`). |
| TMDB (`TMDB_READ_ACCESS_TOKEN`) | Off unless the owner sets a staging token. TMDB requests carry film titles, not personal data. |
| Share feeds (`PUBLIC_SHARE_BASE_URL`) | Point at `strim-stg.plis.dev`; copied share codes are new, so no player link from production works. |
| E-mail | Strim sends none. |
| A source URL a tester enters | Fetched as in production; that is the tester's own action. |

The playlist file cache lives inside the container and is not copied, so a copied playlist shows its stored
counts but cannot be reprocessed on staging.
