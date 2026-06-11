using System.Linq;
using AutoTabOrganiser.Metadata;
using FluentAssertions;
using Xunit;

namespace AutoTabOrganiser.Tests
{
    public class IdPlacementTests
    {
        // ---- SetId (full-text rewrite: import, restore, save-to-scripts) ----

        [Fact]
        public void SetId_FreshFileWithoutCommentBlock_PrependsIdAndBlankSeparator()
        {
            var result = MetadataWriter.SetId("SELECT 1;\n", "abc-123");
            result.Should().Be("-- @id: abc-123\n\nSELECT 1;\n");
        }

        [Fact]
        public void SetId_ExistingCommentBlock_InsertsIdAsSecondLine()
        {
            var result = MetadataWriter.SetId("-- my query\nSELECT 1;\n", "abc");
            result.Should().Be("-- my query\n-- @id: abc\nSELECT 1;\n");
        }

        [Fact]
        public void SetId_LegacyTrailingId_MovesToTopAndDropsThePadding()
        {
            var blanks = string.Concat(Enumerable.Repeat("\n", 40));
            var input = "SELECT 1;\n" + blanks + "-- @id: old-id\n";

            var result = MetadataWriter.SetId(input, "new-id");

            result.Should().Be("-- @id: new-id\n\nSELECT 1;\n");
        }

        [Fact]
        public void SetId_ExistingLeadingBlockId_ReplacedInPlace()
        {
            var input = "-- @folder: x\n-- @id: old-id\nSELECT 1;\n";
            var result = MetadataWriter.SetId(input, "new-id");
            result.Should().Be("-- @folder: x\n-- @id: new-id\nSELECT 1;\n");
        }

        [Fact]
        public void SetId_RepeatedCalls_AreIdempotent()
        {
            foreach (var input in new[] { "SELECT 1;\n", "-- header\nSELECT 1;\n" })
            {
                var twice = MetadataWriter.SetId(MetadataWriter.SetId(input, "id-v1"), "id-v2");
                var fresh = MetadataWriter.SetId(input, "id-v2");
                twice.Should().Be(fresh, $"repeated SetId must not accumulate lines (input: {input})");
            }
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
            var result = MetadataWriter.SetId("SELECT 1;\r\n", "abc");
            result.Should().Be("-- @id: abc\r\n\r\nSELECT 1;\r\n");
            for (int i = 0; i < result.Length; i++)
            {
                if (result[i] == '\n')
                    (i > 0 && result[i - 1] == '\r').Should().BeTrue($"unpaired \\n at offset {i}");
            }
        }

        [Fact]
        public void InjectId_IsAliasForSetId()
        {
            var a = MetadataWriter.InjectId("SELECT 1;\n", "abc");
            var b = MetadataWriter.SetId("SELECT 1;\n", "abc");
            a.Should().Be(b);
        }

        // ---- ComputeIdInjection (buffer edits: live SSMS tabs) ----

        [Fact]
        public void ComputeIdInjection_NoCommentBlock_PrependsHeaderThenIdThenBlank()
        {
            var inj = MetadataWriter.ComputeIdInjection("SELECT 1;\n", "abc", "New Tab 2026-06-11 14:32 on PRODDB01");

            inj.InsertOffset.Should().Be(0);
            inj.InsertedText.Should().Be("-- New Tab 2026-06-11 14:32 on PRODDB01\n-- @id: abc\n\n");
        }

        [Fact]
        public void ComputeIdInjection_NoCommentBlock_NoHeader_PrependsIdOnly()
        {
            var inj = MetadataWriter.ComputeIdInjection("SELECT 1;\n", "abc", null);

            inj.InsertOffset.Should().Be(0);
            inj.InsertedText.Should().Be("-- @id: abc\n\n");
        }

        [Fact]
        public void ComputeIdInjection_ExistingCommentBlock_InsertsAsSecondLine_NoGeneratedHeader()
        {
            var text = "-- my analysis query\nSELECT 1;\n";
            var inj = MetadataWriter.ComputeIdInjection(text, "abc", "New Tab 2026-06-11");

            inj.InsertOffset.Should().Be("-- my analysis query\n".Length);
            inj.InsertedText.Should().Be("-- @id: abc\n");

            var applied = text.Insert(inj.InsertOffset, inj.InsertedText);
            applied.Should().Be("-- my analysis query\n-- @id: abc\nSELECT 1;\n");
        }

        [Fact]
        public void ComputeIdInjection_LoneCommentLineWithoutTerminator_AppendsBelow()
        {
            var nl = System.Environment.NewLine;
            var inj = MetadataWriter.ComputeIdInjection("-- header only", "abc", null);

            inj.InsertOffset.Should().Be("-- header only".Length);
            inj.InsertedText.Should().Be(nl + "-- @id: abc" + nl);
        }

        [Fact]
        public void ComputeIdInjection_EmptyId_ReturnsNull()
        {
            MetadataWriter.ComputeIdInjection("SELECT 1;\n", "", "h").Should().BeNull();
            MetadataWriter.ComputeIdInjection("SELECT 1;\n", null, "h").Should().BeNull();
        }

        // ---- Parser round-trips for every placement ----

        [Fact]
        public void Parse_ReadsIdFromSecondLineOfLeadingBlock()
        {
            var text = "-- New Tab 2026-06-11 14:32 on PRODDB01\n-- @id: abc-123\n\nSELECT 1;\n";
            var meta = MetadataParser.Parse(text);
            meta.Id.Should().Be("abc-123");
        }

        [Fact]
        public void Parse_SetIdRoundTrip()
        {
            var meta = MetadataParser.Parse(MetadataWriter.SetId("-- header\nSELECT 1;\n", "rt-id"));
            meta.Id.Should().Be("rt-id");
        }

        [Fact]
        public void Parse_LegacyTrailingId_StillWorks()
        {
            var blanks = string.Concat(Enumerable.Repeat("\n", 40));
            var input = "-- @folder: x\nSELECT 1;\n" + blanks + "-- @id: abc-123\n";
            var meta = MetadataParser.Parse(input);
            meta.Id.Should().Be("abc-123");
            meta.Folder.Should().Be("x");
        }

        [Fact]
        public void Parse_BothLeadingAndTrailingId_TrailingWins()
        {
            // A file edited by both old and new code. The trailing id is the one the store
            // already knows, so it stays authoritative.
            var blanks = string.Concat(Enumerable.Repeat("\n", 40));
            var input = "-- @id: new-leading\nSELECT 1;\n" + blanks + "-- @id: old-trailing\n";
            var meta = MetadataParser.Parse(input);
            meta.Id.Should().Be("old-trailing");
        }

        [Fact]
        public void CanonicalCompare_NewLeadingVsLegacyTrailing_AreEqual()
        {
            var blanks = string.Concat(Enumerable.Repeat("\n", 40));
            var legacy = "SELECT 1;\n" + blanks + "-- @id: a\n";
            var modern = MetadataWriter.SetId("SELECT 1;\n", "b");

            MetadataWriter.CanonicalContentForCompare(legacy)
                .Should().Be(MetadataWriter.CanonicalContentForCompare(modern));
        }
    }
}
