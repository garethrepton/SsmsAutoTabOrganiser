using FluentAssertions;
using AutoTabOrganiser.Metadata;
using Xunit;

namespace AutoTabOrganiser.Tests
{
    public class MetadataParserTests
    {
        [Fact]
        public void Parses_FolderNameId()
        {
            var text = "-- @folder: Investigations/PROD-1234\n-- @name: Find slow queries\n-- @id: 7f3c-a1b2-9c8d\nSELECT 1;\n";
            var m = MetadataParser.Parse(text);
            m.Folder.Should().Be("Investigations/PROD-1234");
            m.Name.Should().Be("Find slow queries");
            m.Id.Should().Be("7f3c-a1b2-9c8d");
        }

        [Fact]
        public void NoSnapshotFlag()
        {
            var text = "-- @folder: x\n-- @nosnapshot\nSELECT 1;\n";
            var m = MetadataParser.Parse(text);
            m.NoSnapshot.Should().BeTrue();
        }

        [Fact]
        public void MultilineDesc()
        {
            var text =
                "-- @folder: x\n" +
                "-- @desc: |\n" +
                "--   First line.\n" +
                "--   Second line.\n" +
                "-- @id: abc\n" +
                "SELECT 1;\n";
            var m = MetadataParser.Parse(text);
            m.Description.Should().Contain("First line.");
            m.Description.Should().Contain("Second line.");
            m.Id.Should().Be("abc");
        }

        [Fact]
        public void Tags_OnlyFromHeader_NotFromBodyComments()
        {
            var text =
                "-- @folder: x\n" +
                "-- #prod #performance\n" +
                "SELECT TOP 10 * FROM dbo.X;\n" +
                "-- middle comment with #shard-3\n" +
                "/* block comment with #archive */\n" +
                "SELECT * FROM #temp_results;\n";
            var m = MetadataParser.Parse(text);
            m.Tags.Should().BeEquivalentTo("prod", "performance");
            m.Tags.Should().NotContain("shard-3");
            m.Tags.Should().NotContain("archive");
            m.Tags.Should().NotContain("temp_results");
        }

        [Fact]
        public void NoCommentBlock_NoTags()
        {
            var text = "SELECT 1; /* #only-here */\n";
            var m = MetadataParser.Parse(text);
            m.Tags.Should().BeEmpty();
            m.CommentBlockEndExclusive.Should().Be(0);
        }

        [Fact]
        public void CommentBlockEnd_IsCorrect()
        {
            var text = "-- @folder: x\n-- @name: y\nSELECT 1;\n";
            var m = MetadataParser.Parse(text);
            // First two lines are comments. End-exclusive offset must equal length of those two lines (incl. \n).
            m.CommentBlockEndExclusive.Should().Be("-- @folder: x\n-- @name: y\n".Length);
        }
    }
}
