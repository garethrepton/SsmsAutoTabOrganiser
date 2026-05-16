using System.Text.RegularExpressions;

namespace AutoTabOrganiser.Tracking
{
    internal static class ConnectionExtractor
    {
        // SSMS title is typically: "SQLQueryN.sql - SERVER.database (LOGIN (NN))*"
        //
        // The server portion can itself contain dots:
        //   - AzureSQL:        "tab.sql - myhost.database.windows.net.mydb (admin (61))"
        //   - LocalDB:         "tab.sql - (localdb)\MSSQLLocalDB.MyDB (sa (52))"
        //   - FQDN:            "tab.sql - sql01.corp.local.MyDB (sa (52))"
        // So the server-vs-db split must be on the *last* dot before " (", not the first.
        // We use a greedy `.+` for server, then require the database segment to have no dots
        // (matching SSMS's normal display convention) followed by " (" before the login info.
        private static readonly Regex TitleRegex =
            new Regex(@"^.+? - (?<server>.+)\.(?<db>[^.\s]+) \(", RegexOptions.Compiled);

        public static (string server, string database) FromWindowTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return (null, null);
            var m = TitleRegex.Match(title);
            return m.Success ? (m.Groups["server"].Value, m.Groups["db"].Value) : (null, null);
        }
    }
}
