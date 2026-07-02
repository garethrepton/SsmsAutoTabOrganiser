using System.Collections.Generic;

namespace AutoTabOrganiser.Storage
{
    internal sealed class SnapshotRecord
    {
        public string Id { get; set; }
        public string TabId { get; set; }
        public string FilePath { get; set; }
        public string Folder { get; set; }
        public string Name { get; set; }
        public string ContentHash { get; set; }
        public long   ContentSize { get; set; }
        public string DiskPath { get; set; }
        public string Reason { get; set; }
        public long   Ts { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public string Desc { get; set; }
    }

    internal sealed class TabSummary
    {
        public string TabId { get; set; }
        public string LatestSnapshotId { get; set; }
        public string Folder { get; set; }
        public string Name { get; set; }
        public string TagsCsv { get; set; }
        public long Ts { get; set; }                  // latest snapshot of any reason — "last edited"
        public long? LastSavedTs { get; set; }        // latest snapshot with reason='saved' — null if never explicitly saved
        public bool IsOpen { get; set; }
        public bool IsDirty { get; set; }
        public string Desc { get; set; }
        public string Server { get; set; }       // last-known SSMS server name
        public string Database { get; set; }     // last-known SSMS database name
        public long AccessCount { get; set; }    // total snapshots ever written for this tab (frecency signal)
        public long LastActivatedTs { get; set; } // last time the user focused the tab; 0 on legacy rows

        /// <summary>MRU timestamp: focus time when known, else last snapshot (legacy rows).</summary>
        public long EffectiveActivatedTs => LastActivatedTs > Ts ? LastActivatedTs : Ts;
        public string SavedFilePath { get; set; }  // file_path of the most recent saved-reason snapshot (the .sql on disk)
        public string LatestFilePath { get; set; } // file_path of the most recent snapshot of any reason (fallback)
    }
}
