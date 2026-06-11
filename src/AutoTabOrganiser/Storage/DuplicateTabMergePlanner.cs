using System;
using System.Collections.Generic;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Util;

namespace AutoTabOrganiser.Storage
{
    /// <summary>
    /// Decides which <c>tabs_latest</c> rows are duplicates of each other and how to
    /// collapse them. Two rows are duplicates when the canonical form of their latest
    /// content (<see cref="MetadataWriter.CanonicalContentForCompare"/> — @id lines
    /// stripped, trailing whitespace trimmed) is byte-equal. The raw <c>content_hash</c>
    /// dedup in <c>SweepCrossTabContentDuplicates</c> can never see this: each copy
    /// carries its own <c>-- @id:</c> line, so the hashes always differ, and the search /
    /// quick switcher keeps listing every copy as a separate row.
    ///
    /// Winner-selection mirrors <see cref="StoredQueryDuplicateSweeper"/>: open row first,
    /// else newest <c>ts</c>, ties broken by tab_id so the choice is deterministic across
    /// runs. Open rows are never merged away — a live tab's pipeline would write its row
    /// straight back.
    ///
    /// Pure logic class — no SQLite. <c>SnapshotStore.MergeDuplicateTabRows</c> executes
    /// the plan.
    /// </summary>
    internal static class DuplicateTabMergePlanner
    {
        public sealed class Row
        {
            public string TabId;
            public bool IsOpen;
            public long Ts;
            public string Content;
        }

        public sealed class Merge
        {
            public string WinnerTabId;
            public string LoserTabId;
        }

        public static List<Merge> Plan(IEnumerable<Row> rows)
        {
            var merges = new List<Merge>();
            if (rows == null) return merges;

            var groups = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (r == null || string.IsNullOrEmpty(r.TabId)) continue;
                var canonical = MetadataWriter.CanonicalContentForCompare(r.Content);
                // Blank tabs are not duplicates of each other — there's nothing being
                // duplicated, and merging them would fuse unrelated scratch histories.
                if (string.IsNullOrWhiteSpace(canonical)) continue;
                var key = Hashing.Sha256Hex(canonical);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Row>();
                list.Add(r);
            }

            foreach (var group in groups.Values)
            {
                if (group.Count < 2) continue;

                var winner = group[0];
                foreach (var m in group)
                {
                    if (ReferenceEquals(m, winner)) continue;
                    if (m.IsOpen != winner.IsOpen) { if (m.IsOpen) winner = m; continue; }
                    if (m.Ts != winner.Ts)         { if (m.Ts > winner.Ts) winner = m; continue; }
                    if (string.CompareOrdinal(m.TabId, winner.TabId) < 0) winner = m;
                }

                foreach (var m in group)
                {
                    if (ReferenceEquals(m, winner)) continue;
                    if (m.IsOpen) continue; // never merge away a live tab
                    if (string.Equals(m.TabId, winner.TabId, StringComparison.Ordinal)) continue;
                    merges.Add(new Merge { WinnerTabId = winner.TabId, LoserTabId = m.TabId });
                }
            }
            return merges;
        }
    }
}
