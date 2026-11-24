-- Fails when the connected database has a column that columns.txt does not list.
-- Reads columns.txt from standard input and prints column names only, never values:
--   psql -X -q -v ON_ERROR_STOP=1 -d strim_stg -f check-coverage.sql < columns.txt
-- Uses pg_catalog rather than information_schema so that columns the role holds no privilege on are
-- still counted.

\set ON_ERROR_STOP on

CREATE TEMP TABLE coverage_lines (line text);
\copy coverage_lines (line) FROM pstdin

CREATE TEMP TABLE coverage_listed AS
SELECT split_part(t, ' ', 1) AS col, split_part(t, ' ', 2) AS mark, split_part(t, ' ', 3) AS extra
FROM (SELECT btrim(regexp_replace(line, '\s+', ' ', 'g')) AS t FROM coverage_lines) l
WHERE t <> '' AND left(t, 1) <> '#';

CREATE TEMP TABLE coverage_actual AS
SELECT CASE WHEN n.nspname = 'public' THEN '' ELSE n.nspname || '.' END || c.relname || '.' || a.attname AS col
FROM pg_attribute a
JOIN pg_class c ON c.oid = a.attrelid
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
  AND a.attnum > 0 AND NOT a.attisdropped
  AND n.nspname NOT IN ('pg_catalog', 'information_schema')
  AND n.nspname NOT LIKE 'pg\_%';

\echo 'Columns in the database but not in columns.txt:'
SELECT col AS missing FROM coverage_actual EXCEPT SELECT col FROM coverage_listed ORDER BY 1;
\echo 'Entries in columns.txt without a column (stale, not an error):'
SELECT col AS stale FROM coverage_listed EXCEPT SELECT col FROM coverage_actual ORDER BY 1;

DO $$
DECLARE
  bad_lines integer;
  duplicates integer;
  missing integer;
BEGIN
  SELECT count(*) INTO bad_lines FROM coverage_listed WHERE mark NOT IN ('keep', 'sanitised') OR extra <> '';
  SELECT count(*) INTO duplicates FROM (SELECT col FROM coverage_listed GROUP BY col HAVING count(*) > 1) d;
  SELECT count(*) INTO missing FROM (SELECT col FROM coverage_actual EXCEPT SELECT col FROM coverage_listed) m;
  IF bad_lines > 0 THEN
    RAISE EXCEPTION 'columns.txt: % line(s) not in the form "<table>.<column> keep|sanitised"', bad_lines;
  END IF;
  IF duplicates > 0 THEN
    RAISE EXCEPTION 'columns.txt: % column(s) listed more than once', duplicates;
  END IF;
  IF missing > 0 THEN
    RAISE EXCEPTION 'coverage check failed: % column(s) missing from columns.txt', missing;
  END IF;
  RAISE NOTICE 'coverage check passed';
END $$;
