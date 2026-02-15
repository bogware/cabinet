using Cabinet.Query;
using Cabinet.Query.Bql;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Query;

public class BqlLexerTests
{
    [Fact]
    public void Tokenize_SimpleSearch_ProducesIdentifierAndEof()
    {
        var lexer = new BqlLexer("find errors");
        var tokens = lexer.Tokenize();

        tokens.Should().HaveCount(3); // find, errors, EOF
        tokens[0].Kind.Should().Be(BqlTokenKind.Identifier);
        tokens[0].Value.Should().Be("find");
        tokens[1].Kind.Should().Be(BqlTokenKind.Identifier);
        tokens[1].Value.Should().Be("errors");
        tokens[2].Kind.Should().Be(BqlTokenKind.Eof);
    }

    [Fact]
    public void Tokenize_StringLiteral_HandlesDoubleQuotes()
    {
        var lexer = new BqlLexer("find \"connection timeout\"");
        var tokens = lexer.Tokenize();

        tokens.Should().HaveCount(3);
        tokens[1].Kind.Should().Be(BqlTokenKind.StringLiteral);
        tokens[1].Value.Should().Be("connection timeout");
    }

    [Fact]
    public void Tokenize_StringLiteral_HandlesSingleQuotes()
    {
        var lexer = new BqlLexer("find 'error message'");
        var tokens = lexer.Tokenize();

        tokens[1].Kind.Should().Be(BqlTokenKind.StringLiteral);
        tokens[1].Value.Should().Be("error message");
    }

    [Fact]
    public void Tokenize_StringLiteral_HandlesEscapes()
    {
        var lexer = new BqlLexer("find \"say \\\"hello\\\"\"");
        var tokens = lexer.Tokenize();

        tokens[1].Kind.Should().Be(BqlTokenKind.StringLiteral);
        tokens[1].Value.Should().Be("say \"hello\"");
    }

    [Fact]
    public void Tokenize_Numbers_ParsesIntegersAndDecimals()
    {
        var lexer = new BqlLexer("42 3.14");
        var tokens = lexer.Tokenize();

        tokens[0].Kind.Should().Be(BqlTokenKind.NumberLiteral);
        tokens[0].Value.Should().Be("42");
        tokens[1].Kind.Should().Be(BqlTokenKind.NumberLiteral);
        tokens[1].Value.Should().Be("3.14");
    }

    [Fact]
    public void Tokenize_TimeSpan_ParsesTimeUnits()
    {
        var lexer = new BqlLexer("24h 7d 30m 60s");
        var tokens = lexer.Tokenize();

        tokens[0].Kind.Should().Be(BqlTokenKind.TimeSpanLiteral);
        tokens[0].Value.Should().Be("24h");
        tokens[1].Kind.Should().Be(BqlTokenKind.TimeSpanLiteral);
        tokens[1].Value.Should().Be("7d");
        tokens[2].Kind.Should().Be(BqlTokenKind.TimeSpanLiteral);
        tokens[2].Value.Should().Be("30m");
        tokens[3].Kind.Should().Be(BqlTokenKind.TimeSpanLiteral);
        tokens[3].Value.Should().Be("60s");
    }

    [Fact]
    public void Tokenize_Operators_ParsesAllComparisonOperators()
    {
        var lexer = new BqlLexer("= != > >= < <=");
        var tokens = lexer.Tokenize();

        tokens[0].Kind.Should().Be(BqlTokenKind.Equals);
        tokens[1].Kind.Should().Be(BqlTokenKind.NotEquals);
        tokens[2].Kind.Should().Be(BqlTokenKind.Greater);
        tokens[3].Kind.Should().Be(BqlTokenKind.GreaterEquals);
        tokens[4].Kind.Should().Be(BqlTokenKind.Less);
        tokens[5].Kind.Should().Be(BqlTokenKind.LessEquals);
    }

    [Fact]
    public void Tokenize_Pipeline_HandlesPipeOperator()
    {
        var lexer = new BqlLexer("find errors | where severity >= 3 | head 10");
        var tokens = lexer.Tokenize();

        tokens.Should().Contain(t => t.Kind == BqlTokenKind.Pipe);
        tokens.Count(t => t.Kind == BqlTokenKind.Pipe).Should().Be(2);
    }

    [Fact]
    public void Tokenize_Arithmetic_ParsesArithmeticOperators()
    {
        var lexer = new BqlLexer("a + b - c * d / e");
        var tokens = lexer.Tokenize();

        tokens[1].Kind.Should().Be(BqlTokenKind.Plus);
        tokens[3].Kind.Should().Be(BqlTokenKind.Minus);
        tokens[5].Kind.Should().Be(BqlTokenKind.Star);
        tokens[7].Kind.Should().Be(BqlTokenKind.Slash);
    }

    [Fact]
    public void Tokenize_Parentheses_ParsesGrouping()
    {
        var lexer = new BqlLexer("count(field)");
        var tokens = lexer.Tokenize();

        tokens[0].Kind.Should().Be(BqlTokenKind.Identifier);
        tokens[0].Value.Should().Be("count");
        tokens[1].Kind.Should().Be(BqlTokenKind.OpenParen);
        tokens[2].Kind.Should().Be(BqlTokenKind.Identifier);
        tokens[3].Kind.Should().Be(BqlTokenKind.CloseParen);
    }

    [Fact]
    public void Tokenize_UnterminatedString_ThrowsParseException()
    {
        var lexer = new BqlLexer("find \"unterminated");
        var act = () => lexer.Tokenize();
        act.Should().Throw<QueryParseException>().WithMessage("*Unterminated string*");
    }

    [Fact]
    public void Tokenize_UnexpectedCharacter_ThrowsParseException()
    {
        var lexer = new BqlLexer("find @invalid");
        var act = () => lexer.Tokenize();
        act.Should().Throw<QueryParseException>().WithMessage("*Unexpected character*");
    }

    [Fact]
    public void Token_IsKeyword_IsCaseInsensitive()
    {
        var token = new BqlToken(BqlTokenKind.Identifier, "FIND", 0);
        token.IsKeyword("find").Should().BeTrue();
        token.IsKeyword("FIND").Should().BeTrue();
        token.IsKeyword("Find").Should().BeTrue();
    }

    [Fact]
    public void Token_IsAnyKeyword_MatchesMultiple()
    {
        var token = new BqlToken(BqlTokenKind.Identifier, "search", 0);
        token.IsAnyKeyword("find", "search").Should().BeTrue();
        token.IsAnyKeyword("where", "stats").Should().BeFalse();
    }
}
