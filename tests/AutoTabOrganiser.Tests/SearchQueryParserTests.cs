using FluentAssertions;
using AutoTabOrganiser.Tree;
using Xunit;

namespace AutoTabOrganiser.Tests
{
    public class SearchQueryParserTests
    {
        [Fact]
        public void Parses_HashTag()
        {
            var q = SearchQueryParser.Parse("#prod");
            q.Terms.Should().HaveCount(1);
            q.Terms[0].Field.Should().Be("tag");
            q.Terms[0].Value.Should().Be("prod");
            q.Terms[0].Negate.Should().BeFalse();
        }

        [Fact]
        public void Parses_NegatedHashTag()
        {
            var q = SearchQueryParser.Parse("-#archive");
            q.Terms[0].Negate.Should().BeTrue();
            q.Terms[0].Value.Should().Be("archive");
        }

        [Fact]
        public void Parses_FieldQualifiedTokens()
        {
            var q = SearchQueryParser.Parse("name:slow folder:prod since:7d");
            q.Terms.Should().HaveCount(3);
            q.Terms[0].Field.Should().Be("name");
            q.Terms[1].Field.Should().Be("folder");
            q.Terms[2].Field.Should().Be("since");
            q.Terms[2].Value.Should().Be("7d");
        }

        [Fact]
        public void Bare_TokenHasNullField()
        {
            var q = SearchQueryParser.Parse("foo");
            q.Terms.Should().HaveCount(1);
            q.Terms[0].Field.Should().BeNull();
        }

        [Fact]
        public void ToSql_TagIsPrefixMatch()
        {
            var q = SearchQueryParser.Parse("#pro");
            var (where, pars) = SearchQueryParser.ToSql(q, 0);
            where.Should().Contain("st.tag LIKE");
            pars.Should().ContainSingle(p => (string)p.Value == "pro%");
        }

        [Fact]
        public void ToSql_CombinesAnd()
        {
            var q = SearchQueryParser.Parse("#prod name:slow");
            var (where, pars) = SearchQueryParser.ToSql(q, 0);
            where.Should().Contain("AND");
            pars.Count.Should().BeGreaterThan(0);
        }

        [Fact]
        public void Quoted_TokenIsSingleToken()
        {
            var q = SearchQueryParser.Parse("\"hello world\"");
            q.Terms.Should().HaveCount(1);
            q.Terms[0].Value.Should().Be("hello world");
        }

        [Fact]
        public void Parses_ContentField()
        {
            var q = SearchQueryParser.Parse("content:select");
            q.Terms.Should().HaveCount(1);
            q.Terms[0].Field.Should().Be("content");
            q.Terms[0].Value.Should().Be("select");

            var (where, pars) = SearchQueryParser.ToSql(q, 0);
            where.Should().Contain("tab_content_fts");
            where.Should().Contain("MATCH");
            // FTS5 prefix query: typing "select" matches tokens starting with "select".
            pars.Should().ContainSingle(p => p.Value as string == "select*");
        }
    }
}
