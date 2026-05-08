PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
INSERT INTO schema_version(version)
  SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);

CREATE TABLE IF NOT EXISTS snapshots (
  id              TEXT PRIMARY KEY,
  tab_id          TEXT NOT NULL,
  file_path       TEXT,
  folder          TEXT,
  name            TEXT,
  content_hash    TEXT NOT NULL,
  content_size    INTEGER NOT NULL,
  disk_path       TEXT,           -- legacy; null for new rows since content lives in `content`
  content         TEXT,           -- snapshot text, UTF-8
  reason          TEXT NOT NULL,
  ts              INTEGER NOT NULL,
  server          TEXT,
  database        TEXT
);
CREATE INDEX IF NOT EXISTS ix_snapshots_tab_ts  ON snapshots(tab_id, ts DESC);
CREATE INDEX IF NOT EXISTS ix_snapshots_ts      ON snapshots(ts DESC);
CREATE INDEX IF NOT EXISTS ix_snapshots_folder  ON snapshots(folder);
CREATE INDEX IF NOT EXISTS ix_snapshots_name_nc ON snapshots(name COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS snapshot_tags (
  snapshot_id     TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
  tag             TEXT NOT NULL,
  PRIMARY KEY (snapshot_id, tag)
);
CREATE INDEX IF NOT EXISTS ix_snapshot_tags_tag ON snapshot_tags(tag);

CREATE TABLE IF NOT EXISTS tabs_latest (
  tab_id              TEXT PRIMARY KEY,
  latest_snapshot_id  TEXT NOT NULL REFERENCES snapshots(id),
  folder              TEXT,
  name                TEXT,
  tags_csv            TEXT,
  ts                  INTEGER NOT NULL,
  is_open             INTEGER NOT NULL DEFAULT 0,
  is_dirty            INTEGER NOT NULL DEFAULT 0,
  desc                TEXT
);
CREATE INDEX IF NOT EXISTS ix_tabs_latest_ts    ON tabs_latest(ts DESC);
CREATE INDEX IF NOT EXISTS ix_tabs_latest_name  ON tabs_latest(name COLLATE NOCASE);
