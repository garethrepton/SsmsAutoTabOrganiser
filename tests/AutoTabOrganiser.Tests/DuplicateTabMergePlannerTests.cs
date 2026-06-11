using System.Linq;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Storage;
using FluentAssertions;
using Xunit;

namespace AutoTabOrganiser.Tests
{
    public class DuplicateTabMergePlannerTests
    {
        private static DuplicateTabMergePlanner.Row Row(string tabId, string body, bool isOpen = false, long ts = 0)
            => new DuplicateTabMergePlanner.Row
            {
                TabId = tabId,
                IsOpen = isOpen,
                Ts = ts,
                // Real rows carry the tab's own @id inside the content — that's exactly the
                // difference the canonical compare must see through.
                Content = MetadataWriter.SetId(body, tabId),
            };

        [Fact]
        public void Plan_TwoClosedCopiesDifferingOnlyByIdLine_MergesOlderIntoNewer()
        {
            var merges = DuplicateTabMergePlanner.Plan(new[]
            {
                Row("tab-old", "SELECT a FROM t;\n", ts: 100),
                Row("tab-new", "SELECT a FROM t;\n", ts: 200),
            });

            merges.Should().ContainSingle();
            merges[0].WinnerTabId.Should().Be("tab-new");
            merges[0].LoserTabId.Should().Be("tab-old");
        }

        [Fact]
        public void Plan_OpenRowWins_EvenWhenOlder()
        {
            var merges = DuplicateTabMergePlanner.Plan(new[]
            {
                Row("tab-open",   "SELECT 1;\n", isOpen: true, ts: 100),
                Row("tab-closed", "SELECT 1;\n", ts: 200),
            });

            merges.Should().ContainSingle();
            merges[0].WinnerTabId.Should().Be("tab-open");
            merges[0].LoserTabId.Should().Be("tab-closed");
        }

        [Fact]
        public void Plan_OpenRowsAreNeverMergedAway()
        {
            var merges = DuplicateTabMergePlanner.Plan(new[]
            {
                Row("tab-a", "SELECT 1;\n", isOpen: true, ts: 100),
                Row("tab-b", "SELECT 1;\n", isOpen: true, ts: 200),
            });

            merges.Should().BeEmpty();
        }

        [Fact]
        public void Plan_DifferentContent_NoMerges()
        {
            var merges = DuplicateTabMergePlanner.Plan(new[]
            {
                Row("tab-a", "SELECT 1;\n"),
                Row("tab-b", "SELECT 2;\n"),
            });

            merges.Should().BeEmpty();
        }

        [Fact]
        public void Plan_BlankRows_AreNotTreatedAsDuplicatesOfEachOther()
        {
            var merges = DuplicateTabMergePlanner.Plan(new[]
            {
                new DuplicateTabMergePlanner.Row { TabId = "tab-a", Content = "" },
                new DuplicateTabMergePlanner.Row { TabId = "tab-b", Content = "   \r\n\r\n" },
                new DuplicateTabMergePlanner.Row { TabId = "tab-c", Content = null },
            });

            merges.Should().BeEmpty();
        }

        [Fact]
        public void Plan_ThreeCopies_AllLosersFoldIntoTheSameWinner()
        {
            var merges = DuplicateTabMergePlanner.Plan(new[]
            {
                Row("tab-a", "SELECT x;\n", ts: 100),
                Row("tab-b", "SELECT x;\n", ts: 300),
                Row("tab-c", "SELECT x;\n", ts: 200),
            });

            merges.Should().HaveCount(2);
            merges.Select(m => m.WinnerTabId).Distinct().Should().BeEquivalentTo(new[] { "tab-b" });
            merges.Select(m => m.LoserTabId).Should().BeEquivalentTo(new[] { "tab-a", "tab-c" });
        }

        [Fact]
        public void Plan_TiedTimestamps_WinnerIsDeterministicByTabId()
        {
            var merges1 = DuplicateTabMergePlanner.Plan(new[]
            {
                Row("tab-b", "SELECT y;\n", ts: 100),
                Row("tab-a", "SELECT y;\n", ts: 100),
            });
            var merges2 = DuplicateTabMergePlanner.Plan(new[]
            {
                Row("tab-a", "SELECT y;\n", ts: 100),
                Row("tab-b", "SELECT y;\n", ts: 100),
            });

            merges1.Should().ContainSingle();
            merges2.Should().ContainSingle();
            merges1[0].WinnerTabId.Should().Be(merges2[0].WinnerTabId, "winner must not depend on input order");
            merges1[0].WinnerTabId.Should().Be("tab-a");
        }

        [Fact]
        public void Plan_LegacyLeadingIdVsTrailingId_StillRecognisedAsDuplicates()
        {
            // Copies can carry their "-- @id:" anywhere — leading block or the legacy
            // padded-bottom placement. Canonical compare must match them regardless.
            var legacy = "-- @id: tab-legacy\r\nSELECT z FROM t;\r\n";
            var merges = DuplicateTabMergePlanner.Plan(new[]
            {
                new DuplicateTabMergePlanner.Row { TabId = "tab-legacy", Content = legacy, Ts = 100 },
                Row("tab-modern", "SELECT z FROM t;\r\n", ts: 200),
            });

            merges.Should().ContainSingle();
            merges[0].WinnerTabId.Should().Be("tab-modern");
            merges[0].LoserTabId.Should().Be("tab-legacy");
        }
    }
}
