using AutoTabOrganiser.Tracking;
using FluentAssertions;
using Xunit;

namespace AutoTabOrganiser.Tests
{
    public class ConnectionExtractorTests
    {
        [Theory]
        [InlineData("SQLQuery1.sql - localhost.master (sa (52))",                      "localhost",                "master")]
        [InlineData("SQLQuery1.sql - MACHINE\\SQLEXPRESS.master (sa (52))",            "MACHINE\\SQLEXPRESS",      "master")]
        // AzureSQL: server is a multi-dot FQDN. Previous regex split on the first dot
        // and put most of the server into the database field.
        [InlineData("SQLQuery1.sql - myhost.database.windows.net.mydb (admin (61))",   "myhost.database.windows.net", "mydb")]
        // Generic FQDN: the dots before the database segment all belong to the server.
        [InlineData("foo.sql - sql01.corp.local.AppDB (sa (52))",                      "sql01.corp.local",         "AppDB")]
        // Filenames may contain hyphens — the non-greedy "^.+? - " separator picks the FIRST " - ".
        [InlineData("query-with-dash.sql - host.db (admin (1))",                       "host",                     "db")]
        // The asterisk suffix that SSMS adds on dirty tabs sits outside the captured fields.
        [InlineData("SQLQuery1.sql - localhost.master (sa (52))*",                     "localhost",                "master")]
        public void FromWindowTitle_ParsesCommonFormats(string title, string expectedServer, string expectedDb)
        {
            var (server, db) = ConnectionExtractor.FromWindowTitle(title);
            server.Should().Be(expectedServer);
            db.Should().Be(expectedDb);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        // Disconnected tab — no " - SERVER.DB (LOGIN" segment.
        [InlineData("SQLQuery1.sql")]
        // Looks like it has a separator but no " (" tail — disconnected.
        [InlineData("SQLQuery1.sql - localhost.master")]
        public void FromWindowTitle_ReturnsNullsOnNonMatching(string title)
        {
            var (server, db) = ConnectionExtractor.FromWindowTitle(title);
            server.Should().BeNull();
            db.Should().BeNull();
        }
    }
}
