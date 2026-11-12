using Cabinet.API.Services.Sigma;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class SigmaConverterTests
{
    private const string FailedLogons = """
        title: Many Failed Logons From One Source
        id: 0e95725d-7320-415d-80f7-004da920fc11
        status: test
        description: Detects many failed logons from one address
        tags:
            - attack.credential_access
            - attack.t1110.001
        logsource:
            product: windows
            service: security
        detection:
            selection:
                EventID: 4625
                LogonType:
                    - 3
                    - 10
            filter:
                TargetUserName|endswith: '$'
            condition: selection and not filter | count() by IpAddress > 5
            timeframe: 10m
        falsepositives:
            - Misconfigured services
        level: medium
        """;

    private const string EncodedPowerShell = """
        title: Encoded PowerShell
        tags: [attack.execution, attack.t1059.001]
        logsource: { product: windows, category: process_creation }
        detection:
            selection_image:
                Image|endswith: '\powershell.exe'
            selection_args:
                CommandLine|contains|windash:
                    - ' -enc '
                    - ' -EncodedCommand '
            condition: all of selection_*
        level: high
        """;

    private static async Task<int> CountAsync(string condition, params Dictionary<string, object>[] events)
    {
        var storage = new InMemoryStorage();
        await storage.StoreBatchAsync(events.Select(fields =>
        {
            var e = new LogEvent { RawLine = string.Join(' ', fields.Select(f => $"{f.Key}={f.Value}")) };
            foreach (var (k, v) in fields) e.SetField(k, v);
            return e;
        }));
        var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
        return (await engine.ExecuteAsync(new QueryExecutionContext { Query = $"find * | where {condition}" })).Events.Count;
    }

    [Fact]
    public async Task Selections_Filters_AndCount_Convert()
    {
        var rule = SigmaConverter.Convert(FailedLogons).Single().Rule!;

        rule.Name.Should().Be("Many Failed Logons From One Source");
        rule.Technique.Should().Be("T1110.001");
        rule.Severity.Should().Be("Warning");
        rule.GroupBy.Should().Be("IpAddress");
        rule.Threshold.Should().Be(6);
        rule.WindowSeconds.Should().Be(600);
        rule.Enabled.Should().BeFalse("an imported rule waits for someone to check it");
        rule.Description.Should().Contain("Misconfigured services").And.Contain("0e95725d");

        (await CountAsync(rule.Condition!,
            new() { ["EventID"] = 4625L, ["LogonType"] = 3L, ["TargetUserName"] = "alice" },
            new() { ["EventID"] = 4625L, ["LogonType"] = 10L, ["TargetUserName"] = "WEB01$" },
            new() { ["EventID"] = 4625L, ["LogonType"] = 2L, ["TargetUserName"] = "bob" },
            new() { ["EventID"] = 4624L, ["LogonType"] = 3L, ["TargetUserName"] = "carol" })).Should().Be(1);
    }

    [Fact]
    public async Task Modifiers_AndOfSelections_Convert_CaseInsensitively()
    {
        var rule = SigmaConverter.Convert(EncodedPowerShell).Single().Rule!;

        rule.Technique.Should().Be("T1059.001");
        rule.Severity.Should().Be("Error");
        (await CountAsync(rule.Condition!,
            new() { ["Image"] = @"C:\Windows\System32\WindowsPowerShell\v1.0\POWERSHELL.EXE", ["CommandLine"] = "powershell /enc SQBFAFgA" },
            new() { ["Image"] = @"C:\Windows\System32\cmd.exe", ["CommandLine"] = "cmd -enc x" },
            new() { ["Image"] = @"C:\x\powershell.exe", ["CommandLine"] = "powershell -File a.ps1" })).Should().Be(1);
    }

    [Fact]
    public void SeveralDocuments_AndUnsupportedParts_AreEachReported()
    {
        var results = SigmaConverter.Convert(FailedLogons + "\n---\n" + """
            title: Uses an unknown modifier
            detection:
                selection:
                    CommandLine|base64offset|contains: whoami
                condition: selection
            """);

        results.Should().HaveCount(2);
        results[0].Rule.Should().NotBeNull();
        results[1].Rule.Should().BeNull();
        results[1].Problem.Should().Contain("base64offset");
    }

    [Fact]
    public void Coverage_CountsSubTechniquesUnderTheirTechnique()
    {
        MitreAttack.Parent("T1110.001").Should().Be("T1110");
        MitreAttack.Techniques.Should().Contain(t => t.Id == "T1110" && t.Tactics.Contains("TA0006"));
        MitreAttack.Techniques.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        MitreAttack.Techniques.SelectMany(t => t.Tactics).Should().OnlyContain(id => MitreAttack.Tactics.Any(tactic => tactic.Id == id));
    }
}
