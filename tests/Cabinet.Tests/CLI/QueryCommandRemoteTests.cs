using Cabinet.CLI.Commands;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.CLI;

public sealed class QueryCommandRemoteTests
{
    [Fact]
    public void ServerResults_BecomeRows_WithTheirColumnsAndStatistics()
    {
        const string body = """
            {"query":"find * | stats count by host","results":[{"host":"web","count":2},{"host":"db","count":1.5,"note":null}],
             "count":2,"executionTimeMs":12,"eventsScanned":3,"truncated":true}
            """;

        var result = QueryCommand.ParseRemoteResult(body);

        result.Columns.Should().Equal("host", "count", "note");
        var first = (IDictionary<string, object?>)result.Events[0];
        first["host"].Should().Be("web");
        first["count"].Should().Be(2L);
        ((IDictionary<string, object?>)result.Events[1])["count"].Should().Be(1.5);
        result.Statistics!.EventsScanned.Should().Be(3);
        result.Statistics.Truncated.Should().BeTrue();
        result.Statistics.ExecutionTime.Should().Be(TimeSpan.FromMilliseconds(12));
    }
}
