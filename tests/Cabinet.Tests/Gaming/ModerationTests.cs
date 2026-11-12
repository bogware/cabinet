using System.Security.Claims;
using Cabinet.API.Controllers;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Extras.Gaming.Detection;
using Cabinet.Extras.Gaming.Moderation;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Gaming;

public sealed class ModerationTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, ModerationAction.None)]
    [InlineData(1, 0, 0, 0, ModerationAction.Warn)]
    [InlineData(3, 0, 0, 0, ModerationAction.Mute)]
    [InlineData(5, 0, 0, 0, ModerationAction.Kick)]
    [InlineData(1, 1, 0, 0, ModerationAction.Kick)]
    [InlineData(2, 2, 0, 0, ModerationAction.Ban)]
    [InlineData(1, 1, 0, 2, ModerationAction.Ban)]
    [InlineData(1, 1, 1, 0, ModerationAction.Ban)]
    public void Suggestions_GetHarsher_WithSeverityAndHistory(int toxic, int severe, int bans, int kicks, ModerationAction expected)
    {
        PlayerModeration.Suggest(toxic, severe, bans, kicks, "STEAM_1:0:12345", "alice").Action.Should().Be(expected);
    }

    [Fact]
    public void Bans_AreBySteamId_AndLongerAfterAnEarlierBan()
    {
        var first = PlayerModeration.Suggest(2, 2, 0, 0, "STEAM_1:0:12345", "alice");
        var repeat = PlayerModeration.Suggest(1, 1, 1, 0, "STEAM_1:0:12345", "alice");

        first.Command.Should().StartWith("sm_addban 1440 \"STEAM_1:0:12345\"");
        repeat.BanMinutes.Should().BeGreaterThan(first.BanMinutes!.Value);
        PlayerModeration.Suggest(2, 2, 0, 0, null, "alice").Command.Should().BeNull("a ban by name could catch someone else");
    }

    [Fact]
    public void Commands_CantBeMadeToRunAnotherCommand()
    {
        // The Source console splits at ; so a pasted command must not carry one from a name
        var suggestion = PlayerModeration.Suggest(5, 0, 0, 0, null, "evil\"; rcon_password x; \"");

        suggestion.Command.Should().NotContain(";").And.StartWith("sm_kick \"evil rcon_password x").And.EndWith("\"5 toxic lines\"");
    }

    [Fact]
    public async Task Profile_ScoresChat_AndSuggests_WithoutRunningAnything()
    {
        var storage = new InMemoryStorage();
        var now = DateTime.UtcNow;
        LogEvent Chat(string text, int minutesAgo)
        {
            var e = new LogEvent { RawLine = $"\"alice<2><STEAM_1:0:12345><Survivor>\" say \"{text}\"", Timestamp = now.AddMinutes(-minutesAgo), Tags = ["chat"] };
            e.SetField("PlayerName", "alice");
            e.SetField("SteamId", "STEAM_1:0:12345");
            e.SetField("ChatMessage", text);
            return e;
        }

        await storage.StoreBatchAsync([Chat("gg wp everyone", 30), Chat("i will kill you, you idiot", 20), Chat("you are trash and a loser", 10)]);
        var controller = new ModerationController(
            new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance),
            new ScumscanDetector(NullLogger<ScumscanDetector>.Instance),
            new Mock<IAuditLogger>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("ClientId", "mod")], "ApiKey")) } },
        };

        var profile = (PlayerProfile)((OkObjectResult)await controller.Player("STEAM_1:0:12345")).Value!;

        profile.Names.Should().Equal("alice");
        profile.ChatLines.Should().Be(3);
        profile.ToxicLines.Should().BeGreaterThan(0);
        profile.Suggestion.Action.Should().NotBe(ModerationAction.None);
        profile.SuggestionNote.Should().Contain("never runs RCON");
        (await controller.Player("bad;id")).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }
}
