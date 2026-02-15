using Cabinet.Query.NaturalLanguage;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Query;

public class NaturalLanguageTranslatorTests
{
    private readonly RuleBasedTranslator _translator = new();

    // ─── IsNaturalLanguage detection ─────────────────────────

    [Theory]
    [InlineData("show me errors from the last 24 hours", true)]
    [InlineData("how many errors in the last week", true)]
    [InlineData("list all warnings sorted by time", true)]
    [InlineData("find errors", false)] // BQL keyword
    [InlineData("search error | head 10", false)] // SPL with pipe
    [InlineData("* | stats count", false)] // SPL wildcard
    [InlineData("error", false)] // Single word, ambiguous → treated as BQL
    public void IsNaturalLanguage_DetectsCorrectly(string input, bool expected)
    {
        _translator.IsNaturalLanguage(input).Should().Be(expected);
    }

    // ─── Translation rules ───────────────────────────────────

    [Fact]
    public async Task TranslateAsync_ShowMeErrors_TranslatesToBql()
    {
        var result = await _translator.TranslateAsync("show me errors from the last 24h");

        result.Success.Should().BeTrue();
        result.BqlQuery.Should().NotBeNullOrEmpty();
        result.Confidence.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TranslateAsync_HowManyErrors_TranslatesWithStatsCount()
    {
        var result = await _translator.TranslateAsync("how many errors");

        result.Success.Should().BeTrue();
        result.BqlQuery.Should().Contain("stats count");
    }

    [Fact]
    public async Task TranslateAsync_ErrorsGroupedByHost_TranslatesWithGroupBy()
    {
        var result = await _translator.TranslateAsync("errors grouped by host");

        result.Success.Should().BeTrue();
        result.BqlQuery.Should().Contain("group by host");
    }

    [Fact]
    public async Task TranslateAsync_TopErrors_TranslatesWithTop()
    {
        var result = await _translator.TranslateAsync("top 10 errors by host");

        result.Success.Should().BeTrue();
        result.BqlQuery.Should().Contain("top 10");
    }

    [Fact]
    public async Task TranslateAsync_LatestErrors_TranslatesWithSortAndLimit()
    {
        var result = await _translator.TranslateAsync("latest 5 errors");

        result.Success.Should().BeTrue();
        result.BqlQuery.Should().Contain("limit 5");
    }

    [Fact]
    public async Task TranslateAsync_ShowMeEverything_TranslatesToWildcard()
    {
        var result = await _translator.TranslateAsync("show me everything from the last 24h");

        result.Success.Should().BeTrue();
        result.BqlQuery.Should().Contain("find *");
        result.BqlQuery.Should().Contain("last 24h");
    }

    [Fact]
    public async Task TranslateAsync_EmptyQuery_ReturnsFailed()
    {
        var result = await _translator.TranslateAsync("");

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task TranslateAsync_FallbackExtractsTerms()
    {
        var result = await _translator.TranslateAsync("please show me all the database connection issues");

        result.Success.Should().BeTrue();
        result.BqlQuery.Should().Contain("find");
        result.Confidence.Should().BeLessThan(0.5); // Low confidence for fallback
    }

    // ─── NaturalLanguageTranslation model ────────────────────

    [Fact]
    public void Failed_CreatesFailedResult()
    {
        var result = NaturalLanguageTranslation.Failed("test query", "test reason");

        result.Success.Should().BeFalse();
        result.OriginalQuery.Should().Be("test query");
        result.Explanation.Should().Be("test reason");
    }
}
