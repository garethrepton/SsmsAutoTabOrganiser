using System;
using System.Security.Cryptography;
using System.Text;

namespace AutoTabOrganiser.Util
{
    internal static class Hashing
    {
        public static string Sha256Hex(string text)
        {
            if (text == null) text = string.Empty;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Fingerprint = SHA-256 of normalised text:
        /// - leading comment block stripped
        /// - whitespace runs collapsed to single space
        /// - leading/trailing whitespace trimmed
        /// Used as a fallback identity when @id is missing.
        /// </summary>
        public static string Fingerprint(string text)
        {
            if (string.IsNullOrEmpty(text)) return Sha256Hex(string.Empty);
            var stripped = StripLeadingComments(text);
            var normalised = CollapseWhitespace(stripped);
            return Sha256Hex(normalised);
        }

        private static string StripLeadingComments(string text)
        {
            int i = 0;
            int len = text.Length;
            while (i < len)
            {
                int lineStart = i;
                while (i < len && text[i] != '\n') i++;
                var line = text.Substring(lineStart, i - lineStart).TrimEnd('\r');
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("--")) { if (i < len) i++; continue; }
                if (trimmed.Length == 0) { if (i < len) i++; continue; }
                return text.Substring(lineStart);
            }
            return string.Empty;
        }

        private static string CollapseWhitespace(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool prevSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    if (!prevSpace) sb.Append(' ');
                    prevSpace = true;
                }
                else
                {
                    sb.Append(c);
                    prevSpace = false;
                }
            }
            return sb.ToString().Trim();
        }
    }
}
