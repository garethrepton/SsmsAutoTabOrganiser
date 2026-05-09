using System.Collections.Generic;

namespace AutoTabOrganiser.Settings
{
    internal sealed class StorageSettings
    {
        public string Location { get; set; }                 // null = platform default
        public int    MaxStorageMB { get; set; } = 2048;
        public bool   RetentionEnabled { get; set; } = true;
    }

    internal sealed class SnapshottingSettings
    {
        // 1s feels live without trashing the index on every keystroke. Floor inside
        // SnapshotPipeline allows users to tune lower for stored-queries workflows.
        public int  EditDebounceSeconds    { get; set; } = 1;
        public int  FlushIntervalSeconds   { get; set; } = 60;
        public bool AutoInjectId           { get; set; } = true;
        public bool AutoTagInjectIntoHeader { get; set; } = true;
        public List<AutoTagRule> AutoTagRules { get; set; } = new List<AutoTagRule>();
    }

    internal sealed class AutoTagRule
    {
        /// <summary>Case-insensitive substring searched anywhere in the document.</summary>
        public string Match { get; set; }
        /// <summary>Tags to apply (without leading '#') when <see cref="Match"/> is found.</summary>
        public List<string> Tags { get; set; } = new List<string>();
    }

    internal sealed class PrivacySettings
    {
        public List<string> ServerAllowList { get; set; } = new List<string>();
        public List<string> ServerDenyList  { get; set; } = new List<string>();
        public bool   ConsentGiven     { get; set; } = false;
        public string ConsentTimestamp { get; set; }
    }

    internal sealed class SavedScriptsSettings
    {
        /// <summary>Absolute folder path on disk where "Save to scripts folder" writes files.</summary>
        public string FolderPath { get; set; }
        /// <summary>The @folder string injected into saved scripts so they appear under this folder
        /// in the Tab Organiser. Defaults to "Saved Scripts" when null/blank.</summary>
        public string TreeFolder { get; set; } = "Saved Scripts";
    }

    internal sealed class GitSettings
    {
        /// <summary>Reject commits whose trimmed message is shorter than this many characters.</summary>
        public int MinCommitMessageLength { get; set; } = 5;
        /// <summary>Toggle for the minimum-length check. Default on so accidental "x" commits don't slip through.</summary>
        public bool EnforceMinCommitMessage { get; set; } = true;
    }

    internal sealed class UiSettings
    {
        public string       LastSelectedTabId   { get; set; }
        public List<string> TreeExpandedFolders { get; set; } = new List<string>();
        public int          DetailPaneHeightPx  { get; set; } = 240;
        public List<string> SearchHistory       { get; set; } = new List<string>();
        public string       TabsViewMode        { get; set; } = "tree";   // "tree" | "list"
        public string       TabsSortMode        { get; set; } = "recent"; // "recent" | "name-asc" | "name-desc" | "folder-name"
        public List<string> PinnedTags          { get; set; } = new List<string>();
        /// <summary>
        /// Optional explicit tag-to-colour overrides, keyed by tag name (no leading '#'), values
        /// are #RRGGBB hex strings. Tags not listed here get a stable hash-derived colour.
        /// </summary>
        public Dictionary<string, string> TagColours { get; set; } = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        /// <summary>Show the per-tag stripe margin on the left edge of SQL editor windows.</summary>
        public bool TagStripeEnabled { get; set; } = true;
        /// <summary>How many tabs to show in the RECENT section. 0 or negative is treated as default (12).</summary>
        public int RecentItemsCount { get; set; } = 12;
    }

    internal sealed class AppSettings
    {
        public int                  SchemaVersion { get; set; } = 1;
        public StorageSettings      Storage       { get; set; } = new StorageSettings();
        public SnapshottingSettings Snapshotting  { get; set; } = new SnapshottingSettings();
        public PrivacySettings      Privacy       { get; set; } = new PrivacySettings();
        public SavedScriptsSettings SavedScripts  { get; set; } = new SavedScriptsSettings();
        public GitSettings          Git           { get; set; } = new GitSettings();
        public UiSettings           Ui            { get; set; } = new UiSettings();
    }
}
