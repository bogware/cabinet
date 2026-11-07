using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Extras.Gaming;
using Cabinet.Parsers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cabinet.Tests.Parsers;

/// <summary>
/// A line goes to the first parser that accepts it, so registration order decides what a line
/// becomes. These use the real registrations.
/// </summary>
public class ParserSelectionTests
{
    private const string JsonMentioningSteam = """{"message":"Steam workshop update state changed","level":"INFO"}""";
    private const string CefWithCommas = "CEF:0|Vendor|Product|1.0|100|Attack, blocked, logged|5|src=10.0.0.1";
    private const string ApacheLine = """127.0.0.1 - frank [10/Oct/2026:13:55:36 -0700] "GET /a.gif HTTP/1.0" 200 2326""";
    private const string CsvLine = "alpha,beta,gamma,delta";
    private const string PlainLine = "the workshop opened at nine";
    private const string L4D2Line = "L 12/07/2024 - 10:23:45: World triggered \"Round_Start\"";

    private static ServiceProvider Build(bool gaming)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (gaming)
        {
            services.AddCabinetParsers(s => s.AddGamingParsers());
        }
        else
        {
            services.AddCabinetParsers();
        }

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(false, JsonMentioningSteam, SourceTypes.Json)]
    [InlineData(true, JsonMentioningSteam, SourceTypes.Json)]
    [InlineData(false, CefWithCommas, SourceTypes.Cef)]
    [InlineData(true, CefWithCommas, SourceTypes.Cef)]
    [InlineData(false, ApacheLine, SourceTypes.Apache)]
    [InlineData(true, ApacheLine, SourceTypes.Apache)]
    [InlineData(false, CsvLine, SourceTypes.Csv)]
    [InlineData(false, PlainLine, SourceTypes.Generic)]
    [InlineData(false, L4D2Line, SourceTypes.Generic)]
    [InlineData(true, L4D2Line, GamingSourceTypes.L4D2)]
    public void Provider_ChoosesTheRightParser(bool gaming, string line, string expected)
    {
        using var provider = Build(gaming);

        var parser = provider.GetRequiredService<ILogParserProvider>().GetParser(line);

        parser!.SourceType.Should().Be(expected);
    }

    [Theory]
    [InlineData(false, JsonMentioningSteam, SourceTypes.Json)]
    [InlineData(false, ApacheLine, SourceTypes.Apache)]
    [InlineData(false, PlainLine, SourceTypes.Generic)]
    [InlineData(true, L4D2Line, GamingSourceTypes.L4D2)]
    public void Registry_AgreesWithTheProvider(bool gaming, string line, string expected)
    {
        using var provider = Build(gaming);

        var parser = provider.GetRequiredService<IParserRegistry>().FindParser(line);

        parser!.SourceType.Should().Be(expected);
    }
}
