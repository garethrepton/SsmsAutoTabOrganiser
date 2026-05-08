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
                if (_cached != null) return _cached;
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
                }
                catch
                {
                    _cached = new AppSettings();
                }
                if (BackfillDefaults(_cached)) Save(_cached);
                return _cached;
            }
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
            }
        }

        public void Mutate(Action<AppSettings> mutator)
        {
            lock (_gate)
            {
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
