# Database Schema Management

## Current Approach

This project uses `EnsureCreated()` with idempotent schema patching on every start, not EF Core migrations.

### How It Works

1. **Initial Schema**: `db.Database.EnsureCreated()` creates the database and tables if they don't exist
2. **Schema Patching**: Custom code adds columns, tables and indexes that may be missing from older databases
3. **Provider-Specific Logic**: Separate patching logic for PostgreSQL and SQLite

### Files Involved

- `api/Configuration/Database.cs`: `EnsureStrimSchema()` runs at start-up and calls `TryPatchPostgresSchema()` or
  `TryAddSqliteColumn()` for the playlist columns, `EnsureIdentitySchema()` for the Identity tables and profile
  columns, `EnsureTitleMarksSchema()` for `titlemarks` and `EnsurePlaylistSharesSchema()` for `playlistshares`.
- `api/Data/AppDbContext.cs`: Entity configuration and model definition

### Playlists (`playlists`)

One row per saved playlist (`api/Models/Playlist.cs`).

| Column | What it holds |
|--------|---------------|
| `Id` | The playlist's id. |
| `Name` | The owner's name for the playlist (at most 200 characters). |
| `SourceUrl` | The provider's playlist address; it usually carries the provider account's credentials. |
| `SourceName` | The owner's name for the source. |
| `DisabledGroups` | The provider group names the owner switched off (JSON list). |
| `totalchannels` | Number of entries in the source at the last analysis. |
| `groupcount` | Number of groups in the source at the last analysis. |
| `expirationutc` | When the provider subscription expires, as the provider reported it. |
| `ownerid` | The owning user's id. |
| `sharecode` | The random code of the player (share) link. |
| `isactive` | Whether the player link answers. |
| `viewcount` | How often the player link was fetched. |
| `lastviewedutc` | When the player link was last fetched. |
| `autorefreshenabled` | Whether Strim refreshes the source automatically. |
| `watchlistgroup` | The player link starts with a group of the owner's watchlisted films and series. Off by default. |
| `lastrefreshedutc` | When the source was last refreshed. |
| `refreshfailurecount` | Automatic refreshes that failed in a row; a refresh that works sets it back to 0. |
| `sourcehash` | Hash of the last fetched source. |
| `sourceetag` | The provider's `ETag` of the last fetched source. |
| `sourcelastmodifiedutc` | The provider's `Last-Modified` of the last fetched source. |
| `sourcelengthbytes` | Size of the last fetched source in bytes. |
| `sourcecheckedutc` | When the source was last checked. |
| `accountstatus` | Provider account status (for example "Active") as the provider last reported it. |
| `accountmaxconnections` | The provider account's maximum connections. |
| `accountactiveconnections` | The provider account's active connections. |
| `accountcheckedutc` | When the account facts were last read from the provider. |
| `CreatedAt` | When the playlist was created. |
| `UpdatedAt` | When the playlist was last changed. |

### Watchlist group flag (`playlists.watchlistgroup`)

`watchlistgroup` marks a playlist whose player link should start with a group of watchlisted films and series. It
is off by default: SQLite `INTEGER NOT NULL DEFAULT 0`, PostgreSQL `boolean NOT NULL DEFAULT false`, so every
existing playlist reads `false`. `TryAddSqliteColumn` and `TryPatchPostgresSchema` add it idempotently on every
start. The owner switches the flag with `PATCH /api/playlists/{id}/watchlist-group`, and the player-link route
reads it on every request (see `docs/SECURITY.md`, "Watchlist group").

### Profile columns (`AspNetUsers.display_name`, `avatar`, `theme`)

