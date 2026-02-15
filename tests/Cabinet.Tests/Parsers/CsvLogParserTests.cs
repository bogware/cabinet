using Cabinet.Core.Models;
using Cabinet.Parsers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Parsers;

public class CsvLogParserTests
{
    private readonly CsvLogParser _parser = new();

    [Fact]
    public void CanParse_CsvLine_ReturnsTrue()
    {
        _parser.CanParse("field1,field2,field3").Should().BeTrue();
        _parser.CanParse("a,b,c,d,e").Should().BeTrue();
    }

    [Fact]
    public void CanParse_SingleField_ReturnsFalse()
    {
        _parser.CanParse("just plain text").Should().BeFalse();
        _parser.CanParse("").Should().BeFalse();
    }

    [Fact]
    public void CanParse_TwoFields_ReturnsFalse()
    {
        // Need at least 3 comma-separated fields
        _parser.CanParse("field1,field2").Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_SimpleCsv_ExtractsFields()
    {
        var result = await _parser.ParseAsync("2024-01-01,error,Something went wrong,web-01");

        result.Should().NotBeNull();
        result!.SourceType.Should().Be(SourceTypes.Csv);
        result.GetFieldString("field_0").Should().Be("2024-01-01");
        result.GetFieldString("field_1").Should().Be("error");
        result.GetFieldString("field_2").Should().Be("Something went wrong");
        result.GetFieldString("field_3").Should().Be("web-01");
        result.GetField<int>("FieldCount").Should().Be(4);
        result.Tags.Should().Contain("csv");
    }

    [Fact]
    public async Task ParseAsync_QuotedFields_HandlesCorrectly()
    {
        var result = await _parser.ParseAsync("\"hello, world\",field2,field3");

        result.Should().NotBeNull();
        result!.GetFieldString("field_0").Should().Be("hello, world");
        result.GetFieldString("field_1").Should().Be("field2");
    }

    [Fact]
    public async Task ParseAsync_EscapedQuotes_HandlesCorrectly()
    {
        var result = await _parser.ParseAsync("\"value with \"\"quotes\"\"\",field2,field3");

        result.Should().NotBeNull();
        result!.GetFieldString("field_0").Should().Be("value with \"quotes\"");
    }

    [Fact]
    public async Task ParseAsync_EmptyFields_Skipped()
    {
        var result = await _parser.ParseAsync("value1,,value3");

        result.Should().NotBeNull();
        result!.GetFieldString("field_0").Should().Be("value1");
        result.GetFieldString("field_1").Should().BeNull(); // Empty field skipped
        result.GetFieldString("field_2").Should().Be("value3");
    }

    [Theory]
    [InlineData("a,b,c", 3)]
    [InlineData("a,b,c,d", 4)]
    [InlineData("a,b,c,d,e,f", 6)]
    public async Task ParseAsync_FieldCount_Correct(string csv, int expectedCount)
    {
        var result = await _parser.ParseAsync(csv);

        result.Should().NotBeNull();
        result!.GetField<int>("FieldCount").Should().Be(expectedCount);
    }

    [Fact]
    public void ParseCsvLine_RFC4180_Compliant()
    {
        var fields = CsvLogParser.ParseCsvLine("simple,\"with comma, inside\",\"with \"\"quotes\"\"\"");

        fields.Should().HaveCount(3);
        fields[0].Should().Be("simple");
        fields[1].Should().Be("with comma, inside");
        fields[2].Should().Be("with \"quotes\"");
    }

    [Fact]
    public void CanParse_QuotedCommas_NotCounted()
    {
        // Commas inside quotes shouldn't count toward the threshold
        _parser.CanParse("\"a,b,c\"").Should().BeFalse();
    }

    [Fact]
    public void Name_ReturnsCsv()
    {
        _parser.Name.Should().Be("CSV");
    }

    [Fact]
    public void SourceType_ReturnsCsv()
    {
        _parser.SourceType.Should().Be(SourceTypes.Csv);
    }
}
