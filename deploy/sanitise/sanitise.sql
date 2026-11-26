-- Makes a restored copy of the Strim production database safe for staging.
-- Run as the staging role, connected to the staging database only:
--   psql -X -v ON_ERROR_STOP=1 -d strim_stg -f sanitise.sql
-- Every column marked "sanitised" in columns.txt is rewritten here. One transaction: all or nothing.

\set ON_ERROR_STOP on

BEGIN;

-- Refuse to run anywhere but a staging copy.
DO $$
BEGIN
  IF current_database() NOT LIKE '%\_stg' THEN
    RAISE EXCEPTION 'sanitise.sql runs only on a *_stg database, not on %', current_database();
  END IF;
END $$;

-- Accounts: addresses on the reserved .invalid domain, no password, no phone, no two-factor secret.
-- A display name becomes a neutral 'Person <id prefix>'; accounts without one keep none.
-- Production passwords and sessions stop working; a fresh security stamp invalidates any copied cookie.
UPDATE "AspNetUsers" SET
  "UserName" = left("Id", 200) || '@example.invalid',
  "NormalizedUserName" = upper(left("Id", 200) || '@example.invalid'),
  "Email" = CASE WHEN "Email" IS NULL THEN NULL ELSE left("Id", 200) || '@example.invalid' END,
  "NormalizedEmail" = CASE WHEN "Email" IS NULL THEN NULL ELSE upper(left("Id", 200) || '@example.invalid') END,
  "PasswordHash" = NULL,
  "SecurityStamp" = upper(replace(gen_random_uuid()::text, '-', '')),
  "PhoneNumber" = NULL,
  "PhoneNumberConfirmed" = false,
  "TwoFactorEnabled" = false,
  "display_name" = CASE WHEN "display_name" IS NULL THEN NULL ELSE 'Person ' || left("Id", 8) END;

UPDATE "AspNetUserClaims" SET "ClaimValue" = 'sanitised-' || "Id"
  WHERE "ClaimValue" IS NOT NULL;

-- External logins keep their provider and row count; the key no longer names a real account.
UPDATE "AspNetUserLogins" SET
  "ProviderKey" = 'sanitised-' || gen_random_uuid()::text,
  "ProviderDisplayName" = "LoginProvider";

UPDATE "AspNetUserTokens" SET "Value" = NULL;

-- Playlists: no provider credentials, no outbound refresh, new share codes, neutral names.
-- .invalid never resolves (RFC 6761), so a fetch of a copied source fails without leaving the host.
UPDATE playlists SET
  "Name" = 'Playlist ' || "Id"::text,
  "SourceName" = CASE WHEN "SourceName" IS NULL THEN NULL ELSE 'source-' || "Id"::text END,
  "SourceUrl" = CASE WHEN "SourceUrl" IS NULL OR "SourceUrl" = '' THEN "SourceUrl"
                     ELSE 'https://source.example.invalid/playlists/' || "Id"::text || '.m3u' END,
  autorefreshenabled = false,
  sharecode = CASE WHEN sharecode IS NULL THEN NULL
                   ELSE rtrim(translate(encode(sha256(convert_to(gen_random_uuid()::text, 'UTF8')), 'base64'), '+/', '-_'), '=') END;

UPDATE playlistshares SET
  label = 'Person ' || id::text,
  tokenhash = CASE WHEN tokenhash IS NULL THEN NULL
                   ELSE encode(sha256(convert_to(gen_random_uuid()::text, 'UTF8')), 'hex') END;

COMMIT;
