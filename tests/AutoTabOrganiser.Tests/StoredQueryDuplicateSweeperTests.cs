using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutoTabOrganiser.Metadata;
using AutoTabOrganiser.Storage;
using AutoTabOrganiser.Util;
using FluentAssertions;
using Xunit;

namespace AutoTabOrganiser.Tests
{
    public class StoredQueryDuplicateSweeperTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly Logger _log;

        public StoredQueryDuplicateSweeperTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "ato-sweeper-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _log = new Logger(_tempDir, null);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }

        private string WriteFile(string name, string content)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void Sweep_TwoIdenticalFiles_DeletesLoserKeepsWinner()
        {
            var body = "SELECT 1;\n";
            var aPath = WriteFile("a.sql", MetadataWriter.SetId(body, "tab-a"));
            var bPath = WriteFile("b.sql", MetadataWriter.SetId(body, "tab-b"));

            // Make 'a' newer.
            File.SetLastWriteTimeUtc(bPath, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(aPath, DateTime.UtcNow);

            var sweeper = new StoredQueryDuplicateSweeper(_log);
            var result = sweeper.Sweep(
                new[]
                {
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "tab-a", FilePath = aPath },
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "tab-b", FilePath = bPath },
                },
                isTabOpen: _ => false);

            result.GroupsConsidered.Should().Be(1);
            result.DuplicatesDeleted.Should().Be(1);
            File.Exists(aPath).Should().BeTrue("'a' is newer so it wins");
            File.Exists(bPath).Should().BeFalse();
            result.DeletedPaths.Should().BeEquivalentTo(new[] { bPath });
        }

        [Fact]
        public void Sweep_OpenTabAlwaysWins_EvenIfOlder()
        {
            var body = "SELECT 1;\n";
            var openerPath = WriteFile("opener.sql", MetadataWriter.SetId(body, "tab-open"));
            var newerPath  = WriteFile("newer.sql",  MetadataWriter.SetId(body, "tab-cold"));

            // The "open" file is older.
            File.SetLastWriteTimeUtc(openerPath, DateTime.UtcNow.AddMinutes(-30));
            File.SetLastWriteTimeUtc(newerPath,  DateTime.UtcNow);

            var sweeper = new StoredQueryDuplicateSweeper(_log);
            var result = sweeper.Sweep(
                new[]
                {
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "tab-open", FilePath = openerPath },
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "tab-cold", FilePath = newerPath },
                },
                isTabOpen: tabId => tabId == "tab-open");

            result.DuplicatesDeleted.Should().Be(1);
            File.Exists(openerPath).Should().BeTrue("the open tab's file must be preserved even though it's older");
            File.Exists(newerPath).Should().BeFalse();
        }

        [Fact]
        public void Sweep_LegacyLeadingIdAndNewTrailingId_AreEqualAfterCanonicalisation()
        {
            // Two files with the same SQL — one written by the OLD writer (id at top),
            // one written by the NEW writer (id at bottom). They should sweep as duplicates.
            var legacy = "-- @id: legacy-id\nSELECT 1;\n";
            var modern = MetadataWriter.SetId("SELECT 1;\n", "modern-id");

            var legacyPath = WriteFile("legacy.sql", legacy);
            var modernPath = WriteFile("modern.sql", modern);

            File.SetLastWriteTimeUtc(legacyPath, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(modernPath, DateTime.UtcNow);

            var sweeper = new StoredQueryDuplicateSweeper(_log);
            var result = sweeper.Sweep(
                new[]
                {
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "legacy-id", FilePath = legacyPath },
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "modern-id", FilePath = modernPath },
                },
                isTabOpen: _ => false);

            result.DuplicatesDeleted.Should().Be(1, "even though the @id placement differs, the SQL is identical");
            File.Exists(modernPath).Should().BeTrue("newer file wins");
            File.Exists(legacyPath).Should().BeFalse();
        }

        [Fact]
        public void Sweep_DistinctContent_LeavesAllFilesAlone()
        {
            var aPath = WriteFile("a.sql", MetadataWriter.SetId("SELECT 1;\n", "a"));
            var bPath = WriteFile("b.sql", MetadataWriter.SetId("SELECT 2;\n", "b"));

            var sweeper = new StoredQueryDuplicateSweeper(_log);
            var result = sweeper.Sweep(
                new[]
                {
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "a", FilePath = aPath },
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "b", FilePath = bPath },
                },
                isTabOpen: _ => false);

            result.GroupsConsidered.Should().Be(0);
            result.DuplicatesDeleted.Should().Be(0);
            File.Exists(aPath).Should().BeTrue();
            File.Exists(bPath).Should().BeTrue();
        }

        [Fact]
        public void Sweep_MissingFile_IsSkippedWithoutThrowing()
        {
            var realPath = WriteFile("real.sql", MetadataWriter.SetId("SELECT 1;\n", "a"));
            var ghostPath = Path.Combine(_tempDir, "does-not-exist.sql");

            var sweeper = new StoredQueryDuplicateSweeper(_log);
            var result = sweeper.Sweep(
                new[]
                {
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "a", FilePath = realPath },
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "b", FilePath = ghostPath },
                },
                isTabOpen: _ => false);

            result.DuplicatesDeleted.Should().Be(0);
            File.Exists(realPath).Should().BeTrue();
        }

        [Fact]
        public void Sweep_TwoTabsSharingOneFile_NeverDeletesTheSharedFile()
        {
            // Two TabIds pointing at the SAME on-disk path is a supported state (rows are
            // keyed by TabId, not path). The file must never be treated as its own duplicate.
            var path = WriteFile("shared.sql", MetadataWriter.SetId("SELECT 1;\n", "tab-a"));

            var sweeper = new StoredQueryDuplicateSweeper(_log);
            var result = sweeper.Sweep(
                new[]
                {
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "tab-a", FilePath = path },
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "tab-b", FilePath = path },
                },
                isTabOpen: _ => false);

            result.DuplicatesDeleted.Should().Be(0);
            File.Exists(path).Should().BeTrue("a file shared by two tabs is not a duplicate of itself");
        }

        [Fact]
        public void Sweep_SamePathDifferentCasing_TreatedAsOneFile()
        {
            var path = WriteFile("cased.sql", MetadataWriter.SetId("SELECT 1;\n", "tab-a"));

            var sweeper = new StoredQueryDuplicateSweeper(_log);
            var result = sweeper.Sweep(
                new[]
                {
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "tab-a", FilePath = path },
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "tab-b", FilePath = path.ToUpperInvariant() },
                },
                isTabOpen: _ => false);

            result.DuplicatesDeleted.Should().Be(0);
            File.Exists(path).Should().BeTrue();
        }

        [Fact]
        public void Sweep_FileOutsideRequiredRoot_IsNeverDeleted()
        {
            // Genuine duplicates, but one lives outside the stored-queries root — the
            // boundary must exclude it from the sweep entirely (neither deleted nor
            // counted as a winner that justifies deleting something else).
            var insideDir = Path.Combine(_tempDir, "scripts");
            Directory.CreateDirectory(insideDir);
            var outsideDir = Path.Combine(_tempDir, "elsewhere");
            Directory.CreateDirectory(outsideDir);

            var body = "SELECT 42;\n";
            var insidePath = Path.Combine(insideDir, "in.sql");
            File.WriteAllText(insidePath, MetadataWriter.SetId(body, "tab-in"));
            var outsidePath = Path.Combine(outsideDir, "out.sql");
            File.WriteAllText(outsidePath, MetadataWriter.SetId(body, "tab-out"));

            var sweeper = new StoredQueryDuplicateSweeper(_log);
            var result = sweeper.Sweep(
                new[]
                {
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "tab-in",  FilePath = insidePath },
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "tab-out", FilePath = outsidePath },
                },
                isTabOpen: _ => false,
                requiredRoot: insideDir);

            result.DuplicatesDeleted.Should().Be(0, "the outside file is excluded, leaving a singleton group");
            File.Exists(insidePath).Should().BeTrue();
            File.Exists(outsidePath).Should().BeTrue("files outside the root are untouchable");
        }

        [Fact]
        public void Sweep_DuplicatesInsideRoot_StillSweptWhenRootSupplied()
        {
            var body = "SELECT 7;\n";
            var aPath = WriteFile("a.sql", MetadataWriter.SetId(body, "a"));
            var bPath = WriteFile("b.sql", MetadataWriter.SetId(body, "b"));
            File.SetLastWriteTimeUtc(aPath, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(bPath, DateTime.UtcNow);

            var sweeper = new StoredQueryDuplicateSweeper(_log);
            var result = sweeper.Sweep(
                new[]
                {
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "a", FilePath = aPath },
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "b", FilePath = bPath },
                },
                isTabOpen: _ => false,
                requiredRoot: _tempDir);

            result.DuplicatesDeleted.Should().Be(1);
            File.Exists(bPath).Should().BeTrue("newer file wins");
            File.Exists(aPath).Should().BeFalse();
        }

        [Fact]
        public void Sweep_ThreeWayDuplicate_DeletesTwoLosers()
        {
            var body = "SELECT 99;\n";
            var aPath = WriteFile("a.sql", MetadataWriter.SetId(body, "a"));
            var bPath = WriteFile("b.sql", MetadataWriter.SetId(body, "b"));
            var cPath = WriteFile("c.sql", MetadataWriter.SetId(body, "c"));

            File.SetLastWriteTimeUtc(aPath, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(bPath, DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(cPath, DateTime.UtcNow);

            var sweeper = new StoredQueryDuplicateSweeper(_log);
            var result = sweeper.Sweep(
                new[]
                {
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "a", FilePath = aPath },
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "b", FilePath = bPath },
                    new StoredQueryDuplicateSweeper.Candidate { TabId = "c", FilePath = cPath },
                },
                isTabOpen: _ => false);

            result.GroupsConsidered.Should().Be(1);
            result.DuplicatesDeleted.Should().Be(2);
            File.Exists(cPath).Should().BeTrue("'c' is newest so it wins");
            File.Exists(aPath).Should().BeFalse();
            File.Exists(bPath).Should().BeFalse();
        }
    }
}
