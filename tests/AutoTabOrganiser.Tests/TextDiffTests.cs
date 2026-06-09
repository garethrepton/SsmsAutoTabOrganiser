using System;
using System.Linq;
using FluentAssertions;
using AutoTabOrganiser.Util;
using Xunit;

namespace AutoTabOrganiser.Tests
{
    public class TextDiffTests
    {
        [Fact]
        public void IdenticalInputs_ReturnEmpty()
        {
            TextDiff.Unified("SELECT 1\nGO", "SELECT 1\nGO", "a", "b").Should().BeEmpty();
        }

        [Fact]
        public void IdenticalInputs_DifferentLineEndings_ReturnEmpty()
        {
            TextDiff.Unified("SELECT 1\r\nGO", "SELECT 1\nGO", "a", "b").Should().BeEmpty();
        }

        [Fact]
        public void SingleLineChange_ProducesMinusAndPlus()
        {
            var diff = TextDiff.Unified(
                "SELECT 1\nFROM t\nWHERE x = 1",
                "SELECT 1\nFROM t\nWHERE x = 2",
                "old", "new");

            diff.Should().Contain("--- old");
            diff.Should().Contain("+++ new");
            diff.Should().Contain("-WHERE x = 1");
            diff.Should().Contain("+WHERE x = 2");
            diff.Should().Contain(" FROM t"); // context line, space-prefixed
        }

        [Fact]
        public void PureAddition_OnlyPlusLines()
        {
            var diff = TextDiff.Unified("a\nb", "a\nb\nc\nd", "old", "new");
            var body = diff.Split('\n').Select(l => l.TrimEnd('\r'))
                           .Where(l => l.Length > 0 && (l[0] == '+' || l[0] == '-'))
                           .Where(l => !l.StartsWith("+++") && !l.StartsWith("---")).ToList();
            body.Should().BeEquivalentTo(new[] { "+c", "+d" });
        }

        [Fact]
        public void PureDeletion_OnlyMinusLines()
        {
            var diff = TextDiff.Unified("a\nb\nc", "a", "old", "new");
            diff.Should().Contain("-b");
            diff.Should().Contain("-c");
            diff.Should().NotContain("\n+b");
        }

        [Fact]
        public void EmptyToContent_AllAdded()
        {
            var diff = TextDiff.Unified("", "SELECT 1", "old", "new");
            diff.Should().Contain("+SELECT 1");
        }

        [Fact]
        public void HunkHeader_HasPlausibleLineNumbers()
        {
            // Change at line 50 of a 100-line doc: the hunk must start near line 47
            // (3 lines of context), not at line 1.
            var oldLines = Enumerable.Range(1, 100).Select(i => "line " + i).ToArray();
            var newLines = (string[])oldLines.Clone();
            newLines[49] = "CHANGED";

            var diff = TextDiff.Unified(string.Join("\n", oldLines), string.Join("\n", newLines), "old", "new");

            diff.Should().Contain("@@ -47,");
            diff.Should().Contain("-line 50");
            diff.Should().Contain("+CHANGED");
            diff.Should().NotContain("line 90"); // far-away lines stay out of the hunk
        }

        [Fact]
        public void TwoDistantChanges_ProduceTwoHunks()
        {
            var oldLines = Enumerable.Range(1, 60).Select(i => "line " + i).ToArray();
            var newLines = (string[])oldLines.Clone();
            newLines[4]  = "FIRST";
            newLines[54] = "SECOND";

            var diff = TextDiff.Unified(string.Join("\n", oldLines), string.Join("\n", newLines), "old", "new");

            var hunkCount = diff.Split('\n').Count(l => l.StartsWith("@@"));
            hunkCount.Should().Be(2);
            diff.Should().Contain("+FIRST");
            diff.Should().Contain("+SECOND");
        }

        [Fact]
        public void AdjacentChanges_MergeIntoOneHunk()
        {
            var oldLines = Enumerable.Range(1, 20).Select(i => "line " + i).ToArray();
            var newLines = (string[])oldLines.Clone();
            newLines[8]  = "A";
            newLines[10] = "B";

            var diff = TextDiff.Unified(string.Join("\n", oldLines), string.Join("\n", newLines), "old", "new");

            var hunkCount = diff.Split('\n').Count(l => l.StartsWith("@@"));
            hunkCount.Should().Be(1);
        }

        [Fact]
        public void VeryLargeDivergentInputs_FallBackWithoutThrowing()
        {
            // 3000 completely distinct lines per side exceeds the DP budget → whole-replace
            // fallback. Must not throw or hang.
            var a = string.Join("\n", Enumerable.Range(1, 3000).Select(i => "old " + i));
            var b = string.Join("\n", Enumerable.Range(1, 3000).Select(i => "new " + i));

            var diff = TextDiff.Unified(a, b, "old", "new");

            diff.Should().Contain("-old 1");
            diff.Should().Contain("+new 3000");
        }
    }
}
