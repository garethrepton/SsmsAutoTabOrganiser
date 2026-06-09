using System;
using System.Collections.Generic;
using System.Text;

namespace AutoTabOrganiser.Util
{
    /// <summary>
    /// Minimal unified-format line diff used by the snapshot-history timeline. Pure managed
    /// code on purpose: snapshot contents live in the SQLite store (not necessarily in any
    /// git repo), so shelling out to git is neither possible nor wanted here.
    ///
    /// Algorithm: trim the common prefix/suffix, then a classic LCS dynamic programme over
    /// the remaining lines. Inputs are SQL query scripts (typically well under a thousand
    /// lines); a size guard falls back to a whole-replace diff when the DP table would be
    /// unreasonably large, so pathological inputs degrade gracefully instead of stalling.
    /// </summary>
    internal static class TextDiff
    {
        /// <summary>DP-cell budget above which we fall back to a whole-replace diff.</summary>
        private const long MaxDpCells = 4_000_000;

        /// <summary>
        /// Unified diff of <paramref name="oldText"/> → <paramref name="newText"/> with
        /// <paramref name="context"/> lines of context around each hunk. Returns an empty
        /// string when the inputs are identical.
        /// </summary>
        public static string Unified(string oldText, string newText, string oldLabel, string newLabel, int context = 3)
        {
            var a = SplitLines(oldText ?? string.Empty);
            var b = SplitLines(newText ?? string.Empty);

            // Common prefix / suffix trim keeps the DP small for the typical "edited the
            // middle of the script" case.
            int prefix = 0;
            int maxPrefix = Math.Min(a.Length, b.Length);
            while (prefix < maxPrefix && string.Equals(a[prefix], b[prefix], StringComparison.Ordinal)) prefix++;

            int suffix = 0;
            int maxSuffix = Math.Min(a.Length, b.Length) - prefix;
            while (suffix < maxSuffix
                   && string.Equals(a[a.Length - 1 - suffix], b[b.Length - 1 - suffix], StringComparison.Ordinal))
                suffix++;

            int na = a.Length - prefix - suffix;
            int nb = b.Length - prefix - suffix;
            if (na == 0 && nb == 0) return string.Empty;

            // ops covers only the trimmed middle; entries are ' ' (equal), '-' (delete), '+' (insert).
            List<(char op, string line)> ops;
            if ((long)(na + 1) * (nb + 1) > MaxDpCells)
            {
                ops = new List<(char, string)>(na + nb);
                for (int i = 0; i < na; i++) ops.Add(('-', a[prefix + i]));
                for (int j = 0; j < nb; j++) ops.Add(('+', b[prefix + j]));
            }
            else
            {
                ops = LcsOps(a, prefix, na, b, prefix, nb);
            }

            return RenderUnified(a, b, prefix, suffix, ops, oldLabel, newLabel, Math.Max(0, context));
        }

        private static string[] SplitLines(string text)
            => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static List<(char op, string line)> LcsOps(string[] a, int aStart, int na, string[] b, int bStart, int nb)
        {
            // lcs[i, j] = LCS length of a[aStart+i..] vs b[bStart+j..]
            var lcs = new int[na + 1, nb + 1];
            for (int i = na - 1; i >= 0; i--)
            {
                for (int j = nb - 1; j >= 0; j--)
                {
                    lcs[i, j] = string.Equals(a[aStart + i], b[bStart + j], StringComparison.Ordinal)
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            var ops = new List<(char, string)>(na + nb);
            int x = 0, y = 0;
            while (x < na && y < nb)
            {
                if (string.Equals(a[aStart + x], b[bStart + y], StringComparison.Ordinal))
                {
                    ops.Add((' ', a[aStart + x])); x++; y++;
                }
                else if (lcs[x + 1, y] >= lcs[x, y + 1])
                {
                    ops.Add(('-', a[aStart + x])); x++;
                }
                else
                {
                    ops.Add(('+', b[bStart + y])); y++;
                }
            }
            while (x < na) { ops.Add(('-', a[aStart + x])); x++; }
            while (y < nb) { ops.Add(('+', b[bStart + y])); y++; }
            return ops;
        }

        private static string RenderUnified(string[] a, string[] b, int prefix, int suffix,
                                            List<(char op, string line)> ops,
                                            string oldLabel, string newLabel, int context)
        {
            // Re-inflate the trimmed prefix/suffix as equal ops so hunk grouping and line
            // numbering see the whole document.
            var full = new List<(char op, string line)>(prefix + ops.Count + suffix);
            for (int i = 0; i < prefix; i++) full.Add((' ', a[i]));
            full.AddRange(ops);
            for (int i = suffix; i > 0; i--) full.Add((' ', a[a.Length - i]));

            var sb = new StringBuilder();
            sb.Append("--- ").AppendLine(oldLabel ?? "old");
            sb.Append("+++ ").AppendLine(newLabel ?? "new");

            int aLine = 1, bLine = 1;   // 1-based line counters tracking position in full
            int idx = 0;
            while (idx < full.Count)
            {
                // Skip ahead to the next change.
                while (idx < full.Count && full[idx].op == ' ')
                {
                    idx++; aLine++; bLine++;
                }
                if (idx >= full.Count) break;

                // Hunk start: back up by `context` equal lines.
                int hunkStart = idx;
                int back = Math.Min(context, hunkStart);
                // Only back over equal lines (anything before idx is equal by construction
                // of the outer loop, so this is safe).
                hunkStart -= back;
                int hunkAStart = aLine - back, hunkBStart = bLine - back;

                // Extend the hunk forward: include changes and up to `context` trailing
                // equal lines, merging with a following change if it's within 2*context.
                int i2 = idx, equalRun = 0, hunkEnd = idx;
                int a2 = aLine, b2 = bLine;
                while (i2 < full.Count)
                {
                    if (full[i2].op == ' ')
                    {
                        equalRun++;
                        if (equalRun > context * 2) break;
                    }
                    else
                    {
                        equalRun = 0;
                        hunkEnd = i2;
                    }
                    if (full[i2].op != '+') a2++;
                    if (full[i2].op != '-') b2++;
                    i2++;
                }
                int tail = Math.Min(context, (i2 - 1) - hunkEnd);
                int hunkEndExclusive = hunkEnd + 1 + tail;

                int aCount = 0, bCount = 0;
                for (int k = hunkStart; k < hunkEndExclusive; k++)
                {
                    if (full[k].op != '+') aCount++;
                    if (full[k].op != '-') bCount++;
                }

                sb.Append("@@ -").Append(hunkAStart).Append(',').Append(aCount)
                  .Append(" +").Append(hunkBStart).Append(',').Append(bCount).AppendLine(" @@");
                for (int k = hunkStart; k < hunkEndExclusive; k++)
                {
                    sb.Append(full[k].op).AppendLine(full[k].line);
                }

                // Advance the outer cursors past this hunk.
                for (int k = idx; k < hunkEndExclusive; k++)
                {
                    if (full[k].op != '+') aLine++;
                    if (full[k].op != '-') bLine++;
                }
                idx = hunkEndExclusive;
            }

            return sb.ToString();
        }
    }
}
