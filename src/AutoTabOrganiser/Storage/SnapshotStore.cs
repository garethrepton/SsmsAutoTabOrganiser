using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.Storage
{
    /// <summary>
    /// File-on-disk + SQLite index for snapshots. All public methods are safe to call from the
    /// threadpool — internally serialised with a single connection guarded by a lock.
    /// </summary>
    internal sealed class SnapshotStore : IDisposable
    {
        private readonly string _root;
        private readonly string _snapshotsDir;
        private readonly string _dbPath;
        private readonly Logger _log;
        private readonly object _gate = new object();
        private SqliteConnection _conn;
        // FTS5 is best-effort: if the native library was built without it, we skip all FTS paths.
        private bool _ftsAvailable;

        public string Root => _root;
        public string SnapshotsDir => _snapshotsDir;
        public string DbPath => _dbPath;
        public bool FtsAvailable => _ftsAvailable;

        private static int s_sqliteInit;

        public SnapshotStore(string storageRoot, Logger log)
        {
            _root = storageRoot;
            _snapshotsDir = Path.Combine(_root, "snapshots");
            _dbPath = Path.Combine(_root, "index.db");
            _log = log;
            Directory.CreateDirectory(_snapshotsDir);
            EnsureSqliteProvider();
            OpenAndMigrate();
            BackfillContentFts();
            BackfillDiskSnapshots();
        }

        private static void EnsureSqliteProvider()
        {
            // Microsoft.Data.Sqlite's static initialiser only sets a provider if no native is loaded.
            // In a VSIX host (where assembly load order is unusual) the bundle's module-init
            // sometimes doesn't run before SqliteConnection's static .cctor — so we force it here.
            if (System.Threading.Interlocked.Exchange(ref s_sqliteInit, 1) == 0)
            {
                SQLitePCL.Batteries_V2.Init();
            }
        }

        private void OpenAndMigrate()
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            _conn = new SqliteConnection(cs);
            _conn.Open();

            // Belt-and-braces: before any migration touches the DB, snapshot it to disk
            // whenever the running assembly version differs from what was last recorded.
            // Schema.sql today is purely additive, but a future destructive migration would
            // otherwise have no rollback path. Backup-on-version-change is the invariant.
            BackupBeforeMigrationIfVersionChanged();

            var schema = LoadEmbeddedSchema();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = schema;
                cmd.ExecuteNonQuery();
            }

            // Forward-compat migrations on pre-existing databases.
            EnsureColumn("snapshots", "content", "TEXT");
            // disk_path was originally NOT NULL — relax it for new rows.
            // SQLite can't drop NOT NULL in place; if we hit that we leave the legacy rows alone
            // and ensure new rows still write a non-null disk_path of empty string.

            // Connection denormalised onto tabs_latest so the side panel can render it
            // without a JOIN (which would re-introduce ambiguous-column errors).
            EnsureColumn("tabs_latest", "server",   "TEXT");
            EnsureColumn("tabs_latest", "database", "TEXT");
            BackfillTabsLatestConnection();

            // access_count powers frecency in the Quick Switcher — hot tabs surface above merely
            // recent ones. Defaults to 0 on legacy rows; WriteSnapshot increments it per snapshot.
            EnsureColumn("tabs_latest", "access_count", "INTEGER NOT NULL DEFAULT 0");

            EnsureIndexes();

            EnsureFtsTable();

            // Migration completed cleanly — record the current assembly version so the next
            // run can detect future upgrades.
            RecordCurrentAssemblyVersion();
        }

        private const int BackupKeepCount = 5;
        private const string AssemblyVersionKey = "assembly_version";

        private static string CurrentAssemblyVersion()
        {
            try { return typeof(SnapshotStore).Assembly.GetName().Version?.ToString() ?? "unknown"; }
            catch { return "unknown"; }
        }

        /// <summary>
        /// If the assembly version recorded in <c>app_meta</c> differs from the running
        /// assembly's version (or no version is recorded but user data already exists),
        /// produce a consistent copy of <c>index.db</c> in <c>{root}\backups\</c> using
        /// SQLite's <c>VACUUM INTO</c>. Then prune the backups directory to the most
        /// recent <see cref="BackupKeepCount"/> files. Best-effort: any failure is
        /// logged but does not abort startup — losing a snapshot of the backup is far
        /// less bad than refusing to load the extension.
        /// </summary>
        private void BackupBeforeMigrationIfVersionChanged()
        {
            try
            {
                // app_meta is the version registry. CREATE IF NOT EXISTS keeps this idempotent.
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "CREATE TABLE IF NOT EXISTS app_meta (key TEXT PRIMARY KEY, value TEXT);";
                    cmd.ExecuteNonQuery();
                }

                bool hasPriorData = TableHasRows("snapshots");
                string recorded = ReadAppMeta(AssemblyVersionKey);
                string current = CurrentAssemblyVersion();

                // Three cases:
                //   recorded == current        → no upgrade, no backup needed
                //   recorded == null && !data  → fresh install, no backup needed
                //   otherwise                  → upgrade (or pre-app_meta legacy data) → back up
                if (string.Equals(recorded, current, StringComparison.Ordinal)) return;
                if (recorded == null && !hasPriorData) return;

                var backupsDir = Path.Combine(_root, "backups");
                Directory.CreateDirectory(backupsDir);

                var oldLabel = string.IsNullOrEmpty(recorded) ? "pre-meta" : recorded;
                var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var safeOld = SanitiseForFile(oldLabel);
                var safeNew = SanitiseForFile(current);
                var backupPath = Path.Combine(backupsDir, $"index.{safeOld}-to-{safeNew}.{ts}.db");

                // VACUUM INTO produces a consistent snapshot via SQLite itself — safe even
                // with WAL journal mode and concurrent connections. The destination must be
                // quoted with single quotes inside the SQL string.
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "VACUUM INTO '" + backupPath.Replace("'", "''") + "';";
                    cmd.ExecuteNonQuery();
                }
                _log?.Info($"Pre-migration backup written: {backupPath} (from version '{oldLabel}' to '{current}').");

                PruneOldBackups(backupsDir);
            }
            catch (Exception ex)
            {
                _log?.Warn("Pre-migration DB backup failed (continuing without backup): " + ex.Message);
            }
        }

        private void RecordCurrentAssemblyVersion()
        {
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "INSERT INTO app_meta(key, value) VALUES($k, $v) " +
                        "ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
                    Add(cmd, "$k", AssemblyVersionKey);
                    Add(cmd, "$v", CurrentAssemblyVersion());
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { _log?.Warn("Record assembly version failed: " + ex.Message); }
        }

        private bool TableHasRows(string tableName)
        {
            // SQLite parameter binding can't substitute a table identifier, so the name has to
            // be interpolated. Restrict to a conservative identifier shape so an unsanitised
            // future caller can't smuggle in arbitrary SQL via this path.
            if (string.IsNullOrEmpty(tableName) ||
                !System.Text.RegularExpressions.Regex.IsMatch(tableName, "^[A-Za-z_][A-Za-z0-9_]*$"))
                return false;

            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1;";
                    Add(cmd, "$n", tableName);
                    if (cmd.ExecuteScalar() == null) return false;
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT 1 FROM {tableName} LIMIT 1;";
                    return cmd.ExecuteScalar() != null;
                }
            }
            catch { return false; }
        }

        private string ReadAppMeta(string key)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM app_meta WHERE key=$k;";
                Add(cmd, "$k", key);
                return cmd.ExecuteScalar() as string;
            }
        }

        private static string SanitiseForFile(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        private void PruneOldBackups(string backupsDir)
        {
            try
            {
                var files = Directory.GetFiles(backupsDir, "index.*.db");
                if (files.Length <= BackupKeepCount) return;
                Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                for (int i = BackupKeepCount; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); }
                    catch (Exception ex) { _log?.Debug($"Prune backup '{files[i]}' failed: {ex.Message}"); }
                }
            }
            catch (Exception ex) { _log?.Debug("Prune backups failed: " + ex.Message); }
        }

        /// <summary>
        /// Composite indexes for hot-path queries. All are <c>IF NOT EXISTS</c> so re-running
        /// is a cheap no-op. Each was introduced for a specific query in this codebase — see
        /// the inline comments for which query benefits.
        /// </summary>
        private void EnsureIndexes()
        {
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    // (tab_id, reason, ts DESC) — speeds the scalar subquery in ListTabs that
                    // computes last_saved_ts per tab. Without it SQLite seeks by tab_id then
                    // scans for reason='saved' across every row for the tab.
                    cmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_snapshots_tab_reason_ts " +
                                      "ON snapshots(tab_id, reason, ts DESC);";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _conn.CreateCommand())
                {
                    // (tag, snapshot_id) — speeds the EXISTS clause for #tag filters in the
                    // sidebar/Quick Switcher search. The existing single-column ix_snapshot_tags_tag
                    // helps locate tag rows but still requires a follow-up snapshot_id lookup.
                    cmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_snapshot_tags_tag_snapshot " +
                                      "ON snapshot_tags(tag, snapshot_id);";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _conn.CreateCommand())
                {
                    // (server, ts DESC) and (database, ts DESC) — speed server:/database: filters
                    // in the Quick Switcher when narrowing to a specific connection.
                    cmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_tabs_latest_server_ts " +
                                      "ON tabs_latest(server, ts DESC);";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_tabs_latest_database_ts " +
                                      "ON tabs_latest(database, ts DESC);";
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { _log?.Warn("EnsureIndexes failed: " + ex.Message); }
        }

        /// <summary>
        /// Creates the FTS5 virtual table in its own try/catch so that a library
        /// built without FTS5 support does not abort the rest of schema setup.
        /// </summary>
        private void EnsureFtsTable()
        {
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    // Keep tab_id UNINDEXED — we filter on it via WHERE not MATCH.
                    // unicode61 tokeniser folds to lowercase and splits on punctuation,
                    // which is appropriate for SQL query text.
                    cmd.CommandText =
                        "CREATE VIRTUAL TABLE IF NOT EXISTS tab_content_fts USING fts5(" +
                        "  tab_id UNINDEXED," +
                        "  content," +
                        "  tokenize = 'unicode61'" +
                        ");";
                    cmd.ExecuteNonQuery();
                }
                _ftsAvailable = true;
            }
            catch (Exception ex)
            {
                _ftsAvailable = false;
                _log?.Warn("FTS5 unavailable — content search disabled. " + ex.Message);
            }
        }

        /// <summary>
        /// One-shot copy of server/database from each tab's latest snapshot into the
        /// denormalised columns on <c>tabs_latest</c>. Idempotent — only fills rows where
        /// either column is currently NULL, so re-running on subsequent loads is a cheap no-op.
        /// </summary>
        private void BackfillTabsLatestConnection()
        {
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "UPDATE tabs_latest " +
                        "SET server   = COALESCE(server,   (SELECT server   FROM snapshots WHERE snapshots.id = tabs_latest.latest_snapshot_id)), " +
                        "    database = COALESCE(database, (SELECT database FROM snapshots WHERE snapshots.id = tabs_latest.latest_snapshot_id)) " +
                        "WHERE server IS NULL OR database IS NULL;";
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { _log?.Warn("Backfill connection columns failed: " + ex.Message); }
        }

        private void EnsureColumn(string table, string column, string typeDecl)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({table});";
                using (var rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        if (string.Equals(rd.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
                    }
                }
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeDecl};";
                cmd.ExecuteNonQuery();
            }
        }

        private static string LoadEmbeddedSchema()
        {
            // Schema.sql is shipped as Content next to the assembly (see csproj). Read at runtime.
            var asmDir = Path.GetDirectoryName(typeof(SnapshotStore).Assembly.Location);
            var path = Path.Combine(asmDir ?? "", "Storage", "Schema.sql");
            if (File.Exists(path)) return File.ReadAllText(path);
            // Fallback: minimal inline schema if the file isn't deployed.
            return EmbeddedSchemaFallback;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try { _conn?.Dispose(); } catch { }
                _conn = null;
            }
        }

        // ---------- writes ----------

        public string WriteSnapshot(SnapshotRecord r, string content)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (content == null) content = string.Empty;

            r.ContentSize = Encoding.UTF8.GetByteCount(content);

            // Dual storage: content lives in the DB (fast reads, FTS) AND in a per-snapshot
            // .sql file on disk (browsable backup, external tooling). Both the disk write and
            // the DB insert happen inside the _gate lock so concurrent writers can't race on
            // the deterministic file path or call into a Dispose'd _conn. Disk write still
            // happens before the DB insert so a crash mid-transaction leaves an orphan file
            // rather than a DB row pointing at nothing — BackfillDiskSnapshots heals orphans.
            lock (_gate)
            {
                r.DiskPath = WriteContentToDisk(r, content);

                using (var tx = _conn.BeginTransaction())
                {
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText =
                            "INSERT INTO snapshots(id, tab_id, file_path, folder, name, content_hash, content_size, disk_path, content, reason, ts, server, database) " +
                            "VALUES($id,$tab,$fp,$fld,$nm,$ch,$cs,$dp,$ct,$r,$ts,$sv,$db);";
                        Add(cmd, "$id", r.Id);
                        Add(cmd, "$tab", r.TabId);
                        Add(cmd, "$fp", r.FilePath);
                        Add(cmd, "$fld", r.Folder);
                        Add(cmd, "$nm", r.Name);
                        Add(cmd, "$ch", r.ContentHash);
                        Add(cmd, "$cs", r.ContentSize);
                        Add(cmd, "$dp", r.DiskPath);
                        Add(cmd, "$ct", content);
                        Add(cmd, "$r", r.Reason);
                        Add(cmd, "$ts", r.Ts);
                        Add(cmd, "$sv", r.Server);
                        Add(cmd, "$db", r.Database);
                        cmd.ExecuteNonQuery();
                    }

                    if (r.Tags != null && r.Tags.Count > 0)
                    {
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "INSERT OR IGNORE INTO snapshot_tags(snapshot_id, tag) VALUES($sid,$tag);";
                            var pSid = cmd.CreateParameter(); pSid.ParameterName = "$sid"; cmd.Parameters.Add(pSid);
                            var pTag = cmd.CreateParameter(); pTag.ParameterName = "$tag"; cmd.Parameters.Add(pTag);
                            foreach (var tag in r.Tags)
                            {
                                pSid.Value = r.Id;
                                pTag.Value = tag;
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    var tagsCsv = r.Tags == null || r.Tags.Count == 0 ? null : string.Join(",", r.Tags);
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        // access_count: starts at 1 on first snapshot for a tab; on update, bump
                        // the existing count by 1. This is the frecency signal the Quick Switcher
                        // sorts by — hot tabs surface above merely-recent ones.
                        cmd.CommandText =
                            "INSERT INTO tabs_latest(tab_id, latest_snapshot_id, folder, name, tags_csv, ts, is_open, is_dirty, desc, server, database, access_count) " +
                            "VALUES($tab,$sid,$fld,$nm,$tg,$ts,1,0,$dsc,$sv,$db,1) " +
                            "ON CONFLICT(tab_id) DO UPDATE SET " +
                            "  latest_snapshot_id=excluded.latest_snapshot_id, " +
                            "  folder=excluded.folder, " +
                            "  name=excluded.name, " +
                            "  tags_csv=excluded.tags_csv, " +
                            "  ts=excluded.ts, " +
                            "  is_open=1, " +
                            "  is_dirty=0, " +
                            "  desc=excluded.desc, " +
                            "  server=excluded.server, " +
                            "  database=excluded.database, " +
                            "  access_count=tabs_latest.access_count + 1;";
                        Add(cmd, "$tab", r.TabId);
                        Add(cmd, "$sid", r.Id);
                        Add(cmd, "$fld", r.Folder);
                        Add(cmd, "$nm", r.Name);
                        Add(cmd, "$tg", tagsCsv);
                        Add(cmd, "$ts", r.Ts);
                        Add(cmd, "$dsc", r.Desc);
                        Add(cmd, "$sv", r.Server);
                        Add(cmd, "$db", r.Database);
                        cmd.ExecuteNonQuery();
                    }

                    // Refresh the FTS row for this tab. We index only the latest content per tab
                    // (not every historical snapshot) to keep the index bounded.
                    //
                    // Two separate ExecuteNonQuery calls: Microsoft.Data.Sqlite only executes the
                    // first statement when multiple are concatenated into one CommandText, which
                    // would silently drop the INSERT and empty the FTS index over time.
                    if (_ftsAvailable)
                    {
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "DELETE FROM tab_content_fts WHERE tab_id = $tab;";
                            Add(cmd, "$tab", r.TabId);
                            cmd.ExecuteNonQuery();
                        }
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "INSERT INTO tab_content_fts(tab_id, content) VALUES($tab, $content);";
                            Add(cmd, "$tab", r.TabId);
                            Add(cmd, "$content", content ?? string.Empty);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                }
            }

            return $"db://{r.Id}";
        }

        public string GetLatestHashForTab(string tabId)
        {
            lock (_gate)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT content_hash FROM snapshots WHERE tab_id=$t ORDER BY ts DESC LIMIT 1;";
                    Add(cmd, "$t", tabId);
                    var v = cmd.ExecuteScalar();
                    return v as string;
                }
            }
        }

        public void SetTabState(string tabId, bool? isOpen, bool? isDirty)
        {
            lock (_gate)
            {
                var fragments = new List<string>();
                using (var cmd = _conn.CreateCommand())
                {
                    if (isOpen.HasValue) fragments.Add("is_open=$o");
                    if (isDirty.HasValue) fragments.Add("is_dirty=$d");
                    if (fragments.Count == 0) return;
                    cmd.CommandText = "UPDATE tabs_latest SET " + string.Join(", ", fragments) + " WHERE tab_id=$t;";
                    if (isOpen.HasValue) Add(cmd, "$o", isOpen.Value ? 1 : 0);
                    if (isDirty.HasValue) Add(cmd, "$d", isDirty.Value ? 1 : 0);
                    Add(cmd, "$t", tabId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void ClearAllOpenFlags()
        {
            lock (_gate)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE tabs_latest SET is_open=0;";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ---------- reads (UI) ----------

        public List<TabSummary> ListTabs(string sqlWhere, IEnumerable<KeyValuePair<string, object>> parameters, string orderBy)
        {
            lock (_gate)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    // server/database are denormalised onto tabs_latest (kept fresh by
                    // WriteSnapshot's INSERT/ON CONFLICT) so this is a single-table query;
                    // a JOIN here would re-introduce ambiguous-column errors against the
                    // unqualified column names the search-query parser emits.
                    // last_saved_ts: scalar subquery for the most recent saved-reason snapshot
                    // per tab. Subquery (rather than JOIN) keeps column names unambiguous so
                    // the search-query parser's unqualified WHERE clauses don't break.
                    cmd.CommandText =
                        "SELECT tab_id, latest_snapshot_id, folder, name, tags_csv, ts, is_open, is_dirty, desc, server, database, " +
                        "       (SELECT MAX(s.ts) FROM snapshots s WHERE s.tab_id = tabs_latest.tab_id AND s.reason = 'saved') AS last_saved_ts, " +
                        "       access_count, " +
                        // Denormalised file-path lookups — saved-reason first (the user's
                        // explicit Save), then the latest snapshot's path as a fallback. These
                        // hop off the (tab_id, reason, ts DESC) and (tab_id, ts DESC) indexes
                        // respectively, so they're cheap per row even at 200+ tabs.
                        "       (SELECT s.file_path FROM snapshots s WHERE s.tab_id = tabs_latest.tab_id AND s.reason = 'saved' AND s.file_path IS NOT NULL ORDER BY s.ts DESC LIMIT 1) AS saved_file_path, " +
                        "       (SELECT s.file_path FROM snapshots s WHERE s.tab_id = tabs_latest.tab_id ORDER BY s.ts DESC LIMIT 1) AS latest_file_path " +
                        "FROM tabs_latest " +
                        (string.IsNullOrEmpty(sqlWhere) ? "" : "WHERE " + sqlWhere + " ") +
                        "ORDER BY " + (string.IsNullOrEmpty(orderBy) ? "ts DESC" : orderBy) + ";";
                    if (parameters != null)
                        foreach (var p in parameters) Add(cmd, p.Key, p.Value);

                    var list = new List<TabSummary>();
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            list.Add(new TabSummary
                            {
                                TabId = rd.GetString(0),
                                LatestSnapshotId = rd.GetString(1),
                                Folder = rd.IsDBNull(2) ? null : rd.GetString(2),
                                Name = rd.IsDBNull(3) ? null : rd.GetString(3),
                                TagsCsv = rd.IsDBNull(4) ? null : rd.GetString(4),
                                Ts = rd.GetInt64(5),
                                IsOpen = rd.GetInt32(6) != 0,
                                IsDirty = rd.GetInt32(7) != 0,
                                Desc = rd.IsDBNull(8) ? null : rd.GetString(8),
                                Server = rd.IsDBNull(9) ? null : rd.GetString(9),
                                Database = rd.IsDBNull(10) ? null : rd.GetString(10),
                                LastSavedTs = rd.IsDBNull(11) ? (long?)null : rd.GetInt64(11),
                                AccessCount = rd.IsDBNull(12) ? 0 : rd.GetInt64(12),
                                SavedFilePath = rd.IsDBNull(13) ? null : rd.GetString(13),
                                LatestFilePath = rd.IsDBNull(14) ? null : rd.GetString(14),
                            });
                        }
                    }
                    return list;
                }
            }
        }

        public List<SnapshotRecord> ListSnapshots(string whereSql, IEnumerable<KeyValuePair<string, object>> parameters, int limit = 500)
        {
            lock (_gate)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT id, tab_id, file_path, folder, name, content_hash, content_size, disk_path, reason, ts, server, database " +
                        "FROM snapshots " +
                        (string.IsNullOrEmpty(whereSql) ? "" : "WHERE " + whereSql + " ") +
                        "ORDER BY ts DESC LIMIT " + limit + ";";
                    if (parameters != null)
                        foreach (var p in parameters) Add(cmd, p.Key, p.Value);
                    var list = new List<SnapshotRecord>();
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            list.Add(new SnapshotRecord
                            {
                                Id = rd.GetString(0),
                                TabId = rd.GetString(1),
                                FilePath = rd.IsDBNull(2) ? null : rd.GetString(2),
                                Folder = rd.IsDBNull(3) ? null : rd.GetString(3),
                                Name = rd.IsDBNull(4) ? null : rd.GetString(4),
                                ContentHash = rd.GetString(5),
                                ContentSize = rd.GetInt64(6),
                                DiskPath = rd.GetString(7),
                                Reason = rd.GetString(8),
                                Ts = rd.GetInt64(9),
                                Server = rd.IsDBNull(10) ? null : rd.GetString(10),
                                Database = rd.IsDBNull(11) ? null : rd.GetString(11),
                            });
                        }
                    }
                    return list;
                }
            }
        }

        public string ReadSnapshotContent(string diskPath)
        {
            // Legacy callers still pass disk paths; new path is ReadSnapshotContentById.
            if (string.IsNullOrEmpty(diskPath)) return string.Empty;
            var full = Path.IsPathRooted(diskPath) ? diskPath : Path.Combine(_snapshotsDir, diskPath);
            return File.Exists(full) ? File.ReadAllText(full) : string.Empty;
        }

        public string ReadSnapshotContentById(string snapshotId)
        {
            lock (_gate)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT content, disk_path FROM snapshots WHERE id=$id;";
                    Add(cmd, "$id", snapshotId);
                    using (var rd = cmd.ExecuteReader())
                    {
                        if (!rd.Read()) return string.Empty;
                        if (!rd.IsDBNull(0)) return rd.GetString(0);
                        var disk = rd.IsDBNull(1) ? null : rd.GetString(1);
                        if (string.IsNullOrEmpty(disk)) return string.Empty;
                        return ReadSnapshotContent(disk);
                    }
                }
            }
        }

        public List<string> GetTagsForSnapshot(string snapshotId)
        {
            lock (_gate)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT tag FROM snapshot_tags WHERE snapshot_id=$s;";
                    Add(cmd, "$s", snapshotId);
                    var list = new List<string>();
                    using (var rd = cmd.ExecuteReader()) while (rd.Read()) list.Add(rd.GetString(0));
                    return list;
                }
            }
        }

        /// <summary>
        /// Populate <c>tab_content_fts</c> for any tab in <c>tabs_latest</c> whose content isn't yet indexed.
        /// Safe to call repeatedly — only inserts missing rows. Returns the number of tabs indexed.
        /// </summary>
        public int BackfillContentFts()
        {
            if (!_ftsAvailable) return 0;

            lock (_gate)
            {
                int count = 0;
                try
                {
                    // Find tabs present in tabs_latest that have no FTS entry yet.
                    var pending = new List<KeyValuePair<string, string>>();
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT tl.tab_id, tl.latest_snapshot_id " +
                            "FROM tabs_latest tl " +
                            "LEFT JOIN tab_content_fts f ON f.tab_id = tl.tab_id " +
                            "WHERE f.tab_id IS NULL;";
                        using (var rd = cmd.ExecuteReader())
                        {
                            while (rd.Read())
                                pending.Add(new KeyValuePair<string, string>(rd.GetString(0), rd.GetString(1)));
                        }
                    }

                    if (pending.Count == 0) return 0;

                    _log?.Info($"FTS backfill: indexing {pending.Count} tab(s).");

                    using (var tx = _conn.BeginTransaction())
                    {
                        foreach (var pair in pending)
                        {
                            string content = null;
                            using (var cmd = _conn.CreateCommand())
                            {
                                cmd.CommandText = "SELECT content FROM snapshots WHERE id=$id;";
                                Add(cmd, "$id", pair.Value);
                                var raw = cmd.ExecuteScalar();
                                content = raw as string;
                            }

                            using (var cmd = _conn.CreateCommand())
                            {
                                cmd.Transaction = tx;
                                cmd.CommandText =
                                    "INSERT INTO tab_content_fts(tab_id, content) VALUES($tab, $content);";
                                Add(cmd, "$tab", pair.Key);
                                Add(cmd, "$content", content ?? string.Empty);
                                cmd.ExecuteNonQuery();
                            }
                            count++;
                        }
                        tx.Commit();
                    }

                    _log?.Info($"FTS backfill complete: {count} tab(s) indexed.");
                }
                catch (Exception ex)
                {
                    // FTS is best-effort — log and continue rather than breaking the app.
                    _log?.Warn("FTS backfill failed: " + ex.Message);
                }

                return count;
            }
        }

        /// <summary>
        /// Writes the snapshot body to <c>snapshots/{yyyy-MM}/{shortId}_{name}.sql</c> under the
        /// storage root and returns the path relative to <c>_snapshotsDir</c>. Deterministic from
        /// (id, ts, name) so backfill produces the same path as the original write.
        /// </summary>
        private string WriteContentToDisk(SnapshotRecord r, string content)
        {
            var rel = BuildDiskRelPath(r);
            var full = Path.Combine(_snapshotsDir, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, content ?? string.Empty, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // Disk write is best-effort — DB still gets the content. Returning empty disk_path
                // keeps the row consistent with "no on-disk copy" so backfill will retry later.
                _log?.Warn($"Snapshot file write failed for id={r.Id}: {ex.Message}");
                return "";
            }
            return rel;
        }

        private static string BuildDiskRelPath(SnapshotRecord r)
        {
            var bucket = DateTimeOffset.FromUnixTimeMilliseconds(r.Ts).UtcDateTime.ToString("yyyy-MM");
            var shortId = (r.Id ?? "").Replace("-", "");
            if (shortId.Length > 8) shortId = shortId.Substring(0, 8);
            if (shortId.Length == 0) shortId = "noid";
            var safeName = AutoTabOrganiser.Util.PathSanitiser.Sanitise(r.Name) ?? "untitled";
            return Path.Combine(bucket, $"{shortId}_{safeName}.sql");
        }

        /// <summary>
        /// Writes a .sql file for any snapshot row whose <c>disk_path</c> is empty/null but whose
        /// <c>content</c> is present. Idempotent — safe to run on every startup. Deterministic
        /// naming means re-running after a partial failure picks up where it left off.
        /// </summary>
        public int BackfillDiskSnapshots()
        {
            lock (_gate)
            {
                int written = 0;
                try
                {
                    var pending = new List<SnapshotRecord>();
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT id, tab_id, name, ts, content " +
                            "FROM snapshots " +
                            "WHERE (disk_path IS NULL OR disk_path = '') AND content IS NOT NULL;";
                        using (var rd = cmd.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                pending.Add(new SnapshotRecord
                                {
                                    Id = rd.GetString(0),
                                    TabId = rd.GetString(1),
                                    Name = rd.IsDBNull(2) ? null : rd.GetString(2),
                                    Ts = rd.GetInt64(3),
                                });
                            }
                        }
                    }

                    if (pending.Count == 0) return 0;
                    _log?.Info($"Snapshot disk backfill: writing {pending.Count} file(s).");

                    foreach (var r in pending)
                    {
                        string content;
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.CommandText = "SELECT content FROM snapshots WHERE id=$id;";
                            Add(cmd, "$id", r.Id);
                            content = cmd.ExecuteScalar() as string;
                        }

                        var rel = WriteContentToDisk(r, content ?? string.Empty);
                        if (string.IsNullOrEmpty(rel)) continue;

                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.CommandText = "UPDATE snapshots SET disk_path=$dp WHERE id=$id;";
                            Add(cmd, "$dp", rel);
                            Add(cmd, "$id", r.Id);
                            cmd.ExecuteNonQuery();
                        }
                        written++;
                    }

                    _log?.Info($"Snapshot disk backfill complete: {written} file(s) written.");
                }
                catch (Exception ex)
                {
                    _log?.Warn("Snapshot disk backfill failed: " + ex.Message);
                }
                return written;
            }
        }

        // ---------- helpers ----------

        private static void Add(SqliteCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private static string MakeRelative(string root, string full)
        {
            var r = Path.GetFullPath(root);
            var f = Path.GetFullPath(full);
            return f.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                ? f.Substring(r.Length).TrimStart('\\', '/')
                : f;
        }

        // Used only if Schema.sql isn't deployed alongside the DLL.
        private const string EmbeddedSchemaFallback = @"
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;
CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
INSERT INTO schema_version(version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);
CREATE TABLE IF NOT EXISTS snapshots (id TEXT PRIMARY KEY, tab_id TEXT NOT NULL, file_path TEXT, folder TEXT, name TEXT, content_hash TEXT NOT NULL, content_size INTEGER NOT NULL, disk_path TEXT NOT NULL, reason TEXT NOT NULL, ts INTEGER NOT NULL, server TEXT, database TEXT);
CREATE INDEX IF NOT EXISTS ix_snapshots_tab_ts ON snapshots(tab_id, ts DESC);
CREATE INDEX IF NOT EXISTS ix_snapshots_ts ON snapshots(ts DESC);
CREATE INDEX IF NOT EXISTS ix_snapshots_folder ON snapshots(folder);
CREATE INDEX IF NOT EXISTS ix_snapshots_name_nc ON snapshots(name COLLATE NOCASE);
CREATE TABLE IF NOT EXISTS snapshot_tags (snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE, tag TEXT NOT NULL, PRIMARY KEY (snapshot_id, tag));
CREATE INDEX IF NOT EXISTS ix_snapshot_tags_tag ON snapshot_tags(tag);
CREATE TABLE IF NOT EXISTS tabs_latest (tab_id TEXT PRIMARY KEY, latest_snapshot_id TEXT NOT NULL REFERENCES snapshots(id), folder TEXT, name TEXT, tags_csv TEXT, ts INTEGER NOT NULL, is_open INTEGER NOT NULL DEFAULT 0, is_dirty INTEGER NOT NULL DEFAULT 0, desc TEXT);
CREATE INDEX IF NOT EXISTS ix_tabs_latest_ts ON tabs_latest(ts DESC);
CREATE INDEX IF NOT EXISTS ix_tabs_latest_name ON tabs_latest(name COLLATE NOCASE);
";

        /// <summary>
        /// Remove every trace of a tab: all snapshot rows + their on-disk files, the
        /// <c>tabs_latest</c> row, and the FTS entry. Used by the sidebar's "Delete from
        /// history" command. tabs_latest is deleted first so the FK from
        /// <c>latest_snapshot_id</c> to <c>snapshots(id)</c> doesn't block snapshot deletion.
        /// </summary>
        public void DeleteTab(string tabId)
        {
            if (string.IsNullOrEmpty(tabId)) return;
            lock (_gate)
            {
                var diskPaths = new List<string>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT disk_path FROM snapshots WHERE tab_id=$t;";
                    Add(cmd, "$t", tabId);
                    using (var rd = cmd.ExecuteReader())
                        while (rd.Read()) if (!rd.IsDBNull(0)) diskPaths.Add(rd.GetString(0));
                }

                foreach (var disk in diskPaths)
                {
                    if (string.IsNullOrEmpty(disk)) continue;
                    try
                    {
                        var full = Path.IsPathRooted(disk) ? disk : Path.Combine(_snapshotsDir, disk);
                        if (File.Exists(full)) File.Delete(full);
                    }
                    catch { /* best-effort — DB rows still go below */ }
                }

                using (var tx = _conn.BeginTransaction())
                {
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM tabs_latest WHERE tab_id=$t;";
                        Add(cmd, "$t", tabId);
                        cmd.ExecuteNonQuery();
                    }
                    if (_ftsAvailable)
                    {
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "DELETE FROM tab_content_fts WHERE tab_id=$t;";
                            Add(cmd, "$t", tabId);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        // snapshot_tags has ON DELETE CASCADE on snapshot_id.
                        cmd.CommandText = "DELETE FROM snapshots WHERE tab_id=$t;";
                        Add(cmd, "$t", tabId);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                _log?.Info($"DeleteTab: removed tab_id={tabId} (snapshots={diskPaths.Count}).");
            }
        }

        public void DeleteSnapshot(string snapshotId)
        {
            lock (_gate)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT disk_path FROM snapshots WHERE id=$id;";
                    Add(cmd, "$id", snapshotId);
                    var disk = cmd.ExecuteScalar() as string;
                    if (!string.IsNullOrEmpty(disk))
                    {
                        var full = Path.IsPathRooted(disk) ? disk : Path.Combine(_snapshotsDir, disk);
                        try { if (File.Exists(full)) File.Delete(full); } catch { }
                    }
                }
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM snapshots WHERE id=$id;";
                    Add(cmd, "$id", snapshotId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public List<SnapshotRecord> ListAllSnapshotsForPruner()
        {
            return ListSnapshots(null, null, int.MaxValue);
        }

        /// <summary>
        /// Total bytes-of-content across all snapshots. Used by <see cref="Pruner"/> to decide
        /// whether the quota pass needs to do any work — much cheaper than fetching every row.
        /// </summary>
        public long SumSnapshotContentSize()
        {
            lock (_gate)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COALESCE(SUM(content_size), 0) FROM snapshots;";
                    var v = cmd.ExecuteScalar();
                    if (v == null || v is DBNull) return 0;
                    return Convert.ToInt64(v);
                }
            }
        }

        /// <summary>
        /// Cross-tab snapshot deduplication. Groups snapshots by <c>content_hash</c>; when a
        /// hash appears under two or more distinct <c>tab_id</c>s, picks one snapshot to keep
        /// (winner) and deletes the rest of the group's losers.
        ///
        /// Winner-selection: snapshot whose tab is currently <c>is_open=1</c> in
        /// <c>tabs_latest</c>, else newest <c>ts</c>. Tie-break by <c>tab_id</c> for
        /// determinism.
        ///
        /// Guards (mirrors <see cref="Pruner"/>): never deletes a snapshot whose
        /// <c>reason</c> is <c>saved</c> or <c>closed</c>, and never deletes a snapshot
        /// referenced by <c>tabs_latest.latest_snapshot_id</c>. So a tab's "current view"
        /// is always preserved — only historical snapshots that happen to match another
        /// tab's content can be pruned.
        /// </summary>
        public int SweepCrossTabContentDuplicates()
        {
            lock (_gate)
            {
                var open = new HashSet<string>(StringComparer.Ordinal);
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT tab_id FROM tabs_latest WHERE is_open=1;";
                    using (var rd = cmd.ExecuteReader())
                        while (rd.Read()) open.Add(rd.GetString(0));
                }

                var referenced = new HashSet<string>(StringComparer.Ordinal);
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT latest_snapshot_id FROM tabs_latest;";
                    using (var rd = cmd.ExecuteReader())
                        while (rd.Read()) if (!rd.IsDBNull(0)) referenced.Add(rd.GetString(0));
                }

                // Push the candidate-finding work into SQL: only return snapshots whose
                // content_hash appears in *at least two distinct tabs*. With 5k+ rows, this
                // avoids streaming the entire snapshots table into managed memory just to
                // find that 95% of hashes are singletons. The inner GROUP BY is served by the
                // ix_snapshots_tab_ts index (covering on content_hash is unnecessary because
                // SQLite's planner will still pick this when content_hash has high cardinality).
                var rows = new List<DedupRow>();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT id, tab_id, content_hash, ts, reason, disk_path " +
                        "FROM snapshots " +
                        "WHERE content_hash IN (" +
                        "  SELECT content_hash FROM snapshots " +
                        "  GROUP BY content_hash HAVING COUNT(DISTINCT tab_id) >= 2" +
                        ");";
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            rows.Add(new DedupRow
                            {
                                Id       = rd.GetString(0),
                                TabId    = rd.GetString(1),
                                Hash     = rd.GetString(2),
                                Ts       = rd.GetInt64(3),
                                Reason   = rd.IsDBNull(4) ? "" : rd.GetString(4),
                                DiskPath = rd.IsDBNull(5) ? null : rd.GetString(5),
                            });
                        }
                    }
                }

                var toDelete = new List<DedupRow>();
                foreach (var group in rows.GroupBy(r => r.Hash, StringComparer.Ordinal))
                {
                    var members = group.ToList();
                    // Per-tab dedup is handled by SnapshotPipeline already. Only act when
                    // the same hash spans multiple tabs.
                    var distinctTabs = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var m in members) distinctTabs.Add(m.TabId);
                    if (distinctTabs.Count < 2) continue;

                    DedupRow winner = null;
                    foreach (var m in members)
                    {
                        if (winner == null) { winner = m; continue; }
                        bool mOpen = open.Contains(m.TabId);
                        bool wOpen = open.Contains(winner.TabId);
                        if (mOpen != wOpen)         { if (mOpen) winner = m; continue; }
                        if (m.Ts != winner.Ts)      { if (m.Ts > winner.Ts) winner = m; continue; }
                        if (string.CompareOrdinal(m.TabId, winner.TabId) < 0) winner = m;
                    }

                    foreach (var m in members)
                    {
                        if (m == winner) continue;
                        if (m.Reason == "saved" || m.Reason == "closed") continue;
                        if (referenced.Contains(m.Id)) continue;
                        toDelete.Add(m);
                    }
                }

                if (toDelete.Count == 0) return 0;

                // Delete on-disk snapshot files first so a crash mid-loop leaves orphan rows
                // (which BackfillDiskSnapshots can heal) rather than orphan files (which it can't).
                foreach (var m in toDelete)
                {
                    if (string.IsNullOrEmpty(m.DiskPath)) continue;
                    try
                    {
                        var full = Path.IsPathRooted(m.DiskPath) ? m.DiskPath : Path.Combine(_snapshotsDir, m.DiskPath);
                        if (File.Exists(full)) File.Delete(full);
                    }
                    catch { /* best-effort — DB row will still be removed below */ }
                }

                using (var tx = _conn.BeginTransaction())
                {
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM snapshots WHERE id=$id;";
                        var p = cmd.CreateParameter(); p.ParameterName = "$id"; cmd.Parameters.Add(p);
                        foreach (var m in toDelete)
                        {
                            p.Value = m.Id;
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }

                _log?.Info($"snapshot cross-tab dedup: deleted {toDelete.Count}.");
                return toDelete.Count;
            }
        }

        private sealed class DedupRow
        {
            public string Id;
            public string TabId;
            public string Hash;
            public long   Ts;
            public string Reason;
            public string DiskPath;
        }

        public List<string> GetAllTags()
        {
            lock (_gate)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT DISTINCT tag FROM snapshot_tags ORDER BY tag COLLATE NOCASE;";
                    var tags = new List<string>();
                    using (var rd = cmd.ExecuteReader()) while (rd.Read()) tags.Add(rd.GetString(0));
                    return tags;
                }
            }
        }

        public HashSet<string> GetReferencedSnapshotIds()
        {
            lock (_gate)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT latest_snapshot_id FROM tabs_latest;";
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read()) if (!rd.IsDBNull(0)) set.Add(rd.GetString(0));
                    }
                    return set;
                }
            }
        }
    }
}
