using FluentAssertions;
using AutoTabOrganiser.Util;
using Xunit;

namespace AutoTabOrganiser.Tests
{
    public class HashingTests
    {
        [Fact]
        public void Sha256_IsStableForSameInput()
        {
            var a = Hashing.Sha256Hex("SELECT 1");
            var b = Hashing.Sha256Hex("SELECT 1");
            a.Should().Be(b);
            a.Length.Should().Be(64);
        }

        [Fact]
        public void Sha256_DiffersForDifferentInput()
        {
            Hashing.Sha256Hex("a").Should().NotBe(Hashing.Sha256Hex("b"));
        }

        [Fact]
        public void Fingerprint_IgnoresLeadingComments()
        {
            var fp1 = Hashing.Fingerprint("-- @folder: x\nSELECT 1\n");
            var fp2 = Hashing.Fingerprint("-- different comments\n-- @id: abc\nSELECT 1\n");
            fp1.Should().Be(fp2);
        }

        [Fact]
        public void Fingerprint_CollapsesWhitespace()
        {
            var fp1 = Hashing.Fingerprint("SELECT      1");
            var fp2 = Hashing.Fingerprint("SELECT 1");
            fp1.Should().Be(fp2);
        }
    }
}
