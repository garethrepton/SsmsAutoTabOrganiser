using System.Linq;
using AutoTabOrganiser.Metadata;
using FluentAssertions;
using Xunit;

namespace AutoTabOrganiser.Tests
{
    public class TrailingIdTests
    {
        [Fact]
        public void SetId_FreshFile_AppendsWith40BlankLinesOfPadding()
        {
            var input = "SELECT 1;\n";
            var result = MetadataWriter.SetId(input, "abc-123");

            // The file still starts with the original content.
            result.Should().StartWith("SELECT 1;\n");
            // The trailing line is the @id with a single newline terminator.
            result.Should().EndWith("-- @id: abc-123\n");

            // 40 blank lines between "SELECT 1;\n" and "-- @id: …\n".
            var middle = result.Substring("SELECT 1;\n".Length,
                result.Length - "SELECT 1;\n".Length - "-- @id: abc-123\n".Length);
            middle.Should().Be(string.Concat(Enumerable.Repeat("\n", MetadataWriter.TrailingIdPaddingLines)));
        }

        [Fact]
        public void SetId_FileEndingWithoutNewline_TerminatesAndPads()
        {
            // No newline anywhere in the input → writer uses Environment.NewLine.
            var input = "SELECT 1;";
            var nl = System.Environment.NewLine;
            var result = MetadataWriter.SetId(input, "abc");

            result.Should().StartWith("SELECT 1;" + nl);
            result.Should().EndWith("-- @id: abc" + nl);
            // 1 line ending to terminate the content + 40 blanks + the id line.
            var blanks = string.Concat(Enumerable.Repeat(nl, MetadataWriter.TrailingIdPaddingLines));
            result.Should().Be("SELECT 1;" + nl + blanks + "-- @id: abc" + nl);
        }

        [Fact]
        public void SetId_LegacyLeadingId_MovesItToBottomAndStripsLeading()
        {
            var input = "-- @folder: x\n-- @id: old-id\nSELECT 1;\n";
            var result = MetadataWriter.SetId(input, "new-id");

            // Leading @id line is gone.
            result.Should().NotContain("-- @id: old-id");
            // Folder is still in the leading block.
            result.Should().StartWith("-- @folder: x\nSELECT 1;\n");
            // New @id at the bottom.
            result.Should().EndWith("-- @id: new-id\n");
        }

        [Fact]
        public void SetId_AlreadyAtBottom_DoesNotKeepGrowingPadding()
        {
            var first = MetadataWriter.SetId("SELECT 1;\n", "id-v1");
            var second = MetadataWriter.SetId(first, "id-v2");

            // Second call must not stack another 40-line pad on top of the first.
            // It should look identical to a fresh SetId call with the new value.
            var fresh = MetadataWriter.SetId("SELECT 1;\n", "id-v2");
            second.Should().Be(fresh);
        }

        [Fact]
        public void SetId_PreservesExistingTrailingBlanks_DoesNotAddMoreThanNeeded()
        {
            // 50 trailing blank lines already — exceeds the 40 floor, so we shouldn't add any.
            var blanks = string.Concat(Enumerable.Repeat("\n", 50));
            var input = "SELECT 1;\n" + blanks;
            var result = MetadataWriter.SetId(input, "abc");

            // Bytes between the content line and the @id line should still be 50 newlines.
            var middle = result.Substring("SELECT 1;\n".Length,
                result.Length - "SELECT 1;\n".Length - "-- @id: abc\n".Length);
            middle.Should().Be(blanks);
        }

        [Fact]
        public void SetId_EmptyId_ReturnsTextUnchanged()
        {
            var input = "SELECT 1;\n";
            MetadataWriter.SetId(input, "").Should().Be(input);
            MetadataWriter.SetId(input, "   ").Should().Be(input);
            MetadataWriter.SetId(input, null).Should().Be(input);
        }

        [Fact]
        public void SetId_PreservesCrlfLineEndings()
        {
            var input = "SELECT 1;\r\n";
            var result = MetadataWriter.SetId(input, "abc");
            result.Should().EndWith("-- @id: abc\r\n");
            // No bare \n that wasn't preceded by \r (i.e. the file is consistently CRLF).
            for (int i = 0; i < result.Length; i++)
            {
                if (result[i] == '\n')
                    (i > 0 && result[i - 1] == '\r').Should().BeTrue($"unpaired \\n at offset {i}");
            }
        }

        [Fact]
        public void Parse_ReadsTrailingId()
        {
            var blanks = string.Concat(Enumerable.Repeat("\n", 40));
            var input = "-- @folder: x\nSELECT 1;\n" + blanks + "-- @id: abc-123\n";
            var meta = MetadataParser.Parse(input);
            meta.Id.Should().Be("abc-123");
            meta.Folder.Should().Be("x");
        }

        [Fact]
        public void Parse_LegacyLeadingId_StillWorks()
        {
            var input = "-- @folder: x\n-- @id: legacy-id\nSELECT 1;\n";
            var meta = MetadataParser.Parse(input);
            meta.Id.Should().Be("legacy-id");
        }

        [Fact]
        public void Parse_BothLeadingAndTrailingId_TrailingWins()
        {
            // Hypothetical: a file edited by both old and new code. Trailing is canonical.
            var blanks = string.Concat(Enumerable.Repeat("\n", 40));
            var input = "-- @id: old-leading\nSELECT 1;\n" + blanks + "-- @id: new-trailing\n";
            var meta = MetadataParser.Parse(input);
            meta.Id.Should().Be("new-trailing");
        }

        [Fact]
        public void InjectId_IsAliasForSetId()
        {
            var a = MetadataWriter.InjectId("SELECT 1;\n", "abc");
            var b = MetadataWriter.SetId("SELECT 1;\n", "abc");
            a.Should().Be(b);
        }
    }
}
