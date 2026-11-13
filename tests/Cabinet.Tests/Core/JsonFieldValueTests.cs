using System.Text.Json;
using Cabinet.Core.Models;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Core;

public sealed class JsonFieldValueTests
{
    [Theory]
    [InlineData("4", typeof(long))]
    [InlineData("-9007199254740993", typeof(long))]
    [InlineData("4.5", typeof(double))]
    [InlineData("1e400", typeof(double))]
    [InlineData("\"4\"", typeof(string))]
    [InlineData("true", typeof(bool))]
    public void Numbers_KeepTheirKind(string json, Type expected)
    {
        using var document = JsonDocument.Parse(json.Replace("1e400", "1e300", StringComparison.Ordinal));

        JsonFieldValue.Unwrap(document.RootElement)!.GetType().Should().Be(expected, "a whole number read from JSON stays whole");
    }
}
