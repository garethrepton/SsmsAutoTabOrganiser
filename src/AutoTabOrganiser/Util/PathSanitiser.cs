using System;
using System.Text;

namespace AutoTabOrganiser.Util
{
    internal static class PathSanitiser
    {
        /// <summary>
        /// Derive a safe filename component from the first non-blank, non-comment line of a SQL document.
        /// 1. Strip leading/trailing whitespace.
        /// 2. Strip a trailing semicolon.
        /// 3. Truncate to 60 characters.
        /// 4. Sanitise to [A-Za-z0-9._ -]; other chars -> '_'.
        /// 5. Collapse runs of '_' and trim them from both ends.
        /// 6. If empty, returns null (caller falls back to @name / tab title / "untitled").
        /// </summary>
        public static string FromFirstLine(string documentText)
        {
            var firstLine = ExtractFirstLine(documentText);
            if (string.IsNullOrEmpty(firstLine)) return null;
            return Sanitise(firstLine);
        }

        public static string Sanitise(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            if (s.EndsWith(";")) s = s.Substring(0, s.Length - 1).TrimEnd();
            if (s.Length > 60) s = s.Substring(0, 60);

            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                    || c == '.' || c == '_' || c == ' ' || c == '-')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }
            var raw = sb.ToString();

            var collapsed = new StringBuilder(raw.Length);
            char prev = '\0';
            foreach (var c in raw)
            {
                if (c == '_' && prev == '_') continue;
                collapsed.Append(c);
                prev = c;
            }

            var result = collapsed.ToString().Trim('_').Trim();
            return result.Length == 0 ? null : result;
        }

        private static string ExtractFirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0;
            int len = text.Length;
            while (i < len)
            {
                int lineStart = i;
                while (i < len && text[i] != '\n') i++;
                var line = text.Substring(lineStart, i - lineStart).TrimEnd('\r');
                if (i < len) i++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("--")) continue;
                if (trimmed.StartsWith("/*"))
                {
                    while (i < len && !text.Substring(Math.Max(0, i - 2), Math.Min(2, i)).Contains("*/")) i++;
                    continue;
                }
                return trimmed;
            }
            return null;
        }
    }
}
