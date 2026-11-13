using System.Security.Claims;
using Cabinet.API.Authentication;
using Cabinet.API.Controllers;
using Cabinet.API.Services.Archive;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>Searching archived events where they are, without rehydrating them.</summary>
public sealed class ArchiveSearchTests : IAsyncLifetime
{
    private static readonly DateTime Day = new(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-archive-search-").FullName;
    private EventArchiver _archiver = null!;

    public async Task InitializeAsync()
    {
        // Three archived days of web events: a 500 every tenth event
        var storage = new InMemoryStorage();
        await storage.StoreBatchAsync(Enumerable.Range(0, 3 * 240).Select(i =>
        {
            var e = new LogEvent
            {
                RawLine = $"GET /p{i % 4} {(i % 10 == 0 ? "500 failed" : "200 ok")}",
                Index = "web",
                Host = $"web-{i % 2}",
                Severity = i % 10 == 0 ? LogSeverity.Error : LogSeverity.Info,
                Timestamp = Day.AddMinutes(i * 6),
            };
            e.SetField("status", i % 10 == 0 ? 500L : 200L);
            return e;
        }));
        _archiver = new EventArchiver(new DirectoryEventArchive(_directory), NullLogger<EventArchiver>.Instance);
        await _archiver.ArchiveAsync(storage, "web", Day.AddDays(3), CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    private ArchiveController Controller(IndexAccessScope? scope = null, int maxEvents = 500_000)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Archive:SearchMaxEvents"] = maxEvents.ToString(System.Globalization.CultureInfo.InvariantCulture) }).Build();
        var controller = new ArchiveController(new InMemoryStorage(), scope ?? new IndexAccessScope(), Mock.Of<IAuditLogger>(), _archiver, new DirectoryEventArchive(_directory), configuration);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("ClientId", "test")], "test")) } };
        return controller;
    }

    [Fact]
    public async Task APlainQuestion_RunsOverTheArchivedDays_InTheRange()
    {
        var result = await Controller().Search(new ArchiveSearchRequest
        {
            Index = "web",
            From = Day.AddDays(1),
            To = Day.AddDays(2).AddTicks(-1),
            Query = "errors by host",
        }, CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<QueryResponse>().Subject;
        response.Query.Should().Be("find * | where severity >= error | stats count by host | sort by count desc");
        response.Results.Cast<IDictionary<string, object?>>().Sum(row => Convert.ToInt32(row["count"], System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(24, "one day holds 240 events, a tenth of them errors");
    }

    [Fact]
    public async Task AWordSearch_FindsArchivedLines()
    {
        var result = await Controller().Search(new ArchiveSearchRequest { Index = "web", From = Day, To = Day.AddDays(3), Query = "find failed | stats count", Exact = true }, CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<QueryResponse>().Subject;
        Convert.ToInt32(((IDictionary<string, object?>)response.Results.Single())["count"], System.Globalization.CultureInfo.InvariantCulture).Should().Be(72);
    }

    [Fact]
    public async Task TooManyEvents_AreRefused_WithWhatToDoInstead()
    {
        var result = await Controller(maxEvents: 100).Search(new ArchiveSearchRequest { Index = "web", From = Day, To = Day.AddDays(3), Query = "find *" }, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Contain("shorter time");
    }

    [Fact]
    public async Task AnIndexTheCallerCantRead_IsRefused()
    {
        var scope = new IndexAccessScope { ReadableIndexes = ["db"] };

        var result = await Controller(scope).Search(new ArchiveSearchRequest { Index = "web", From = Day, To = Day.AddDays(3), Query = "find *" }, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }
}
