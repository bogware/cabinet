using System.Globalization;
using System.Text.RegularExpressions;
using Cabinet.API.Middleware;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Extras.Gaming.Detection;
using Cabinet.Extras.Gaming.Moderation;
using Cabinet.Query;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Player profiles for game server moderators: who a player is, what they said, how toxic it was,
/// and what a moderator might do about it. Suggestions only: Cabinet never runs RCON commands.
/// Part of the optional gaming extension.
/// </summary>
[ApiController]
[Route("api/v1/moderation")]
[RequireCapability(Capability.Search)]
public sealed class ModerationController : ControllerBase
{
    private static readonly Regex PlayerKey = new(@"^[A-Za-z0-9_:\[\]\-. ]{1,64}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly IQueryEngine _engine;
    private readonly IScumscanDetector _scumscan;
    private readonly IAuditLogger _audit;

    public ModerationController(IQueryEngine engine, IScumscanDetector scumscan, IAuditLogger audit)
    {
        _engine = engine;
        _scumscan = scumscan;
        _audit = audit;
    }

    /// <summary>The most active players who chatted, with how much they said.</summary>
    [HttpGet("players")]
    [ProducesResponseType(typeof(List<Dictionary<string, object?>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Players([FromQuery] int days = 7, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            $"find ChatMessage=* SteamId=* earliest=-{Math.Clamp(days, 1, 365)}d | stats count as chats, latest(PlayerName) as name, dc(PlayerName) as names, latest(_time) as last_seen by SteamId | sort -chats | head 100",
            100, cancellationToken);
        return Ok(result.Events);
    }

    /// <summary>One player: their names, activity, chat with toxicity, history, and a suggested action.</summary>
    [HttpGet("players/{player}")]
    [ProducesResponseType(typeof(PlayerProfile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Player(string player, [FromQuery] int days = 30, CancellationToken cancellationToken = default)
    {
        if (!PlayerKey.IsMatch(player))
        {
            return Problem(detail: "Give a Steam ID or a player name of letters, digits, spaces and _:[]-.", statusCode: StatusCodes.Status400BadRequest);
        }

        var since = $"earliest=-{Math.Clamp(days, 1, 365)}d";
        var who = $"(SteamId=\"{player}\" OR PlayerName=\"{player}\")";

        var summary = (await RunAsync($"find {who} {since} | stats count as events, earliest(_time) as first_seen, latest(_time) as last_seen, values(PlayerName) as names, values(SteamId) as steam_ids", 1, cancellationToken))
            .Events.Cast<IDictionary<string, object?>>().FirstOrDefault();
        var steamId = (summary?.TryGetValue("steam_ids", out var ids) == true ? ids as IEnumerable<object?> : null)?.Select(id => id?.ToString()).FirstOrDefault(id => id != null && PlayerModeration.IsSteamId(id));
        var names = (summary?.TryGetValue("names", out var found) == true ? found as IEnumerable<object?> : null)?.Select(n => n?.ToString() ?? string.Empty).Where(n => n.Length > 0).ToList() ?? [];

        // Each chat line, scored by the same detector the gaming rules use
        var chat = new List<ChatLine>();
        foreach (var row in (await RunAsync($"find {who} ChatMessage=* {since} | table _time, PlayerName, ChatMessage", 500, cancellationToken)).Events.Cast<IDictionary<string, object?>>())
        {
            var text = row.TryGetValue("ChatMessage", out var message) ? message?.ToString() ?? string.Empty : string.Empty;
            var logEvent = new LogEvent { RawLine = text, Tags = ["chat"] };
            logEvent.SetField("ChatMessage", text);
            var flagged = await _scumscan.AnalyzeAsync(logEvent);
            chat.Add(new ChatLine
            {
                Time = row.TryGetValue("_time", out var time) ? time as DateTime? : null,
                Text = text,
                Flag = flagged?.Description,
                Severity = flagged?.Severity.ToString(),
            });
        }

        var history = (await RunAsync($"find (TargetSteamId=\"{player}\" OR TargetName=\"{player}\") AdminAction=* {since} | stats count by AdminAction", 50, cancellationToken))
            .Events.Cast<IDictionary<string, object?>>()
            .ToDictionary(row => row.TryGetValue("AdminAction", out var action) ? action?.ToString() ?? "?" : "?", row => Convert.ToInt32(row["count"], CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
        int Prior(string action) => history.Where(h => h.Key.Contains(action, StringComparison.OrdinalIgnoreCase)).Sum(h => h.Value);

        var toxic = chat.Count(c => c.Flag != null);
        var severe = chat.Count(c => c.Severity is nameof(DetectionSeverity.High) or nameof(DetectionSeverity.Critical));
        var suggestion = PlayerModeration.Suggest(toxic, severe, Prior("ban"), Prior("kick"), steamId, names.LastOrDefault() ?? player);

        await _audit.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Search,
            ResourceType = "PlayerProfile",
            Description = $"Viewed player profile {player}",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        });

        return Ok(new PlayerProfile
        {
            Player = player,
            SteamId = steamId,
            Names = names,
            Events = summary?.TryGetValue("events", out var count) == true && count != null ? Convert.ToInt64(count, CultureInfo.InvariantCulture) : 0,
            FirstSeen = summary?.TryGetValue("first_seen", out var first) == true ? first as DateTime? : null,
            LastSeen = summary?.TryGetValue("last_seen", out var last) == true ? last as DateTime? : null,
            ChatLines = chat.Count,
            ToxicLines = toxic,
            SevereLines = severe,
            AdminActions = history,
            RecentChat = chat.OrderByDescending(c => c.Time).Take(50).ToList(),
            Suggestion = suggestion,
            SuggestionNote = "A suggestion only. Cabinet never runs RCON commands; check the chat yourself, then run it from your server console if you agree.",
        });
    }

    private Task<QueryResult> RunAsync(string query, int limit, CancellationToken cancellationToken) =>
        _engine.ExecuteAsync(new QueryExecutionContext { Query = query, Limit = limit, Timeout = TimeSpan.FromSeconds(60) }, cancellationToken);
}

/// <summary>One line a player said.</summary>
public sealed class ChatLine
{
    public DateTime? Time { get; init; }

    public required string Text { get; init; }

    /// <summary>Why it was flagged, or null.</summary>
    public string? Flag { get; init; }

    public string? Severity { get; init; }
}

/// <summary>What a moderator needs to know about a player.</summary>
public sealed class PlayerProfile
{
    public required string Player { get; init; }

    public string? SteamId { get; init; }

    public required List<string> Names { get; init; }

    public long Events { get; init; }

    public DateTime? FirstSeen { get; init; }

    public DateTime? LastSeen { get; init; }

    public int ChatLines { get; init; }

    public int ToxicLines { get; init; }

    public int SevereLines { get; init; }

    /// <summary>Admin actions taken against them, by kind.</summary>
    public required Dictionary<string, int> AdminActions { get; init; }

    public required List<ChatLine> RecentChat { get; init; }

    public required ModerationSuggestion Suggestion { get; init; }

    public required string SuggestionNote { get; init; }
}
