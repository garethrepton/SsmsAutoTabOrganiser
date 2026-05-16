using System;
using System.Collections.Generic;
using System.Linq;
using AutoTabOrganiser.Settings;

namespace AutoTabOrganiser.Metadata
{
    internal static class AutoTagger
    {
        /// <summary>
        /// Returns the deduped list of tag names that any rule's <see cref="AutoTagRule.Match"/>
        /// substring is found in the document. Tag names are normalised (lowercased, leading '#'
        /// stripped). Order: matched rules in order, tags within a rule in declared order.
        /// </summary>
        public static List<string> MatchedTags(string text, IList<AutoTagRule> rules)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text) || rules == null || rules.Count == 0) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                if (string.IsNullOrEmpty(rule.Match)) continue;
                if (text.IndexOf(rule.Match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var raw in rule.Tags ?? new List<string>())
                {
                    var clean = (raw ?? "").Trim().TrimStart('#').ToLowerInvariant();
                    if (clean.Length == 0) continue;
                    if (seen.Add(clean)) result.Add(clean);
                }
            }
            return result;
        }

        /// <summary>
        /// Builds the new document text with auto-tag chips inserted at the end of the existing
        /// comment block. Returns null if there's nothing to insert (no header, or all tags
        /// already present). Caller must apply the edit on the UI thread.
        /// </summary>
        public static string BuildInjectedText(string text, IEnumerable<string> tags, ParsedMetadata meta)
        {
            var injection = ComputeInjection(text, tags, meta);
            if (injection == null) return null;
            return text.Substring(0, injection.InsertOffset)
                 + injection.InsertedText
                 + text.Substring(injection.InsertOffset);
        }

        /// <summary>
        /// Computes WHERE to insert and WHAT to insert without producing the full new text. This
        /// is the safer surface for editor-buffer callers — they get the exact substring to
        /// pass to <c>ITextEdit.Insert</c> instead of having to recompute the diff from
        /// <see cref="BuildInjectedText"/>'s output. Returns null when there is nothing to inject.
        /// </summary>
        public static TagInjection ComputeInjection(string text, IEnumerable<string> tags, ParsedMetadata meta)
        {
            if (tags == null) return null;
            if (meta == null || meta.CommentBlockEndExclusive == 0) return null;

            var alreadyHas = new HashSet<string>(meta.Tags ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var toAdd = tags
                .Select(t => (t ?? "").Trim().TrimStart('#').ToLowerInvariant())
                .Where(t => t.Length > 0 && !alreadyHas.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (toAdd.Count == 0) return null;

            var line = "-- " + string.Join(" ", toAdd.Select(t => "#" + t));
            var nl = MetadataWriter.DetectLineEnding(text);
            return new TagInjection
            {
                InsertOffset = meta.CommentBlockEndExclusive,
                InsertedText = line + nl,
            };
        }

        /// <summary>An insertion-only edit to apply at a specific offset in the buffer.</summary>
        internal sealed class TagInjection
        {
            public int InsertOffset { get; set; }
            public string InsertedText { get; set; }
        }
    }
}
