using System;

namespace AutoTabOrganiser.Metadata
{
    internal static class MetadataWriter
    {
        public static string GenerateShortId()
        {
            var g = Guid.NewGuid().ToString("N");
            return $"{g.Substring(0, 8)}-{g.Substring(8, 4)}-{g.Substring(12, 4)}";
        }

        /// <summary>
        /// Returns the new full text with `-- @id: &lt;value&gt;` injected as the last line of the
        /// existing leading comment block. If the document has no leading comment block, returns
        /// null (caller must not inject — see SPEC.md "@id injection" rules).
        /// </summary>
        public static string InjectId(string text, string id, int commentBlockEndExclusive)
        {
            if (commentBlockEndExclusive <= 0 || string.IsNullOrEmpty(id)) return null;
            var newLine = "-- @id: " + id;
            var insertion = newLine + DetectLineEnding(text);

            // Insert at commentBlockEndExclusive (just after the last comment line's newline).
            return text.Substring(0, commentBlockEndExclusive) + insertion + text.Substring(commentBlockEndExclusive);
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

        /// <summary>
        /// Inserts or replaces the leading <c>-- @id: &lt;value&gt;</c> line so the file carries its
        /// stable tab identity even after being closed and reopened from disk.
        /// </summary>
        public static string SetId(string text, string id)
        {
            if (id == null) id = string.Empty;
            return SetLeadingKey(text, "id", id.Trim());
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
