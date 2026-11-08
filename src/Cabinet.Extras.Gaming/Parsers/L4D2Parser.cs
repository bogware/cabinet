using System.Text.RegularExpressions;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Extras.Gaming.Parsers;

/// <summary>
/// Parser for Left 4 Dead 2 server logs (<c>log on</c>), which name players as
/// <c>"Name&lt;userid&gt;&lt;steamid&gt;&lt;team&gt;"</c>, followed in newer lines by character,
/// state, health, position and area.
/// </summary>
public partial class L4D2Parser : GamingLogParser
{
    public L4D2Parser(ILogger<L4D2Parser>? logger = null) : base(logger) { }

    public override string Name => "L4D2";
    public override string SourceType => GamingSourceTypes.L4D2;

    [GeneratedRegex(@"^L\s+\d{2}/\d{2}/\d{4}")]
    private static partial Regex L4D2LogPattern();

    // A player: name, user ID, Steam ID or BOT, team, then optional character, state and so on.
    // Names can hold quotes and spaces but not '<'.
    private const string Player = @"""(?<name>[^<]*)<(?<uid>-?\d+)><(?<steam>[^>]*)><(?<team>[^>]*)>(?:<(?<class>[^>]*)>)?(?:<[^>]*>)*""";

    [GeneratedRegex(@"^L\s+\d{2}/\d{2}/\d{4}\s+-\s+\d{2}:\d{2}:\d{2}:\s*")]
    private static partial Regex Prefix();

    public override bool CanParse(string line)
    {
        // L4D2 logs typically start with "L MM/DD/YYYY"
        return L4D2LogPattern().IsMatch(line);
    }

    // Severity comes from what the server wrote, never from what a player typed in chat, and
    // player names and kill lines (which say "DEAD") aren't read as levels either.
    protected override void ExtractSeverity(string line, LogEvent logEvent)
    {
        var body = Prefix().Replace(line, string.Empty, 1);
        logEvent.Severity = body.StartsWith('"') || body.StartsWith("(DEATH)", StringComparison.Ordinal)
            ? LogSeverity.Info
            : Cabinet.Parsers.LogSeverities.Detect(body);
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        ExtractL4D2Timestamp(line, logEvent);
        var body = Prefix().Replace(line, string.Empty, 1);

        if (ExtractKill(body, logEvent))
        {
            return;
        }

        var chat = ExtractChat(body, logEvent);
        ExtractPlayer(body, logEvent);
        ExtractMapInfo(body, logEvent);
        ExtractConnection(body, logEvent);
        ExtractServerEvents(body, logEvent);

        // Keywords are looked for in what the server said, never in what a player typed
        DetectImportantEvents(chat == null ? body : body[..chat.Value], logEvent);
    }

