using Cabinet.Query;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

public class QueryParserTests
{
    private readonly QueryParser _parser = new(NullLogger<QueryParser>.Instance);

    [Fact]
    public void Parse_SimpleSearch_CreatesSearchOperator()
    {
        // Arrange
        var query = "error";

        // Act
        var plan = _parser.Parse(query);

        // Assert
        plan.Operators.Should().HaveCount(1);
        plan.Operators[0].Should().BeOfType<SearchOperator>();
        plan.Operators[0].Name.Should().Be("search");
    }

    [Fact]
    public void Parse_PipelinedQuery_CreatesMultipleOperators()
    {
        // Arrange
        var query = "error | where severity >= 3 | head 10";

        // Act
        var plan = _parser.Parse(query);

        // Assert
        plan.Operators.Should().HaveCount(3);
        plan.Operators[0].Should().BeOfType<SearchOperator>();
        plan.Operators[1].Should().BeOfType<WhereOperator>();
        plan.Operators[2].Should().BeOfType<HeadOperator>();
    }

    [Fact]
    public void Parse_StatsQuery_CreatesStatsOperator()
    {
        // Arrange
        var query = "* | stats count by source";

        // Act
        var plan = _parser.Parse(query);

        // Assert
        plan.Operators.Should().HaveCount(2);
        plan.Operators[0].Should().BeOfType<SearchOperator>();
        plan.Operators[1].Should().BeOfType<StatsOperator>();
    }

    [Fact]
    public void Parse_SortQuery_CreatesSortOperator()
    {
        // Arrange
        var query = "* | sort -timestamp";

        // Act
        var plan = _parser.Parse(query);

        // Assert
        plan.Operators.Should().HaveCount(2);
        plan.Operators[1].Should().BeOfType<SortOperator>();
    }

    [Fact]
    public void Parse_EmptyQuery_ThrowsException()
    {
        // Arrange
        var query = "";

        // Act
        Action act = () => _parser.Parse(query);

        // Assert
        act.Should().Throw<QueryParseException>()
            .WithMessage("Query cannot be empty");
    }

    [Fact]
    public void Parse_UnknownOperator_ThrowsException()
    {
        // Arrange
        var query = "* | unknownop";

        // Act
        Action act = () => _parser.Parse(query);

        // Assert
        act.Should().Throw<QueryParseException>()
            .WithMessage("Unknown operator: unknownop");
    }

    [Fact]
    public void Parse_QuotedStrings_HandlesCorrectly()
    {
        // Arrange
        var query = "\"error message\" | where field=\"value with spaces\"";

        // Act
        var plan = _parser.Parse(query);

        // Assert
        plan.Operators.Should().HaveCount(2);
        plan.Operators[0].Should().BeOfType<SearchOperator>();
        plan.Operators[1].Should().BeOfType<WhereOperator>();
    }

    [Theory]
    [InlineData("head 20", typeof(HeadOperator))]
    [InlineData("tail 30", typeof(TailOperator))]
    [InlineData("top 10 field", typeof(TopOperator))]
    [InlineData("dedup steamid", typeof(DedupOperator))]
    [InlineData("fields name,id,time", typeof(FieldsOperator))]
    [InlineData("timechart span=1h", typeof(TimechartOperator))]
    public void Parse_VariousOperators_CreatesCorrectTypes(string operatorQuery, Type expectedType)
    {
        // Arrange
        var query = $"* | {operatorQuery}";

        // Act
        var plan = _parser.Parse(query);

        // Assert
        plan.Operators.Should().HaveCount(2);
        plan.Operators[1].Should().BeOfType(expectedType);
    }

    [Fact]
    public async Task WhereOperator_FiltersCorrectly()
    {
        // Arrange
        var whereOp = new WhereOperator("severity=error");
        var input = new List<dynamic>
        {
            CreateEvent("error", "Error message"),
            CreateEvent("info", "Info message"),
            CreateEvent("error", "Another error")
        };

        // Act
        var result = (await whereOp.ExecuteAsync(input)).ToList();

        // Assert
        result.Should().HaveCount(2);
        result.All(e => ((IDictionary<string, object?>)e)["severity"]?.ToString() == "error").Should().BeTrue();
    }

    [Fact]
    public async Task StatsOperator_CountsByField()
    {
        // Arrange
        var statsOp = new StatsOperator("count by severity");
        var input = new List<dynamic>
        {
            CreateEvent("error", "msg1"),
            CreateEvent("error", "msg2"),
            CreateEvent("info", "msg3"),
            CreateEvent("error", "msg4")
        };

        // Act
        var result = (await statsOp.ExecuteAsync(input)).ToList();

        // Assert
        result.Should().HaveCount(2);
        var errorCount = result.First(r => ((IDictionary<string, object?>)r)["severity"]?.ToString() == "error");
        ((IDictionary<string, object?>)errorCount)["count"].Should().Be(3);
    }

    [Fact]
    public async Task HeadOperator_LimitsResults()
    {
        // Arrange
        var headOp = new HeadOperator(2);
        var input = Enumerable.Range(1, 10).Select(i => CreateEvent("info", $"msg{i}"));

        // Act
        var result = (await headOp.ExecuteAsync(input)).ToList();

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task SortOperator_SortsAscending()
    {
        // Arrange
        var sortOp = new SortOperator("value");
        var input = new List<dynamic>
        {
            CreateEventWithValue(3),
            CreateEventWithValue(1),
            CreateEventWithValue(2)
        };

        // Act
        var result = (await sortOp.ExecuteAsync(input)).ToList();

        // Assert
        var values = result.Select(r => ((IDictionary<string, object?>)r)["value"]).ToList();
        values.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task SortOperator_SortsDescending()
    {
        // Arrange
        var sortOp = new SortOperator("-value");
        var input = new List<dynamic>
        {
            CreateEventWithValue(1),
            CreateEventWithValue(3),
            CreateEventWithValue(2)
        };

        // Act
        var result = (await sortOp.ExecuteAsync(input)).ToList();

        // Assert
        var values = result.Select(r => ((IDictionary<string, object?>)r)["value"]).ToList();
        values.Should().BeInDescendingOrder();
    }

    private dynamic CreateEvent(string severity, string message)
    {
        dynamic evt = new System.Dynamic.ExpandoObject();
        evt.severity = severity;
        evt.message = message;
        evt.RawLine = message;
        evt.Timestamp = DateTime.UtcNow;
        return evt;
    }

    private dynamic CreateEventWithValue(int value)
    {
        dynamic evt = new System.Dynamic.ExpandoObject();
        evt.value = value;
        evt.RawLine = $"Value: {value}";
        return evt;
    }
}