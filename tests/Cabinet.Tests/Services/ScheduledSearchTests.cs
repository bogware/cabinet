using Cabinet.API.Services;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Services;

public class ScheduledSearchTests
{
    [Fact]
    public void MatchesCron_EveryMinute_AlwaysMatches()
    {
        ScheduledSearchService.MatchesCron("* * * * *", new DateTime(2024, 1, 15, 10, 30, 0))
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesCron_SpecificMinute_MatchesExactly()
    {
        ScheduledSearchService.MatchesCron("30 * * * *", new DateTime(2024, 1, 15, 10, 30, 0))
            .Should().BeTrue();
        ScheduledSearchService.MatchesCron("30 * * * *", new DateTime(2024, 1, 15, 10, 31, 0))
            .Should().BeFalse();
    }

    [Fact]
    public void MatchesCron_Every5Minutes_MatchesStep()
    {
        ScheduledSearchService.MatchesCron("*/5 * * * *", new DateTime(2024, 1, 15, 10, 0, 0))
            .Should().BeTrue();
        ScheduledSearchService.MatchesCron("*/5 * * * *", new DateTime(2024, 1, 15, 10, 5, 0))
            .Should().BeTrue();
        ScheduledSearchService.MatchesCron("*/5 * * * *", new DateTime(2024, 1, 15, 10, 3, 0))
            .Should().BeFalse();
    }

    [Fact]
    public void MatchesCron_Range_MatchesWithinRange()
    {
        ScheduledSearchService.MatchesCron("0 9-17 * * *", new DateTime(2024, 1, 15, 12, 0, 0))
            .Should().BeTrue();
        ScheduledSearchService.MatchesCron("0 9-17 * * *", new DateTime(2024, 1, 15, 20, 0, 0))
            .Should().BeFalse();
    }

    [Fact]
    public void MatchesCron_List_MatchesAnyInList()
    {
        ScheduledSearchService.MatchesCron("0,15,30,45 * * * *", new DateTime(2024, 1, 15, 10, 15, 0))
            .Should().BeTrue();
        ScheduledSearchService.MatchesCron("0,15,30,45 * * * *", new DateTime(2024, 1, 15, 10, 20, 0))
            .Should().BeFalse();
    }

    [Fact]
    public void MatchesCron_InvalidCron_ReturnsFalse()
    {
        ScheduledSearchService.MatchesCron("bad", new DateTime(2024, 1, 15, 10, 0, 0))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("count > 5", 5, 10, true)]
    [InlineData("count > 5", 5, 5, false)]
    [InlineData("count >= 5", 5, 5, true)]
    [InlineData("count < 5", 5, 3, true)]
    [InlineData("count <= 5", 5, 5, true)]
    [InlineData("count == 5", 5, 5, true)]
    [InlineData("count == 5", 5, 3, false)]
    [InlineData("count != 0", 0, 1, true)]
    [InlineData("count != 0", 0, 0, false)]
    [InlineData("always", 0, 0, false)]
    [InlineData("always", 0, 1, true)]
    public void EvaluateCondition_HandlesAllOperators(string condition, int threshold, int resultCount, bool expected)
    {
        ScheduledSearchService.EvaluateCondition(condition, threshold, resultCount)
            .Should().Be(expected);
    }

    [Fact]
    public void MatchesCron_DayOfWeekAndDayOfMonth_OrSemantics()
    {
        // POSIX: when both dom and dow are restricted, uses OR semantics
        // "0 0 15 * 1" means: at midnight on the 15th OR on Mondays
        // January 15, 2024 is a Monday (dow=1), so both match
        ScheduledSearchService.MatchesCron("0 0 15 * 1", new DateTime(2024, 1, 15, 0, 0, 0))
            .Should().BeTrue();

        // January 22, 2024 is a Monday (dow=1), dom=22 doesn't match but dow matches → true
        ScheduledSearchService.MatchesCron("0 0 15 * 1", new DateTime(2024, 1, 22, 0, 0, 0))
            .Should().BeTrue();

        // January 16, 2024 is a Tuesday (dow=2), dom=16 doesn't match, dow=2 doesn't match → false
        ScheduledSearchService.MatchesCron("0 0 15 * 1", new DateTime(2024, 1, 16, 0, 0, 0))
            .Should().BeFalse();
    }

    [Fact]
    public void MatchesCron_StepWithRange_MatchesCorrectly()
    {
        // 10-20/3 should match 10, 13, 16, 19
        ScheduledSearchService.MatchesCron("10-20/3 * * * *", new DateTime(2024, 1, 15, 10, 10, 0))
            .Should().BeTrue();
        ScheduledSearchService.MatchesCron("10-20/3 * * * *", new DateTime(2024, 1, 15, 10, 13, 0))
            .Should().BeTrue();
        ScheduledSearchService.MatchesCron("10-20/3 * * * *", new DateTime(2024, 1, 15, 10, 11, 0))
            .Should().BeFalse();
    }
}