    private void ExtractL4D2Timestamp(string line, LogEvent logEvent)
    {
        // L MM/DD/YYYY - HH:MM:SS:
        var match = Regex.Match(line, @"L\s+(\d{2})/(\d{2})/(\d{4})\s+-\s+(\d{2}):(\d{2}):(\d{2}):", RegexOptions.None, RegexTimeout);
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out var month) &&
                int.TryParse(match.Groups[2].Value, out var day) &&
                int.TryParse(match.Groups[3].Value, out var year) &&
                int.TryParse(match.Groups[4].Value, out var hour) &&
                int.TryParse(match.Groups[5].Value, out var minute) &&
                int.TryParse(match.Groups[6].Value, out var second))
            {
                try
                {
                    // srcds writes the server's local time with no offset. Left unspecified, it's
                    // read in the sender's time zone, or as UTC when that isn't known.
                    logEvent.Timestamp = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to parse L4D2 timestamp: {Match}", match.Value);
                    logEvent.Timestamp = DateTime.UtcNow;
                }
            }
        }
    }

    private static bool IsSteamId(string value) =>
        value.StartsWith("STEAM_", StringComparison.Ordinal) || value.StartsWith("[U:", StringComparison.Ordinal);

    /// <summary>Sets the fields for a player, with an optional prefix such as Killer or Victim.</summary>
    private static void SetPlayer(LogEvent logEvent, Match match, string prefix)
    {
        var name = match.Groups["name"].Value;
        var steam = match.Groups["steam"].Value;
        logEvent.SetField(prefix.Length == 0 ? "PlayerName" : prefix, name);
        if (prefix.Length == 0)
        {
            logEvent.SetField("PlayerId", match.Groups["uid"].Value);
        }

        if (IsSteamId(steam))
        {
            logEvent.SetField(prefix + "SteamId", steam);
        }
        else if (steam == "BOT")
        {
            logEvent.SetField(prefix + "IsBot", true);
        }

        if (match.Groups["team"].Value is { Length: > 0 } team)
        {
            logEvent.SetField(prefix + "Team", team);
        }

        if (match.Groups["class"].Success && match.Groups["class"].Value is { Length: > 0 } character)
        {
            logEvent.SetField(prefix + "Character", character);
        }
    }

    private bool ExtractKill(string body, LogEvent logEvent)
    {
        // (DEATH)"Killer<...>..." killed "Victim<...>..." with "weapon"
        var match = Regex.Match(body, $@"^(?:\(DEATH\))?{Player} killed (?<victim>{Player.Replace("?<", "?<v")}) with ""(?<weapon>[^""]*)""(?<rest>.*)$", RegexOptions.None, RegexTimeout);
        if (!match.Success)
        {
            return false;
        }

        SetPlayer(logEvent, match, "Killer");
        var victim = Regex.Match(match.Groups["victim"].Value, "^" + Player + "$", RegexOptions.None, RegexTimeout);
        if (victim.Success)
        {
            SetPlayer(logEvent, victim, "Victim");
        }

        logEvent.SetField("Weapon", match.Groups["weapon"].Value);
        if (match.Groups["rest"].Value.Contains("headshot", StringComparison.OrdinalIgnoreCase))
        {
            logEvent.Tags.Add("headshot");
        }

        logEvent.Tags.Add("kill");
        if (logEvent.Fields.ContainsKey("KillerSteamId") && logEvent.Fields.ContainsKey("VictimSteamId"))
        {
            // Two people, not a player and a bot or common infected
            logEvent.Tags.Add("player_kill");
        }

        return true;
    }

    /// <summary>Records a chat message and returns where it starts in the line, or null.</summary>
    private int? ExtractChat(string body, LogEvent logEvent)
    {
        var match = Regex.Match(body, $@"^{Player} (?<kind>say_team|say) ""(?<text>.*)""\s*$", RegexOptions.None, RegexTimeout);
        if (!match.Success)
        {
            return null;
        }

        logEvent.SetField("ChatMessage", match.Groups["text"].Value);
        logEvent.Tags.Add(match.Groups["kind"].Value == "say_team" ? "team_chat" : "all_chat");
        return match.Groups["kind"].Index;
    }

    private void ExtractPlayer(string body, LogEvent logEvent)
    {
        var match = Regex.Match(body, "^" + Player, RegexOptions.None, RegexTimeout);
        if (match.Success)
        {
            SetPlayer(logEvent, match, string.Empty);
            return;
        }

        if (ExtractSteamId(body) is { } steamId)
        {
            logEvent.SetField("SteamId", steamId);
        }
    }

    private static void ExtractMapInfo(string body, LogEvent logEvent)
    {
        var match = Regex.Match(body, @"^(Loading|Started) map ""([^""]+)""", RegexOptions.None, RegexTimeout);
        if (match.Success)
        {
            logEvent.SetField("Map", match.Groups[2].Value);
            logEvent.Tags.Add(match.Groups[1].Value == "Loading" ? "map_load" : "map_start");
        }
    }

    private void ExtractConnection(string body, LogEvent logEvent)
    {
        if (Regex.IsMatch(body, $"^{Player} connected(?:, address |$)", RegexOptions.None, RegexTimeout))
        {
            if (ExtractIpAddress(body) is { } ip)
            {
                logEvent.SetField("IpAddress", ip);
            }

            logEvent.Tags.Add("player_connect");
        }
        else if (Regex.IsMatch(body, $"^{Player} entered the game", RegexOptions.None, RegexTimeout))
        {
            logEvent.Tags.Add("player_enter");
        }
        else if (Regex.Match(body, $@"^{Player} disconnected \(reason ""(?<reason>.*)""\)", RegexOptions.None, RegexTimeout) is { Success: true } disconnect)
        {
            logEvent.SetField("DisconnectReason", disconnect.Groups["reason"].Value);
            logEvent.Tags.Add("player_disconnect");
        }
        else if (Regex.IsMatch(body, $"^{Player} STEAM USERID validated", RegexOptions.None, RegexTimeout))
        {
            logEvent.Tags.Add("player_validated");
        }
    }

    private void ExtractServerEvents(string body, LogEvent logEvent)
    {
        if (Regex.Match(body, @"^World triggered ""([^""]+)""", RegexOptions.None, RegexTimeout) is { Success: true } world)
        {
            logEvent.SetField("GameEvent", world.Groups[1].Value);
            logEvent.Tags.Add("world_event");
        }

        if (Regex.Match(body, @"^Banid: .*? was banned ""([^""]*)"" by ""([^""]*)""", RegexOptions.None, RegexTimeout) is { Success: true } ban)
        {
            logEvent.SetField("BanDuration", ban.Groups[1].Value.Replace("for ", string.Empty, StringComparison.Ordinal));
            logEvent.SetField("BannedBy", ban.Groups[2].Value);
            logEvent.Tags.Add("ban");
        }

        if (Regex.Match(body, @"^server_cvar: ""([^""]+)"" ""([^""]*)""", RegexOptions.None, RegexTimeout) is { Success: true } cvar)
        {
            logEvent.SetField("Cvar", cvar.Groups[1].Value);
            logEvent.SetField("CvarValue", cvar.Groups[2].Value);
            logEvent.Tags.Add("cvar_change");
        }

        if (body.Contains("rcon from", StringComparison.Ordinal))
        {
            if (ExtractIpAddress(body) is { } ip)
            {
                logEvent.SetField("RconSource", ip);
            }

            logEvent.Tags.Add("rcon");
            logEvent.Severity = LogSeverity.Warning;
        }

        if (body.Contains("needs to be restarted in order to receive the latest update", StringComparison.Ordinal))
        {
            logEvent.Tags.Add("update_required");
            logEvent.Severity = LogSeverity.Warning;
        }

        if (body.StartsWith("Vote passed", StringComparison.Ordinal))
        {
            logEvent.Tags.Add("vote_passed");
        }
        else if (body.StartsWith("Vote failed", StringComparison.Ordinal))
        {
            logEvent.Tags.Add("vote_failed");
        }
        else if (body.Contains("called vote", StringComparison.Ordinal) || body.Contains("callvote", StringComparison.Ordinal))
        {
            logEvent.Tags.Add("vote_called");
        }

        if (body.StartsWith("Log file started", StringComparison.Ordinal) || body.Contains("Server is hibernating", StringComparison.Ordinal))
        {
            logEvent.Tags.Add("server_restart");
        }
    }

    private static void DetectImportantEvents(string text, LogEvent logEvent)
    {
        // The director kicks special infected bots when their death animation ends, constantly
        if (text.Contains("Kicked by Console : death anim finished", StringComparison.Ordinal))
        {
            return;
        }

        var lower = text.ToLowerInvariant();
        if (lower.Contains("vac ban") || lower.Contains("was banned"))
        {
            logEvent.Tags.Add("security");
            logEvent.Severity = LogSeverity.Warning;
        }

        if (lower.Contains("cheat") || lower.Contains("exploit"))
        {
            logEvent.Tags.Add("suspicious");
            logEvent.Severity = LogSeverity.Warning;
        }

        if (lower.Contains("kicked by") || lower.Contains("was banned"))
        {
            logEvent.Tags.Add("admin_action");
        }

        if (lower.Contains("overflow") || lower.Contains("timed out"))
        {
            logEvent.Tags.Add("performance");
        }

        if (lower.Contains("error") || lower.Contains("failed") || lower.Contains("exception"))
        {
            logEvent.Tags.Add("error");
            if (logEvent.Severity < LogSeverity.Error)
            {
                logEvent.Severity = LogSeverity.Error;
            }
        }
    }
}
