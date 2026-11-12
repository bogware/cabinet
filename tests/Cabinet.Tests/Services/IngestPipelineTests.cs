using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class IngestPipelineTests
{
    private static async Task<(PipelineLogStorage Storage, InMemoryStorage Inner, IngestPipeline Pipeline)> CreateAsync(params PipelineRule[] rules)
    {
        var documents = new InMemoryDocumentStore();
        foreach (var rule in rules)
        {
            await documents.PutAsync(IngestPipeline.Collection, rule.Id, rule);
        }

        var pipeline = new IngestPipeline(documents, NullLogger<IngestPipeline>.Instance);
        var inner = new InMemoryStorage();
        return (new PipelineLogStorage(inner, pipeline), inner, pipeline);
    }

    private static LogEvent Event(string line, LogSeverity severity = LogSeverity.Info, string index = "main") =>
        new() { RawLine = line, Severity = severity, Index = index };

    [Fact]
    public async Task Drop_KeepsMatchingEventsOutOfStorage_AndCountsTheBytes()
    {
        var drop = new PipelineRule { Name = "no debug", Condition = "severity <= 1", Action = PipelineAction.Drop };
        var (storage, inner, pipeline) = await CreateAsync(drop);

        var stored = await storage.StoreBatchAsync([Event("debug noise", LogSeverity.Debug), Event("real error", LogSeverity.Error)]);

        stored.Should().Be(1);
        (await inner.GetCountAsync()).Should().Be(1);
        pipeline.Stats[drop.Id].Dropped.Should().Be(1);
        pipeline.Stats[drop.Id].BytesDropped.Should().Be("debug noise".Length);
    }

    [Fact]
    public async Task Sample_KeepsAboutOneInN_AndTheSameOnesWhenResent()
    {
        var (storage, inner, _) = await CreateAsync(new PipelineRule { Name = "sample health checks", Pattern = "GET /health", Action = PipelineAction.Sample, SampleRate = 10 });
        var events = Enumerable.Range(0, 2000).Select(i => Event($"GET /health {i}")).ToList();

        await storage.StoreBatchAsync(events);
        var first = await inner.GetCountAsync();
        await storage.StoreBatchAsync(events);

        first.Should().BeInRange(140, 260);
        (await inner.GetCountAsync()).Should().Be(first, "a resent event gets the same answer, and storage ignores IDs it has");
    }

    [Fact]
    public async Task RouteAndRedact_ChangeTheEvent_InOrder()
    {
        var (storage, inner, _) = await CreateAsync(
            new PipelineRule { Name = "audit to its own index", Order = 1, Pattern = "^AUDIT", Action = PipelineAction.Route, TargetIndex = "audit" },
            new PipelineRule { Name = "hide card numbers", Order = 2, Action = PipelineAction.Redact, RedactPattern = @"\b\d{4}(?:[ -]?\d{4}){3}\b", Replacement = "[CARD]" });
        var withCard = Event("AUDIT paid with 4111 1111 1111 1111");
        withCard.SetField("card", "4111-1111-1111-1111");

        await storage.StoreBatchAsync([withCard, Event("normal line")]);

        var audit = await inner.QueryAsync(new QueryContext { Index = "audit" }).SingleAsync();
        audit.RawLine.Should().Be("AUDIT paid with [CARD]");
        audit.GetFieldString("card").Should().Be("[CARD]");
        (await inner.GetCountAsync(new QueryContext { Index = "main" })).Should().Be(1);
    }

    [Fact]
    public async Task Stop_EndsThePipelineForWhatItMatched()
    {
        var (storage, inner, _) = await CreateAsync(
            new PipelineRule { Name = "keep errors whatever follows", Order = 1, Condition = "severity >= 4", Action = PipelineAction.Route, TargetIndex = "main", Stop = true },
            new PipelineRule { Name = "drop everything else", Order = 2, Action = PipelineAction.Drop });

        await storage.StoreBatchAsync([Event("error", LogSeverity.Error), Event("info")]);

        (await inner.QueryAsync(new QueryContext()).SingleAsync()).RawLine.Should().Be("error");
    }

    [Theory]
    [InlineData(PipelineAction.Sample, "sample")]
    [InlineData(PipelineAction.Route, "index name")]
    [InlineData(PipelineAction.Redact, "pattern")]
    public void IncompleteRules_AreRefused(PipelineAction action, string problem)
    {
        IngestPipeline.Validate(new PipelineRule { Name = "x", Action = action, SampleRate = 1 })!.ToLowerInvariant().Should().Contain(problem);
    }
}
