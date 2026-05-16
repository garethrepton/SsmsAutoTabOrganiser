using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.Storage
{
    /// <summary>
    /// Retention rules:
    ///   - all snapshots within last 7 days kept;
    ///   - 7 to 30 days: one per hour per tab;
    ///   - older than 30 days: one per day per tab;
    ///   - reason in (saved, closed) NEVER pruned;
    ///   - latest snapshot referenced by tabs_latest NEVER pruned.
    /// Then quota: if total bytes > MaxStorageMB, delete oldest-first (still skipping
    /// saved/closed/referenced) until under quota.
    /// </summary>
    internal sealed class Pruner
    {
        private readonly SnapshotStore _store;
        private readonly Logger _log;
        private readonly long _quotaBytes;

        public Pruner(SnapshotStore store, Logger log, long quotaBytes)
        {
            _store = store;
            _log = log;
            _quotaBytes = quotaBytes;
        }

        public PruneResult Prune(DateTime nowUtc)
        {
            var allSnapshots = _store.ListAllSnapshotsForPruner();
            var referenced = _store.GetReferencedSnapshotIds();

            var toDelete = new List<SnapshotRecord>();

            var byTab = allSnapshots.GroupBy(s => s.TabId);
            var nowMs = ((DateTimeOffset)nowUtc).ToUnixTimeMilliseconds();
            var sevenDays = TimeSpan.FromDays(7).TotalMilliseconds;
            var thirtyDays = TimeSpan.FromDays(30).TotalMilliseconds;

            foreach (var group in byTab)
            {
                var orderedNewestFirst = group.OrderByDescending(s => s.Ts).ToList();

                var hourBucketsKept = new HashSet<long>();
                var dayBucketsKept = new HashSet<long>();

                foreach (var s in orderedNewestFirst)
                {
                    if (referenced.Contains(s.Id)) continue;
                    if (s.Reason == "saved" || s.Reason == "closed") continue;

                    var ageMs = nowMs - s.Ts;

                    if (ageMs < sevenDays) continue;

                    if (ageMs < thirtyDays)
                    {
                        var hourBucket = s.Ts / (1000L * 60 * 60);
                        if (hourBucketsKept.Add(hourBucket)) continue;
                        toDelete.Add(s);
                    }
                    else
                    {
                        var dayBucket = s.Ts / (1000L * 60 * 60 * 24);
                        if (dayBucketsKept.Add(dayBucket)) continue;
                        toDelete.Add(s);
                    }
                }
            }

            foreach (var s in toDelete) _store.DeleteSnapshot(s.Id);

            int quotaDeleted = 0;
            if (_quotaBytes > 0)
            {
                // Fast path: ask the DB for the total size in one query. Only if it actually
                // exceeds the quota do we list candidate snapshots — avoids materialising every
                // row into managed memory just to compute a SUM when the user is under quota.
                long total = _store.SumSnapshotContentSize();
                if (total > _quotaBytes)
                {
                    var remaining = _store.ListAllSnapshotsForPruner();
                    foreach (var s in remaining
                                 .OrderBy(s => s.Ts)
                                 .Where(s => s.Reason != "saved" && s.Reason != "closed"
                                          && !referenced.Contains(s.Id)))
                    {
                        if (total <= _quotaBytes) break;
                        _store.DeleteSnapshot(s.Id);
                        total -= s.ContentSize;
                        quotaDeleted++;
                    }
                }
            }

            return new PruneResult { RuleDeleted = toDelete.Count, QuotaDeleted = quotaDeleted };
        }
    }

    internal sealed class PruneResult
    {
        public int RuleDeleted;
        public int QuotaDeleted;
        public override string ToString() => $"prune: rules deleted {RuleDeleted}, quota deleted {QuotaDeleted}";
    }
}