Three nullable columns on the Identity users table hold what a person sets in their profile (#579):
`display_name` (1 to 40 characters, the name Strim shows instead of the user name), `avatar` (`<tone>:<symbol>`
from fixed lists; null is the default initial) and `theme` (`system`, `light` or `dark`; null reads as `system`).
SQLite stores them as `TEXT NULL`, PostgreSQL as `varchar(40)`, `varchar(32)` and `varchar(16)`. `AppDbContext`
maps them as shadow properties of `IdentityUser`, so a new database gets them from `EnsureCreated`;
`Database.EnsureIdentitySchema` adds them to an older table on every start (`ADD COLUMN IF NOT EXISTS` on
PostgreSQL, a `pragma_table_info` check on SQLite). Existing accounts read null in all three.

### Film and series marks (`titlemarks`)

The one extra table the owner approved (18 Nov 2026) holds a user's marks on films and series: `id`, `userid`,
`kind` (`film` or `series`), `titlekey`, `year` (nullable), `displaytitle`, `seen`, `rating` (-1, 0 or 1),
`filmdbid` (nullable: the confirmed TMDB film id on a film mark, the TMDB tv id on a series mark; film and tv ids
overlap, so every reader filters by kind), `watchlist` (on the user's watchlist, default false; marking the title
seen clears it), `watchlistedutc` (nullable: when it was put on the watchlist), `createdutc`, `updatedutc`. The two
watchlist columns are added idempotently to an existing table. `userid` references `AspNetUsers (Id)` with
`ON DELETE CASCADE`, so the marks go with the account. `IX_titlemarks_userid` indexes the user;
two partial unique indexes make a null year its own value: `IX_titlemarks_userid_kind_titlekey_year` on
`(userid, kind, titlekey, year) WHERE year IS NOT NULL` and `IX_titlemarks_userid_kind_titlekey_noyear` on
`(userid, kind, titlekey) WHERE year IS NULL`, so a null year never collides with any real year, 0 included.
`Database.EnsureTitleMarksSchema` creates the table and all three indexes with `IF NOT EXISTS` on every start, for
SQLite and PostgreSQL.

### Playlist shares (`playlistshares`)

Approved by the owner on 19 Nov 2026 (epic #373): one row per person a playlist is shared with read-only.
Columns: `id`, `playlistid`, `label` (the owner's name for the person, at most 100 characters), `role` (default
`viewer`, the only role for now), `tokenhash` (SHA-256 hex of the invitation token, null once accepted),
`tokenexpiresutc`, `vieweruserid`, `createdutc`, `acceptedutc` and `lastopenedutc` (both nullable). Status is
derived: accepted when `vieweruserid` is set, expired when `tokenexpiresutc` has passed, otherwise invited; revoking
and leaving delete the row. `playlistid` references `playlists ("Id")` and `vieweruserid` references
`AspNetUsers (Id)`, both `ON DELETE CASCADE`, so shares go with the playlist and with the viewer's account.
Indexes: `IX_playlistshares_playlistid`, `IX_playlistshares_vieweruserid`, unique `IX_playlistshares_tokenhash` on
`(tokenhash) WHERE tokenhash IS NOT NULL` and unique `IX_playlistshares_playlistid_vieweruserid` on
`(playlistid, vieweruserid) WHERE vieweruserid IS NOT NULL`. `Database.EnsurePlaylistSharesSchema` creates the
table and indexes with `IF NOT EXISTS` on every start, for SQLite and PostgreSQL.

## Rules

- No EF migrations (`AGENTS.md`, `docs/factory/GOAL.md`).
- Only additive columns, added idempotently in `api/Configuration/Database.cs` for SQLite and PostgreSQL.
- A new column or table also goes into `deploy/sanitise/columns.txt` in the same change, and into `sanitise.sql`
  when it can hold personal data. There it sits inside a guarded block (`IF EXISTS` on
  `information_schema.columns`), because the script runs before the candidate adds its columns.

## Index Configuration

Indexes are defined in `AppDbContext.OnModelCreating()`, for example:

```csharp
entity.HasIndex(p => new { p.OwnerId, p.UpdatedAt });
entity.HasIndex(p => p.ShareCode).HasFilter("\"sharecode\" IS NOT NULL");
```

Indexes that an older database may lack are created with `CREATE INDEX IF NOT EXISTS` in the patching code of
`api/Configuration/Database.cs`.
