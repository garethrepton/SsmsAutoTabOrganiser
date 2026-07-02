using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Tree;
using Xunit;

namespace AutoTabOrganiser.Tests
{
    public class QuickSwitchRankerTests
    {
        private static TabSummary Tab(string name, string folder = null, string tags = null,
                                      bool open = false, long activated = 0, long ts = 0, long access = 0)
            => new TabSummary
            {
                TabId = name, Name = name, Folder = folder, TagsCsv = tags,
                IsOpen = open, LastActivatedTs = activated, Ts = ts, AccessCount = access,
            };

        private static List<string> Names(IEnumerable<TabSummary> rows) => rows.Select(r => r.Name).ToList();

        // ---- scoring tiers ----

        [Fact]
        public void ExactName_BeatsPrefix_BeatsSubstring()
        {
            var terms = new[] { "orders" };
            var exact = QuickSwitchRanker.Score(Tab("orders"), terms);
            var prefix = QuickSwitchRanker.Score(Tab("orders by region"), terms);
            var wordStart = QuickSwitchRanker.Score(Tab("customer orders"), terms);
            var substring = QuickSwitchRanker.Score(Tab("reorderscript"), terms);
            exact.Should().BeGreaterThan(prefix);
            prefix.Should().BeGreaterThan(wordStart);
            wordStart.Should().BeGreaterThan(substring);
        }

        [Fact]
        public void CamelCaseHump_CountsAsWordStart()
        {
            var terms = new[] { "orders" };
            var hump = QuickSwitchRanker.Score(Tab("CustomerOrders"), terms);
            var interior = QuickSwitchRanker.Score(Tab("reorderscript"), terms);
            hump.Should().BeGreaterThan(interior);
        }

        [Fact]
        public void NameHit_BeatsFolderHit_BeatsContentOnly()
        {
            var terms = new[] { "prod" };
            var name = QuickSwitchRanker.Score(Tab("prod checks"), terms);
            var folder = QuickSwitchRanker.Score(Tab("checks", folder: "prod"), terms);
            var contentOnly = QuickSwitchRanker.Score(Tab("misc"), terms);
            name.Should().BeGreaterThan(folder);
            folder.Should().BeGreaterThan(contentOnly);
            contentOnly.Should().BeGreaterThan(0); // admitted by FTS — still a hit
        }

        [Fact]
        public void FuzzyNameHit_BeatsContentOnly()
        {
            var terms = new[] { "cusord" };
            var fuzzy = QuickSwitchRanker.Score(Tab("CustomerOrders"), terms);
            var contentOnly = QuickSwitchRanker.Score(Tab("zzz"), terms);
            fuzzy.Should().BeGreaterThan(contentOnly);
        }

        // ---- fuzzy matching ----

        [Theory]
        [InlineData("CustomerOrders", "cusord", true)]
        [InlineData("CustomerOrders", "CUSORD", true)]
        [InlineData("CustomerOrders", "customerorders", true)]
        [InlineData("CustomerOrders", "ordcus", false)] // out of order
        [InlineData("CustomerOrders", "xyz", false)]
        [InlineData(null, "a", false)]
        [InlineData("abc", "", false)]
        public void FuzzyMatches_IsAnOrderedSubsequence(string text, string term, bool expected)
            => QuickSwitchRanker.FuzzyMatches(text, term).Should().Be(expected);

        // ---- ranking / ordering ----

        [Fact]
        public void Rank_NameMatch_SortsAboveContentOnlyMatch_EvenWhenColder()
        {
            var rows = new List<TabSummary>
            {
                Tab("misc scratch", open: true, activated: 2000, access: 50), // content-only hit, hot
                Tab("CustomerOrders", activated: 1000),                       // name hit, cold
            };
            var ranked = QuickSwitchRanker.Rank(rows, new[] { "customer" });
            Names(ranked).Should().ContainInOrder("CustomerOrders", "misc scratch");
        }

        [Fact]
        public void Rank_EqualScore_TiebreaksByOpenThenRecency()
        {
            var rows = new List<TabSummary>
            {
                Tab("orders a", activated: 3000),
                Tab("orders b", open: true, activated: 1000),
                Tab("orders c", activated: 2000),
            };
            var ranked = QuickSwitchRanker.Rank(rows, new[] { "orders" });
            Names(ranked).Should().ContainInOrder("orders b", "orders a", "orders c");
        }

        [Fact]
        public void Rank_NoTerms_DegradesToMruOrder()
        {
            var rows = new List<TabSummary>
            {
                Tab("old", activated: 1000),
                Tab("new", activated: 3000),
                Tab("mid", activated: 2000),
            };
            var ranked = QuickSwitchRanker.Rank(rows, new string[0]);
            // all scores 0, all closed → EffectiveActivatedTs decides
            Names(ranked).Should().ContainInOrder("new", "mid", "old");
        }

        [Fact]
        public void EffectiveActivatedTs_FallsBackToSnapshotTime_ForLegacyRows()
        {
            Tab("legacy", activated: 0, ts: 5000).EffectiveActivatedTs.Should().Be(5000);
            Tab("touched", activated: 9000, ts: 5000).EffectiveActivatedTs.Should().Be(9000);
        }

        // ---- highlight runs ----

        [Fact]
        public void MatchRuns_SubstringHit_IsOneRun()
        {
            var runs = QuickSwitchRanker.MatchRuns("CustomerOrders", new[] { "omer" });
            runs.Should().HaveCount(1);
            runs[0].Key.Should().Be(4);
            runs[0].Value.Should().Be(4);
        }

        [Fact]
        public void MatchRuns_FuzzyHit_MergesAdjacentChars()
        {
            // Greedy left-to-right: c,u,s at 0..2 (one merged run), then o@4, r@7, d@10.
            var runs = QuickSwitchRanker.MatchRuns("CustomerOrders", new[] { "cusord" });
            runs.Should().HaveCount(4);
            runs[0].Should().Be(new KeyValuePair<int, int>(0, 3));
        }

        [Fact]
        public void MatchRuns_OverlappingTerms_AreMerged()
        {
            var runs = QuickSwitchRanker.MatchRuns("CustomerOrders", new[] { "custom", "tomer" });
            runs.Should().HaveCount(1);
            runs[0].Should().Be(new KeyValuePair<int, int>(0, 8)); // "Customer"
        }

        [Fact]
        public void MatchRuns_NoHit_IsEmpty()
        {
            QuickSwitchRanker.MatchRuns("abc", new[] { "xyz" }).Should().BeEmpty();
            QuickSwitchRanker.MatchRuns("", new[] { "x" }).Should().BeEmpty();
            QuickSwitchRanker.MatchRuns("abc", null).Should().BeEmpty();
        }
    }
}
