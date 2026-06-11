using System;
using System.Collections.Generic;
using System.Text;

namespace AutoTabOrganiser.Metadata
{
    internal static class MetadataWriter
    {
        public static string GenerateTabId() => Guid.NewGuid().ToString("D");

        /// <summary>
        /// Returns the new full text with <c>-- @id: &lt;value&gt;</c> placed in the leading
        /// comment block — second line when a block already exists (the first line stays the
        /// human-readable header), top of the file otherwise. An @id already in the leading
        /// block is replaced in place; legacy trailing @id lines (and the blank-line padding
        /// the old writer left above them) are removed.
        /// </summary>
        /// <remarks>
        /// Returns <paramref name="text"/> unchanged when <paramref name="id"/> is empty.
        /// </remarks>
        public static string InjectId(string text, string id) => SetId(text, id);

        /// <summary>
        /// Inserts or replaces the <c>-- @id: &lt;value&gt;</c> line. See
        /// <see cref="InjectId"/> for the placement contract.
        /// </summary>
        public static string SetId(string text, string id)
        {
            text = text ?? string.Empty;
            id = (id ?? string.Empty).Trim();
            if (id.Length == 0) return text;

            var lines = SplitLinesKeepingTerminators(text);

            // Extent of the leading comment block: contiguous `--` lines from the top.
            int blockLines = 0;
            while (blockLines < lines.Count)
            {
                var t = StripTerminator(lines[blockLines]).TrimStart();
                if (t.Length == 0 || !t.StartsWith("--")) break;
                blockLines++;
            }

            var idIndices = new List<int>();
            for (int i = 0; i < lines.Count; i++)
                if (IsIdLineText(StripTerminator(lines[i]))) idIndices.Add(i);

            // The first @id inside the leading block is the replace target; every other @id
            // line (legacy trailing placement, duplicates) is removed.
            int replaceIndex = -1;
            foreach (var i in idIndices)
            {
                if (i < blockLines) { replaceIndex = i; break; }
            }

            bool removedBeyondBlock = false;
            for (int k = idIndices.Count - 1; k >= 0; k--)
            {
                int i = idIndices[k];
                if (i == replaceIndex) continue;
                lines.RemoveAt(i);
                if (i >= blockLines) removedBeyondBlock = true;
            }

            // A removed trailing @id leaves the old writer's blank-line padding dangling at
            // the tail — drop it. Files that never had a trailing @id keep their tail as-is.
            if (removedBeyondBlock)
            {
                while (lines.Count > 0 && StripTerminator(lines[lines.Count - 1]).Trim().Length == 0)
                    lines.RemoveAt(lines.Count - 1);
            }

            if (replaceIndex >= 0)
            {
                var terminator = lines[replaceIndex].Substring(StripTerminator(lines[replaceIndex]).Length);
                lines[replaceIndex] = "-- @id: " + id + terminator;
                return string.Concat(lines);
            }

            var cleaned = string.Concat(lines);
            var injection = ComputeIdInjection(cleaned, id, newTabHeader: null);
            return cleaned.Substring(0, injection.InsertOffset)
                 + injection.InsertedText
                 + cleaned.Substring(injection.InsertOffset);
        }

        public sealed class IdInjection
        {
            public int InsertOffset;
            public string InsertedText;
        }

