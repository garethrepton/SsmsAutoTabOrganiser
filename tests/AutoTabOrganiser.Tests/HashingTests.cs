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
    }
}
