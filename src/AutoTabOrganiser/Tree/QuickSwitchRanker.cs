using System;
using System.Collections.Generic;
using System.Linq;
using AutoTabOrganiser.Storage;

namespace AutoTabOrganiser.Tree
{
    /// <summary>
    /// Relevance ranking for the Quick Switcher. The SQL layer only *filters* candidates;
    /// how well each row matched is scored here, over the small (&lt;= a few hundred row)
    /// filtered set. Higher is better. Tiers: exact name &gt; name prefix &gt; word-boundary
    /// in name &gt; substring in name &gt; folder &gt; tag &gt; desc &gt; fuzzy-subsequence in
    /// name &gt; content-only (the row was admitted by the FTS clause but the term appears
    /// nowhere in its metadata).
    /// </summary>
    internal static class QuickSwitchRanker
    {
        /// <summary>
        /// Order rows by match score, then open-first, then MRU, then edit-frequency.
        /// With no bare terms every score is 0 and this degrades to the MRU ordering.
        /// </summary>
        public static List<TabSummary> Rank(IEnumerable<TabSummary> rows, IReadOnlyList<string> bareTerms)
        {
            return rows
                .OrderByDescending(t => Score(t, bareTerms))
                .ThenByDescending(t => t.IsOpen)
                .ThenByDescending(t => t.EffectiveActivatedTs)
                .ThenByDescending(t => t.AccessCount)
                .ToList();
        }

        public static int Score(TabSummary t, IReadOnlyList<string> bareTerms)
        {
            if (bareTerms == null || bareTerms.Count == 0) return 0;
            int total = 0;
            foreach (var term in bareTerms)
                total += ScoreTerm(t, term);
            return total;
        }

        private static int ScoreTerm(TabSummary t, string term)
        {
            if (string.IsNullOrEmpty(term)) return 0;
            var name = t.Name ?? "";

            if (name.Equals(term, StringComparison.OrdinalIgnoreCase)) return 100;
            if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 90;
            int idx = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return IsWordStart(name, idx) ? 80 : 70;

            if (Contains(t.Folder, term)) return 55;
            if (Contains(t.TagsCsv, term)) return 50;
            if (Contains(t.Desc, term)) return 45;
            if (FuzzyMatches(name, term)) return 40;

            // Term appears in none of the metadata — the SQL filter admitted this row via
            // the FTS content clause, the weakest kind of hit.
            return 25;
        }

        private static bool Contains(string haystack, string needle)
            => !string.IsNullOrEmpty(haystack)
               && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Char at <paramref name="idx"/> begins a word: start of string, after a
        /// non-alphanumeric, or an interior CamelCase hump ("getCustomerOrders" @ 'C').</summary>
        private static bool IsWordStart(string s, int idx)
        {
            if (idx == 0) return true;
            char prev = s[idx - 1];
            if (!char.IsLetterOrDigit(prev)) return true;
            return char.IsUpper(s[idx]) && char.IsLower(prev);
        }

        /// <summary>
        /// Case-insensitive subsequence match: every char of <paramref name="term"/> appears
        /// in <paramref name="text"/> in order ("cusord" → "CustomerOrders").
        /// </summary>
        public static bool FuzzyMatches(string text, string term)
            => FuzzyPositions(text, term) != null;

        /// <summary>
        /// Greedy left-to-right positions of a subsequence match, or null when the term is
        /// not a subsequence of the text. Greedy is good enough for display highlighting;
        /// this is not an editor-grade fuzzy scorer.
        /// </summary>
        public static List<int> FuzzyPositions(string text, string term)
        {
            if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(text)) return null;
            var positions = new List<int>(term.Length);
            int ti = 0;
            for (int i = 0; i < text.Length && ti < term.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(term[ti])) continue;
                positions.Add(i);
                ti++;
            }
            return ti == term.Length ? positions : null;
        }

        /// <summary>
        /// Character runs of <paramref name="text"/> that matched any of the terms — used to
        /// bold matches in result rows. Substring hits win per term; fuzzy positions are the
        /// fallback. Overlapping runs are merged; result is sorted by start.
        /// </summary>
        public static List<KeyValuePair<int, int>> MatchRuns(string text, IEnumerable<string> terms)
        {
            var runs = new List<KeyValuePair<int, int>>(); // key=start, value=length
            if (string.IsNullOrEmpty(text) || terms == null) return runs;

            foreach (var term in terms)
            {
                if (string.IsNullOrEmpty(term)) continue;
                int idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    runs.Add(new KeyValuePair<int, int>(idx, term.Length));
                    continue;
                }
                var fuzzy = FuzzyPositions(text, term);
                if (fuzzy == null) continue;
                foreach (var p in fuzzy)
                    runs.Add(new KeyValuePair<int, int>(p, 1));
            }
            if (runs.Count == 0) return runs;

            // Merge overlapping/adjacent runs so the inline builder gets clean segments.
            runs.Sort((a, b) => a.Key.CompareTo(b.Key));
            var merged = new List<KeyValuePair<int, int>> { runs[0] };
            for (int i = 1; i < runs.Count; i++)
            {
                var last = merged[merged.Count - 1];
                var cur = runs[i];
                if (cur.Key <= last.Key + last.Value)
                {
                    int end = Math.Max(last.Key + last.Value, cur.Key + cur.Value);
                    merged[merged.Count - 1] = new KeyValuePair<int, int>(last.Key, end - last.Key);
                }
                else merged.Add(cur);
            }
            return merged;
        }
    }
}
