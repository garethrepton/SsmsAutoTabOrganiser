using FluentAssertions;
using AutoTabOrganiser.Util;
using Xunit;

namespace AutoTabOrganiser.Tests
{
    public class PathSanitiserTests
    {
        [Fact]
        public void Sanitise_StripsTrailingSemicolonAndUnsafeChars()
        {
            PathSanitiser.Sanitise("SELECT * FROM dbo.X;").Should().Be("SELECT _ FROM dbo.X");
        }

        [Fact]
        public void Sanitise_TruncatesTo60Chars()
        {
            var s = new string('a', 200);
            PathSanitiser.Sanitise(s).Length.Should().BeLessThanOrEqualTo(60);
        }

        [Fact]
        public void FromFirstLine_SkipsBlankAndComment()
        {
            var text = "\n-- @folder: x\n-- comment\n   \nSELECT 1;\n";
            PathSanitiser.FromFirstLine(text).Should().Be("SELECT 1");
        }

        [Fact]
        public void FromFirstLine_OnlyComments_ReturnsNull()
        {
            PathSanitiser.FromFirstLine("-- only comments\n-- here\n").Should().BeNull();
        }
    }
}
