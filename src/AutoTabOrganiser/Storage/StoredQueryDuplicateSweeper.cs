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
        /// <paramref name="requiredRoot"/>, when non-null, is a hard deletion boundary: any
        /// candidate whose resolved path is not under it is excluded from the sweep entirely —
        /// an unattended deleter must never reach outside the folder it was pointed at.
        /// </summary>
        public Result Sweep(IEnumerable<Candidate> candidates, Func<string, bool> isTabOpen, string requiredRoot = null)
        {
            var result = new Result();
            if (candidates == null) return result;

            string rootFull = null;
            if (!string.IsNullOrEmpty(requiredRoot))
            {
                try { rootFull = Path.GetFullPath(requiredRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar; }
                catch { rootFull = null; }
            }

            // Dedupe by resolved full path. Two TabIds can legitimately map to the same
            // on-disk file (rows are keyed by TabId, not path) — without collapsing them
            // here, the same file shows up twice in its hash-group, "wins" once and
            // "loses" once, and File.Delete destroys the only copy. Open-state merges:
            // if any tab pointing at the path is open, the entry counts as open.
            var byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c?.FilePath)) continue;
                try
                {
                    if (!File.Exists(c.FilePath)) continue;
                    var full = Path.GetFullPath(c.FilePath);

                    if (rootFull != null && !full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    {
                        _log?.Debug($"sweep: skipped (outside stored-queries root): {full}");
                        continue;
                    }

                    var open = !string.IsNullOrEmpty(c.TabId)
                               && isTabOpen != null
                               && isTabOpen(c.TabId);

                    if (byPath.TryGetValue(full, out var existing))
                    {
                        existing.IsOpen = existing.IsOpen || open;
                        continue;
                    }

                    var content = File.ReadAllText(full);
                    var canonical = MetadataWriter.CanonicalContentForCompare(content);
                    byPath[full] = new Entry
                    {
                        TabId = c.TabId,
                        Path = full,
                        Hash = Hashing.Sha256Hex(canonical),
                        Mtime = SafeMtime(full),
                        IsOpen = open
                    };
                }
                catch (Exception ex)
                {
                    _log?.Warn($"sweep: read failed {c.FilePath}: {ex.Message}");
                }
            }
            var entries = byPath.Values.ToList();

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
