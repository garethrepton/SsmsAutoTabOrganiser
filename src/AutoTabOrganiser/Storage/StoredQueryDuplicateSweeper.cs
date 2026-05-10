using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.Storage
{
    /// <summary>
    /// Finds groups of <c>.sql</c> files in the Stored Queries folder whose canonical content
    /// (<see cref="MetadataWriter.CanonicalContentForCompare"/>) is byte-equal, and deletes
    /// every member of each group except the winner.
    ///
    /// Winner-selection: file whose <see cref="Candidate.TabId"/> is currently open in SSMS,
    /// else newest <c>LastWriteTimeUtc</c>. Ties broken by path comparison so the choice is
    /// deterministic across runs.
    ///
    /// Pure logic class — no SQLite, no UI thread requirements. Caller decides when to invoke.
    /// </summary>
    internal sealed class StoredQueryDuplicateSweeper
    {
        private readonly Logger _log;

        public StoredQueryDuplicateSweeper(Logger log)
        {
            _log = log;
        }

        public sealed class Candidate
        {
            public string TabId { get; set; }
            public string FilePath { get; set; }
        }

        public sealed class Result
        {
            public int GroupsConsidered;
            public int DuplicatesDeleted;
            public List<string> DeletedPaths { get; } = new List<string>();
            public override string ToString()
                => $"sweep: groups={GroupsConsidered}, deleted={DuplicatesDeleted}";
        }

        /// <summary>
        /// Runs the sweep. <paramref name="isTabOpen"/> is consulted with each candidate's TabId
        /// to determine winner preference; pass <c>_ =&gt; false</c> if open-state isn't available.
        /// </summary>
        public Result Sweep(IEnumerable<Candidate> candidates, Func<string, bool> isTabOpen)
        {
            var result = new Result();
            if (candidates == null) return result;

            var entries = new List<Entry>();
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c?.FilePath)) continue;
                Entry entry;
                try
                {
                    if (!File.Exists(c.FilePath)) continue;
                    var content = File.ReadAllText(c.FilePath);
                    var canonical = MetadataWriter.CanonicalContentForCompare(content);
                    entry = new Entry
                    {
                        TabId = c.TabId,
                        Path = c.FilePath,
                        Hash = Hashing.Sha256Hex(canonical),
                        Mtime = SafeMtime(c.FilePath),
                        IsOpen = !string.IsNullOrEmpty(c.TabId)
                                 && isTabOpen != null
                                 && isTabOpen(c.TabId)
                    };
                }
                catch (Exception ex)
                {
                    _log?.Warn($"sweep: read failed {c.FilePath}: {ex.Message}");
                    continue;
                }
                entries.Add(entry);
            }

            // Group by canonical-content hash. Singletons are skipped — only ≥2 means duplicates.
            foreach (var group in entries.GroupBy(e => e.Hash, StringComparer.Ordinal))
            {
                var members = group.ToList();
                if (members.Count < 2) continue;
                result.GroupsConsidered++;

                var winner = members
                    .OrderByDescending(e => e.IsOpen)
                    .ThenByDescending(e => e.Mtime)
                    .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                    .First();

                foreach (var loser in members)
                {
                    if (ReferenceEquals(loser, winner)) continue;
                    try
                    {
                        File.Delete(loser.Path);
                        result.DuplicatesDeleted++;
                        result.DeletedPaths.Add(loser.Path);
                        _log?.Info($"sweep: deleted duplicate '{loser.Path}' (kept '{winner.Path}', open={winner.IsOpen})");
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn($"sweep: delete failed {loser.Path}: {ex.Message}");
                    }
                }
            }

            return result;
        }

        private static long SafeMtime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path).Ticks; }
            catch { return 0L; }
        }

        private sealed class Entry
        {
            public string TabId;
            public string Path;
            public string Hash;
            public long Mtime;
            public bool IsOpen;
        }
    }
}
