using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoTabOrganiser.Metadata
{
    internal static class MetadataParser
    {
        private static readonly Regex KeyLineRegex =
            new Regex(@"^\s*--\s*@(?<key>[A-Za-z][A-Za-z0-9_-]*)\s*:?\s*(?<value>.*)$", RegexOptions.Compiled);

        private static readonly Regex FlagLineRegex =
            new Regex(@"^\s*--\s*@(?<key>[A-Za-z][A-Za-z0-9_-]*)\s*$", RegexOptions.Compiled);

        public static ParsedMetadata Parse(string text)
        {
            var meta = new ParsedMetadata();
            if (string.IsNullOrEmpty(text)) return meta;

            var lines = SplitLinesPreservingOffsets(text, out var lineEndExclusiveOffsets);

            int lastCommentLineIndex = -1;
            string activeMultilineKey = null;
            var multilineBuilder = new StringBuilder();

            for (int li = 0; li < lines.Count; li++)
            {
                var line = lines[li];
                var trimmed = line.TrimStart();

                if (trimmed.Length == 0) break;
                if (!trimmed.StartsWith("--")) break;

                lastCommentLineIndex = li;

                // First check single-line @key (or @key: value), or @flag.
                var keyMatch = KeyLineRegex.Match(line);
                var flagMatch = FlagLineRegex.Match(line);

                if (keyMatch.Success)
                {
                    if (activeMultilineKey != null)
                    {
                        ApplyKey(meta, activeMultilineKey, multilineBuilder.ToString().TrimEnd());
                        activeMultilineKey = null;
                        multilineBuilder.Clear();
                    }

                    var key = keyMatch.Groups["key"].Value.ToLowerInvariant();
                    var value = keyMatch.Groups["value"].Value.Trim();

                    if (value == "|")
                    {
                        activeMultilineKey = key;
                        multilineBuilder.Clear();
                    }
                    else
                    {
                        ApplyKey(meta, key, value);
                    }
                }
                else if (flagMatch.Success && activeMultilineKey == null)
                {
                    var key = flagMatch.Groups["key"].Value.ToLowerInvariant();
                    ApplyKey(meta, key, string.Empty);
                }
                else if (activeMultilineKey != null)
                {
                    var continuation = StripCommentLeader(line);
                    multilineBuilder.AppendLine(continuation);
                }
            }

            if (activeMultilineKey != null)
            {
                ApplyKey(meta, activeMultilineKey, multilineBuilder.ToString().TrimEnd());
            }

            meta.CommentBlockEndExclusive = lastCommentLineIndex < 0 ? 0 : lineEndExclusiveOffsets[lastCommentLineIndex];

            ExtractTagsFromHeaderComments(text, meta.CommentBlockEndExclusive, meta.Tags);

            return meta;
        }

        private static void ApplyKey(ParsedMetadata meta, string key, string value)
        {
            switch (key)
            {
                case "folder":
                    meta.Folder = string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('/');
                    break;
                case "name":
                    meta.Name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                    break;
                case "desc":
                case "description":
                    meta.Description = string.IsNullOrEmpty(value) ? null : value;
                    break;
                case "id":
                    meta.Id = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                    break;
                case "server":
                    meta.Server = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                    break;
                case "database":
                case "db":
                    meta.Database = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                    break;
                case "nosnapshot":
                    meta.NoSnapshot = true;
                    break;
            }
        }

        private static string StripCommentLeader(string line)
        {
            var i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i + 1 < line.Length && line[i] == '-' && line[i + 1] == '-')
            {
                i += 2;
                if (i < line.Length && line[i] == ' ') i++;
                if (i < line.Length && line[i] == ' ') i++;
            }
            return i >= line.Length ? string.Empty : line.Substring(i);
        }

        private static List<string> SplitLinesPreservingOffsets(string text, out List<int> lineEndsExclusive)
        {
            var lines = new List<string>();
            lineEndsExclusive = new List<int>();
            int i = 0, len = text.Length;
            while (i < len)
            {
                int start = i;
                while (i < len && text[i] != '\n') i++;
                int endLineContentExclusive = i;
                if (i < len) i++; // consume '\n'
                var line = text.Substring(start, endLineContentExclusive - start);
                if (line.EndsWith("\r")) line = line.Substring(0, line.Length - 1);
                lines.Add(line);
                lineEndsExclusive.Add(i);
            }
            return lines;
        }

        // Extract every #word that appears inside any -- or /* */ comment within the leading
        // header block (chars [0, endExclusive)). Restricting to the header avoids picking up
        // things like `-- create #temp_results` later in the file as if they were tags.
        public static void ExtractTagsFromHeaderComments(string text, int endExclusive, List<string> outTags)
        {
            if (string.IsNullOrEmpty(text) || endExclusive <= 0) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int i = 0, len = Math.Min(text.Length, endExclusive);
            bool inLineComment = false;
            bool inBlockComment = false;

            while (i < len)
            {
                char c = text[i];

                if (!inLineComment && !inBlockComment)
                {
                    if (c == '-' && i + 1 < len && text[i + 1] == '-')
                    {
                        inLineComment = true;
                        i += 2;
                        continue;
                    }
                    if (c == '/' && i + 1 < len && text[i + 1] == '*')
                    {
                        inBlockComment = true;
                        i += 2;
                        continue;
                    }
                    if (c == '\'' || c == '"' || c == '[')
                    {
                        char close = c == '[' ? ']' : c;
                        i++;
                        while (i < len && text[i] != close)
                        {
                            if (text[i] == '\\' && i + 1 < len) i += 2; else i++;
                        }
                        if (i < len) i++;
                        continue;
                    }
                    i++;
                    continue;
                }

                if (inLineComment)
                {
                    if (c == '\n') { inLineComment = false; i++; continue; }
                }
                else if (inBlockComment)
                {
                    if (c == '*' && i + 1 < len && text[i + 1] == '/') { inBlockComment = false; i += 2; continue; }
                }

                if (c == '#')
                {
                    int start = i + 1;
                    int j = start;
                    while (j < len)
                    {
                        char cc = text[j];
                        if ((cc >= 'A' && cc <= 'Z') || (cc >= 'a' && cc <= 'z') || (cc >= '0' && cc <= '9') || cc == '_' || cc == '-') j++;
                        else break;
                    }
                    if (j - start >= 2)
                    {
                        var tag = text.Substring(start, j - start).ToLowerInvariant();
                        if (seen.Add(tag)) outTags.Add(tag);
                    }
                    i = Math.Max(j, i + 1);
                    continue;
                }

                i++;
            }
        }
    }
}
