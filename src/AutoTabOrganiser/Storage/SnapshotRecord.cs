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
        public long Ts { get; set; }
        public bool IsOpen { get; set; }
        public bool IsDirty { get; set; }
        public string Desc { get; set; }
        public string Server { get; set; }       // last-known SSMS server name
        public string Database { get; set; }     // last-known SSMS database name
    }
}