        /// <summary>
        /// Computes the exact (offset, text) insertion that places <c>-- @id: &lt;value&gt;</c>
        /// into a document that doesn't have one yet — the shape ITextEdit.Insert needs, so the
        /// buffer injection in DocumentTracker stays a pure insert. When the document starts
        /// with a comment block the @id goes in as the second line, below the human-readable
        /// header. When it doesn't, the @id (optionally preceded by a generated
        /// <paramref name="newTabHeader"/> comment line, so untitled tabs get a searchable
        /// name) is prepended with a blank-line separator. Returns null when
        /// <paramref name="id"/> is empty. Callers are responsible for checking the document
        /// doesn't already carry an @id.
        /// </summary>
        public static IdInjection ComputeIdInjection(string text, string id, string newTabHeader)
        {
            text = text ?? string.Empty;
            id = (id ?? string.Empty).Trim();
            if (id.Length == 0) return null;

            var nl = DetectLineEnding(text);
            var idLine = "-- @id: " + id;

            int firstLineEnd = 0;
            while (firstLineEnd < text.Length && text[firstLineEnd] != '\n') firstLineEnd++;
            var firstLine = text.Substring(0, firstLineEnd).TrimEnd('\r').TrimStart();

            if (firstLine.StartsWith("--"))
            {
                if (firstLineEnd >= text.Length)
                {
                    // Lone comment line without a terminator: append below it.
                    return new IdInjection { InsertOffset = text.Length, InsertedText = nl + idLine + nl };
                }
                return new IdInjection { InsertOffset = firstLineEnd + 1, InsertedText = idLine + nl };
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(newTabHeader))
                sb.Append("-- ").Append(newTabHeader.Trim()).Append(nl);
            sb.Append(idLine).Append(nl).Append(nl); // blank line separates the header from the query
            return new IdInjection { InsertOffset = 0, InsertedText = sb.ToString() };
        }

        /// <summary>
        /// Inserts or replaces the leading <c>-- @folder: &lt;folder&gt;</c> line in the document.
        /// Behaviour:
        /// <list type="bullet">
        /// <item>If a leading <c>-- @folder:</c> line exists in the leading comment block, its value is replaced.</item>
        /// <item>If a leading comment block exists but no @folder line, a new line is inserted at the top of it.</item>
        /// <item>If there is no leading comment block, a fresh single-line block is prepended (followed by a blank line).</item>
        /// </list>
        /// </summary>
        public static string SetFolder(string text, string folder)
        {
            if (folder == null) folder = string.Empty;
            return SetLeadingKey(text, "folder", folder.Trim().Trim('/'));
        }

        /// <summary>
        /// Inserts or replaces the leading <c>-- @file: &lt;name&gt;</c> line in the document, recording the
        /// filename a tab was saved under so the metadata round-trips with the .sql file.
        /// </summary>
        public static string SetFile(string text, string fileName)
        {
            if (fileName == null) fileName = string.Empty;
            return SetLeadingKey(text, "file", fileName.Trim());
        }

        /// <summary>Inserts or replaces the leading <c>-- @name: &lt;value&gt;</c> line.</summary>
        public static string SetName(string text, string name)
        {
            if (name == null) name = string.Empty;
            return SetLeadingKey(text, "name", name.Trim());
        }

        /// <summary>Inserts or replaces <c>-- @server: &lt;value&gt;</c>. Empty value removes nothing — caller should skip the call instead.</summary>
        public static string SetServer(string text, string server)
        {
            if (server == null) server = string.Empty;
            return SetLeadingKey(text, "server", server.Trim());
        }

        /// <summary>Inserts or replaces <c>-- @database: &lt;value&gt;</c>.</summary>
        public static string SetDatabase(string text, string database)
        {
            if (database == null) database = string.Empty;
            return SetLeadingKey(text, "database", database.Trim());
        }

        /// <summary>
        /// Inserts or replaces a <c>-- @key: value</c> line inside the leading comment block. If the
        /// document has no leading comment block, prepends a fresh single-line block + blank-line separator.
        /// </summary>
        private static string SetLeadingKey(string text, string key, string value)
        {
            text = text ?? string.Empty;
            var nl = DetectLineEnding(text);

            int i = 0, len = text.Length;
            int keyLineStart = -1, keyLineEndExclusive = -1;
            int firstCommentLineStart = -1;

            while (i < len)
            {
                int lineStart = i;
                while (i < len && text[i] != '\n') i++;
                int lineEndContent = i;
                if (i < len) i++; // consume '\n'

                var raw = text.Substring(lineStart, lineEndContent - lineStart);
                if (raw.EndsWith("\r")) raw = raw.Substring(0, raw.Length - 1);
                var trimmed = raw.TrimStart();

                if (trimmed.Length == 0) break;
                if (!trimmed.StartsWith("--")) break;

                if (firstCommentLineStart < 0) firstCommentLineStart = lineStart;

                if (keyLineStart < 0)
                {
                    var rest = trimmed.Substring(2).TrimStart();
                    var token = "@" + key;
                    if (rest.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                        && (rest.Length == token.Length || rest[token.Length] == ':' || char.IsWhiteSpace(rest[token.Length])))
                    {
                        keyLineStart = lineStart;
                        keyLineEndExclusive = i;
                    }
                }
            }

            var newLine = "-- @" + key + ": " + value + nl;

            if (keyLineStart >= 0)
            {
                return text.Substring(0, keyLineStart) + newLine + text.Substring(keyLineEndExclusive);
            }
            if (firstCommentLineStart >= 0)
            {
                return text.Substring(0, firstCommentLineStart) + newLine + text.Substring(firstCommentLineStart);
            }
            // No comment block: prepend a new block + blank line separator.
            return newLine + nl + text;
        }

