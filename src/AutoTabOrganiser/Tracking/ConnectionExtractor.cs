using System.Text.RegularExpressions;

namespace AutoTabOrganiser.Tracking
{
    internal static class ConnectionExtractor
    {
        // SSMS title is typically: "SQLQueryN.sql - SERVER.database (LOGIN (NN))*"
        // Best-effort regex; if it doesn't match, server/database stay null.
        private static readonly Regex TitleRegex =
            new Regex(@"^.+? - (?<server>[^\.]+)\.(?<db>[^ ]+) \(", RegexOptions.Compiled);

        public static (string server, string database) FromWindowTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return (null, null);
            var m = TitleRegex.Match(title);
            return m.Success ? (m.Groups["server"].Value, m.Groups["db"].Value) : (null, null);
        }
    }
}
