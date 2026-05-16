using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AutoTabOrganiser.Settings
{
    internal sealed class SettingsStore
    {
        private readonly string _file;
        private readonly object _gate = new object();
        private AppSettings _cached;
        // Disk mtime of the on-disk settings.json at the moment _cached was populated. If the
        // file's mtime changes (the user edited settings.json externally, e.g. via the "Open
        // Settings File" command), the cache is invalidated on the next Load/Mutate so we don't
        // overwrite their edits.
        private DateTime _cachedMtimeUtc;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        public SettingsStore(string settingsFilePath)
        {
            _file = settingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(_file));
        }

        public string FilePath => _file;

        public AppSettings Load()
        {
            lock (_gate)
            {
                // Cache miss OR external edit since we last read → reload from disk.
                if (_cached != null && !DiskHasChanged()) return _cached;

                if (!File.Exists(_file))
                {
                    _cached = new AppSettings();
                    SeedExamples(_cached);
                    Save(_cached);
                    return _cached;
                }
                try
                {
                    var json = File.ReadAllText(_file);
                    _cached = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
                    _cachedMtimeUtc = SafeGetWriteTimeUtc(_file);
                }
                catch
                {
                    _cached = new AppSettings();
                    _cachedMtimeUtc = SafeGetWriteTimeUtc(_file);
                }
                if (BackfillDefaults(_cached)) Save(_cached);
                return _cached;
            }
        }

        /// <summary>
        /// Returns true if the on-disk settings file's mtime differs from what was recorded when
        /// the cache was last populated — meaning someone (the user, an external tool) edited
        /// the file behind our back. Conservative on errors: returns true so we re-read rather
        /// than potentially overwrite.
        /// </summary>
        private bool DiskHasChanged()
        {
            try
            {
                if (!File.Exists(_file)) return _cached != null;
                return SafeGetWriteTimeUtc(_file) != _cachedMtimeUtc;
            }
            catch { return true; }
        }

        private static DateTime SafeGetWriteTimeUtc(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// Applies defaults that should exist on every install, including those that predate the
        /// setting being introduced. Returns true when something was changed and a re-save is needed.
        /// </summary>
        private static bool BackfillDefaults(AppSettings s)
        {
            var changed = false;
            if (s.SavedScripts == null) { s.SavedScripts = new SavedScriptsSettings(); changed = true; }
            if (string.IsNullOrWhiteSpace(s.SavedScripts.FolderPath))
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                s.SavedScripts.FolderPath = Path.Combine(docs, "AutoTabOrganiser", "Scripts");
                changed = true;
            }
            if (s.Ui == null) { s.Ui = new UiSettings(); changed = true; }
            if (s.Ui.RecentItemsCount <= 0) { s.Ui.RecentItemsCount = 12; changed = true; }
            return changed;
        }

        /// <summary>
        /// One-time seeding of example AutoTag rules and a default saved-scripts folder, applied only
        /// when a fresh settings.json is being created. Once the user changes or clears these, they
        /// won't reappear.
        /// </summary>
        private static void SeedExamples(AppSettings s)
        {
            if (s.Snapshotting.AutoTagRules.Count == 0)
            {
                s.Snapshotting.AutoTagRules.AddRange(new[]
                {
                    new AutoTagRule { Match = "PROD-",            Tags = new List<string>{ "incident", "prod" } },
                    new AutoTagRule { Match = "sys.dm_exec_",     Tags = new List<string>{ "dmv" } },
                    new AutoTagRule { Match = "DELETE FROM",      Tags = new List<string>{ "mutation", "delete" } },
                    new AutoTagRule { Match = "UPDATE ",          Tags = new List<string>{ "mutation", "update" } },
                    new AutoTagRule { Match = "TRUNCATE ",        Tags = new List<string>{ "mutation", "truncate" } },
                    new AutoTagRule { Match = "DROP TABLE",       Tags = new List<string>{ "schema-change" } },
                    new AutoTagRule { Match = "ALTER TABLE",      Tags = new List<string>{ "schema-change" } },
                    new AutoTagRule { Match = "CREATE INDEX",     Tags = new List<string>{ "index" } },
                    new AutoTagRule { Match = "WITH (NOLOCK)",    Tags = new List<string>{ "nolock" } },
                    new AutoTagRule { Match = "BACKUP DATABASE",  Tags = new List<string>{ "backup" } },
                });
            }

            if (s.SavedScripts == null) s.SavedScripts = new SavedScriptsSettings();
            if (string.IsNullOrWhiteSpace(s.SavedScripts.FolderPath))
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                s.SavedScripts.FolderPath = Path.Combine(docs, "AutoTabOrganiser", "Scripts");
            }
        }

        public void Save(AppSettings s)
        {
            lock (_gate)
            {
                _cached = s;
                var json = JsonSerializer.Serialize(s, JsonOpts);
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_file))
                {
                    var bak = _file + ".bak";
                    try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                    File.Replace(tmp, _file, bak, ignoreMetadataErrors: true);
                    try { File.Delete(bak); } catch { }
                }
                else
                {
                    File.Move(tmp, _file);
                }
                // Refresh mtime so a subsequent Load doesn't think we edited externally.
                _cachedMtimeUtc = SafeGetWriteTimeUtc(_file);
            }
        }

        public void Mutate(Action<AppSettings> mutator)
        {
            lock (_gate)
            {
                // Force a fresh read from disk before mutating: if the user edited settings.json
                // externally since our cache was populated, that edit must win over the in-memory
                // copy. Without this, the next Mutate would Save() the cached version on top of
                // their changes and silently lose them.
                _cached = null;
                var s = Load();
                mutator(s);
                Save(s);
            }
        }

        public string ResolveStorageLocation()
        {
            var s = Load();
            if (!string.IsNullOrWhiteSpace(s.Storage.Location)) return s.Storage.Location;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "AutoTabOrganiser");
        }

        public static string DefaultSettingsFilePath()
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(roaming, "AutoTabOrganiser", "settings.json");
        }
    }
}
