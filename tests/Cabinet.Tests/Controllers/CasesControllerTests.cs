using System.Security.Claims;
using Cabinet.API.Authentication;
using Cabinet.API.Controllers;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cabinet.Tests.Controllers;

public sealed class CasesControllerTests
{
    private static async Task<(CasesController Controller, LogEvent Visible, LogEvent Secret)> CreateAsync()
    {
        var storage = new InMemoryStorage();
        var visible = new LogEvent { RawLine = "login failed for alice", Index = "team-a", Timestamp = new DateTime(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc) };
        var secret = new LogEvent { RawLine = "classified", Index = "secret", Timestamp = new DateTime(2026, 3, 4, 4, 0, 0, DateTimeKind.Utc) };
        await storage.StoreBatchAsync([visible, secret]);

        var scope = new IndexAccessScope { ReadableIndexes = ["team-a"], Principal = new RbacPrincipal { Id = "analyst", Role = SystemRole.Analyst } };
        var controller = new CasesController(new CaseStore(new InMemoryDocumentStore()), storage, scope, new Mock<IAuditLogger>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("ClientId", "analyst")], "ApiKey")) } },
        };
        return (controller, visible, secret);
    }

    [Fact]
    public async Task Timeline_PutsEverythingInTimeOrder_AndLeavesOutWhatTheReaderCantRead()
    {
        var (controller, visible, secret) = await CreateAsync();
        var created = (Case)((CreatedAtActionResult)await controller.Create(new CaseRequest { Title = "Brute force on web", EventIds = [visible.Id, secret.Id] }, CancellationToken.None)).Value!;
        await controller.Add(created.Id, new CaseItemsRequest { Note = "Blocked the address", Search = "find failed alice" }, CancellationToken.None);
        await controller.Update(created.Id, new CaseRequest { Status = CaseStatus.Resolved }, CancellationToken.None);

        var timeline = (List<TimelineItem>)((OkObjectResult)await controller.Timeline(created.Id, CancellationToken.None)).Value!;

        timeline.Select(t => t.Kind).Should().Equal("event", "change", "note", "search", "change");
        timeline[0].Text.Should().Be("login failed for alice");
        timeline.Should().NotContain(t => t.Text.Contains("classified"));
        timeline[^1].Text.Should().Be("Changed status from Open to Resolved");
        controller.Response.Headers["X-Hidden-Events"].ToString().Should().Be("1");
    }

    [Fact]
    public async Task AddingTheSameEventTwice_KeepsOne()
    {
        var (controller, visible, _) = await CreateAsync();
        var created = (Case)((CreatedAtActionResult)await controller.Create(new CaseRequest { Title = "x" }, CancellationToken.None)).Value!;

        await controller.Add(created.Id, new CaseItemsRequest { EventIds = [visible.Id] }, CancellationToken.None);
        var after = (Case)((OkObjectResult)await controller.Add(created.Id, new CaseItemsRequest { EventIds = [visible.Id] }, CancellationToken.None)).Value!;

        after.Events.Should().ContainSingle();
    }
}
