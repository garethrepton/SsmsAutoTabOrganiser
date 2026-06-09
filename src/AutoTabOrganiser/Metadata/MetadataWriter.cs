using System;
using System.Text;

namespace AutoTabOrganiser.Metadata
{
    internal static class MetadataWriter
    {
        /// <summary>
        /// Minimum number of blank lines between the last non-blank content line and the
        /// trailing <c>-- @id: …</c> marker. Keeps the id well below the editing area so
        /// it doesn't show up in normal scrolling.
        /// </summary>
        public const int TrailingIdPaddingLines = 40;

        public static string GenerateTabId() => Guid.NewGuid().ToString("D");

        /// <summary>
        /// Returns the new full text with <c>-- @id: &lt;value&gt;</c> placed at the bottom of
        /// the file, separated from the last non-blank content line by at least
        /// <see cref="TrailingIdPaddingLines"/> blank lines. Any pre-existing <c>-- @id: …</c>
        /// line (legacy leading-block or trailing) is removed first so the file ends up with
        /// exactly one canonical trailing @id.
        /// </summary>
        /// <remarks>
        /// Returns <paramref name="text"/> unchanged when <paramref name="id"/> is empty.
        /// </remarks>
        public static string InjectId(string text, string id) => SetId(text, id);

        /// <summary>
        /// Inserts or replaces the trailing <c>-- @id: &lt;value&gt;</c> line. See
        /// <see cref="InjectId"/> for the placement contract.
        /// </summary>
        public static string SetId(string text, string id)
        {
            text = text ?? string.Empty;
            id = (id ?? string.Empty).Trim();
            if (id.Length == 0) return text;

            var nl = DetectLineEnding(text);
            var stripped = StripAllIdLines(text);
            return AppendTrailingId(stripped, id, nl);
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
        /// Returns <paramref name="text"/> with every <c>-- @id: …</c> line removed AND trailing
        /// blank lines / whitespace trimmed. Two stored-query files are "exact duplicates" iff
        /// this canonical form is byte-equal: it lets a new file (with @id padded at the bottom)
        /// compare equal to a legacy file (with @id in the leading block) when the SQL itself
        /// is the same.
        /// </summary>
        public static string CanonicalContentForCompare(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return StripAllIdLines(text).TrimEnd('\r', '\n', ' ', '\t');
        }

        /// <summary>
        /// Returns <paramref name="text"/> with every <c>-- @id: …</c> line removed. Used by
        /// <see cref="SetId"/> to canonicalise: legacy files with a leading @id get it stripped,
        /// then a fresh trailing @id is appended.
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
            var trimmed = raw.TrimStart();
            if (!trimmed.StartsWith("--")) return false;
            var rest = trimmed.Substring(2).TrimStart();
            if (!rest.StartsWith("@id", StringComparison.OrdinalIgnoreCase)) return false;
            // Match exactly @id, @id:, or @id followed by whitespace — don't catch @ident etc.
            return rest.Length == 3 || rest[3] == ':' || char.IsWhiteSpace(rest[3]);
        }

        /// <summary>
        /// Appends <c>-- @id: id</c> as a trailing line, ensuring at least
        /// <see cref="TrailingIdPaddingLines"/> blank lines between the last non-blank content
        /// line and the new @id line. Existing trailing blank lines are counted toward the
        /// quota so the padding doesn't keep growing on repeated saves.
        /// </summary>
        private static string AppendTrailingId(string text, string id, string nl)
        {
            int len = text.Length;

            // Find the offset just after the last non-blank line.
            int lastNonBlankEnd = 0;
            int trailingBlanks = 0;
            int i = 0;
            while (i < len)
            {
                int lineStart = i;
                while (i < len && text[i] != '\n') i++;
                int contentEnd = i;
                if (i < len) i++; // consume '\n'
                int lineEndIncl = i;

                var raw = text.Substring(lineStart, contentEnd - lineStart);
                if (raw.EndsWith("\r")) raw = raw.Substring(0, raw.Length - 1);
                if (raw.Trim().Length > 0)
                {
                    lastNonBlankEnd = lineEndIncl;
                    trailingBlanks = 0;
                }
                else
                {
                    trailingBlanks++;
                }
            }

            var sb = new StringBuilder(len + (TrailingIdPaddingLines + 2) * nl.Length + id.Length + 12);
            sb.Append(text);
            // If the last content line wasn't terminated, terminate it now. The terminator
            // doesn't count as a blank line — it just ends the content line.
            if (len > 0 && text[len - 1] != '\n')
            {
                sb.Append(nl);
            }

            int blanksToAdd = Math.Max(0, TrailingIdPaddingLines - trailingBlanks);
            for (int k = 0; k < blanksToAdd; k++) sb.Append(nl);
            sb.Append("-- @id: ").Append(id).Append(nl);
            return sb.ToString();
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
