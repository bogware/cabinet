using Cabinet.CLI.Services;
using Cabinet.Parsers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cabinet.Tests.CLI;

public sealed class RuleFilesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-rules-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static LogLineParser Parser() => new ServiceCollection().AddLogging().AddCabinetParsers().BuildServiceProvider().GetRequiredService<LogLineParser>();

    private static string Repo(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Cabinet.sln"))) dir = dir.Parent;
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public async Task TheExampleRule_PassesItsTests()
    {
        var result = await RuleFiles.TestAsync(Repo("docs", "rules", "ssh-brute-force.yaml"), Parser());

        result.Problems.Should().BeEmpty();
        result.TestsRun.Should().Be(2);
        result.Rule!.ToRule().GroupBy.Should().Be("src");
    }

    [Fact]
    public async Task AFailingTest_AndAWrongField_AreReported()
    {
        var file = Path.Combine(_directory, "r.yaml");
        await File.WriteAllTextAsync(file, """
            id: r1
            name: Disk full
            pattern: disk full
            extract: on (?<disk>\S+)
            tests:
              - line: "disk full on /dev/sda1"
                fields: { disk: /dev/sdb1 }
              - line: "disk almost full"
            """);

        var result = await RuleFiles.TestAsync(file, Parser());

        result.Problems.Should().HaveCount(2);
        result.Problems[0].Should().Contain("disk should be '/dev/sdb1'");
        result.Problems[1].Should().Contain("test 2 should match");
    }

    [Theory]
    [InlineData("name: x\npattern: y\ntests: [{line: y}]", "id is required")]
    [InlineData("id: a\nname: x\ntests: [{line: y}]", "needs a pattern, a condition")]
    [InlineData("id: a\nname: x\ncondition: 'status >'\ntests: [{line: y}]", "doesn't compile")]
    [InlineData("id: a\nname: x\npattern: y", "add at least one line")]
    [InlineData("id: [", "Not a valid rule file")]
    public async Task BadFiles_SayWhatsWrong(string yaml, string problem)
    {
        var file = Path.Combine(_directory, "bad.yaml");
        await File.WriteAllTextAsync(file, yaml);

        (await RuleFiles.TestAsync(file, Parser())).Problems.Should().Contain(p => p.Contains(problem));
    }

    [Fact]
    public void PulledRules_RoundTripThroughYaml()
    {
        var rule = new Cabinet.Core.Models.DetectionRule { Id = "x1", Name = "Thing", Condition = "status >= 500", Technique = "T1190" };

        var yaml = RuleFiles.Serialize(RuleFile.From(rule));

        yaml.Should().Contain("id: x1").And.Contain("condition: status >= 500").And.Contain("technique: T1190").And.NotContain("tests");
    }
}