        /// <summary>
        /// Returns <paramref name="text"/> with every <c>-- @id: …</c> line removed AND
        /// leading/trailing blank lines / whitespace trimmed. Two stored-query files are
        /// "exact duplicates" iff this canonical form is byte-equal: it lets files compare
        /// equal regardless of where their @id lives (leading block — which leaves a blank
        /// separator line at the top once stripped — or the legacy padded-bottom placement)
        /// when the SQL itself is the same.
        /// </summary>
        public static string CanonicalContentForCompare(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return StripAllIdLines(text).Trim('\r', '\n', ' ', '\t');
        }

        /// <summary>
        /// Returns <paramref name="text"/> with every <c>-- @id: …</c> line removed. Used by
        /// <see cref="CanonicalContentForCompare"/> so @id placement never affects equality.
        /// </summary>
        private static string StripAllIdLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            var sb = new StringBuilder(text.Length);
            int i = 0, len = text.Length;
            while (i < len)
            {
                int lineStart = i;
                while (i < len && text[i] != '\n') i++;
                int contentEnd = i;
                if (i < len) i++; // consume '\n'
                int lineEndIncl = i;

                if (!IsIdLine(text, lineStart, contentEnd))
                    sb.Append(text, lineStart, lineEndIncl - lineStart);
            }
            return sb.ToString();
        }

        private static bool IsIdLine(string text, int lineStart, int contentEnd)
        {
            var raw = text.Substring(lineStart, contentEnd - lineStart);
            if (raw.EndsWith("\r")) raw = raw.Substring(0, raw.Length - 1);
            return IsIdLineText(raw);
        }

        private static bool IsIdLineText(string raw)
        {
            var trimmed = raw.TrimStart();
            if (!trimmed.StartsWith("--")) return false;
            var rest = trimmed.Substring(2).TrimStart();
            if (!rest.StartsWith("@id", StringComparison.OrdinalIgnoreCase)) return false;
            // Match exactly @id, @id:, or @id followed by whitespace — don't catch @ident etc.
            return rest.Length == 3 || rest[3] == ':' || char.IsWhiteSpace(rest[3]);
        }

        /// <summary>Splits into lines, each retaining its own terminator (the last may have none).</summary>
        private static List<string> SplitLinesKeepingTerminators(string text)
        {
            var lines = new List<string>();
            int i = 0, len = text.Length;
            while (i < len)
            {
                int start = i;
                while (i < len && text[i] != '\n') i++;
                if (i < len) i++; // include '\n'
                lines.Add(text.Substring(start, i - start));
            }
            return lines;
        }

        private static string StripTerminator(string line)
        {
            if (line.EndsWith("\n")) line = line.Substring(0, line.Length - 1);
            if (line.EndsWith("\r")) line = line.Substring(0, line.Length - 1);
            return line;
        }

        public static int LineNumberAtOffset(string text, int offset)
        {
            int line = 1;
            int max = Math.Min(offset, text == null ? 0 : text.Length);
            for (int i = 0; i < max; i++) if (text[i] == '\n') line++;
            return line;
        }

        public static string DetectLineEnding(string text)
        {
            if (text == null) return Environment.NewLine;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') return (i > 0 && text[i - 1] == '\r') ? "\r\n" : "\n";
            }
            return Environment.NewLine;
        }
    }
}
