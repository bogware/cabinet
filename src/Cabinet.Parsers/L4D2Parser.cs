using System.Text.RegularExpressions;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Parsers;

/// <summary>
/// Parser for Left 4 Dead 2 server logs
/// </summary>
public partial class L4D2Parser : BaseLogParser
{
    public L4D2Parser(ILogger<L4D2Parser>? logger = null) : base(logger) { }

    public override string Name => "L4D2";
    public override string SourceType => SourceTypes.L4D2;

    [GeneratedRegex(@"^L\s+\d{2}/\d{2}/\d{4}")]
    private static partial Regex L4D2LogPattern();

    public override bool CanParse(string line)
    {
        // L4D2 logs typically start with "L MM/DD/YYYY"
        return L4D2LogPattern().IsMatch(line);
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        // Extract common L4D2 patterns
        ExtractL4D2Timestamp(line, logEvent);
        ExtractPlayerInfo(line, logEvent);
        ExtractMapInfo(line, logEvent);
        ExtractGameEvents(line, logEvent);
        ExtractServerEvents(line, logEvent);
        DetectImportantEvents(line, logEvent);
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
                    // The line carries no offset. Read it as UTC so every host produces the same instant.
                    logEvent.Timestamp = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to parse L4D2 timestamp: {Match}", match.Value);
                    logEvent.Timestamp = DateTime.UtcNow;
                }
            }
        }
    }

    private void ExtractPlayerInfo(string line, LogEvent logEvent)
    {
        // "PlayerName<123><STEAM_0:1:12345><Team>" pattern - team can be empty
        var playerPattern = @"""([^<]+)<(\d+)><([^>]+)><([^>]*)>""";
        var match = Regex.Match(line, playerPattern, RegexOptions.None, RegexTimeout);
        if (match.Success)
        {
            logEvent.SetField("PlayerName", match.Groups[1].Value);
            logEvent.SetField("PlayerId", match.Groups[2].Value);
            logEvent.SetField("SteamId", match.Groups[3].Value);
            if (!string.IsNullOrEmpty(match.Groups[4].Value))
            {
                logEvent.SetField("Team", match.Groups[4].Value);
            }
        }

        // Extract Steam ID independently
        var steamId = ExtractSteamId(line);
        if (steamId != null && !logEvent.Fields.ContainsKey("SteamId"))
        {
            logEvent.SetField("SteamId", steamId);
        }
    }

    private void ExtractMapInfo(string line, LogEvent logEvent)
    {
        // Map loading/started patterns
        if (line.Contains("Loading map"))
        {
            var match = Regex.Match(line, @"Loading map ""([^""]+)""");
            if (match.Success)
            {
                logEvent.SetField("Map", match.Groups[1].Value);
                logEvent.Tags.Add("map_load");
            }
        }
        else if (line.Contains("Started map"))
        {
            var match = Regex.Match(line, @"Started map ""([^""]+)""");
            if (match.Success)
            {
                logEvent.SetField("Map", match.Groups[1].Value);
                logEvent.Tags.Add("map_start");
            }
        }
    }

    private void ExtractGameEvents(string line, LogEvent logEvent)
    {
        // Player connected/disconnected
        if (line.Contains(" connected"))
        {
            var ip = ExtractIpAddress(line);
            if (ip != null)
            {
                logEvent.SetField("IpAddress", ip);
            }
            logEvent.Tags.Add("player_connect");
        }
        else if (line.Contains("disconnected"))
        {
            logEvent.Tags.Add("player_disconnect");

            // Extract disconnect reason
            var match = Regex.Match(line, @"disconnected \((.+?)\)");
            if (match.Success)
            {
                logEvent.SetField("DisconnectReason", match.Groups[1].Value);
            }
        }

        // Kill events
        if (line.Contains(" killed "))
        {
            var match = Regex.Match(line, @"""([^<]+)(?:<[^>]*>)*"" killed ""([^<]+)(?:<[^>]*>)*"" with ""([^""]+)""");
            if (match.Success)
            {
                logEvent.SetField("Killer", match.Groups[1].Value);
                logEvent.SetField("Victim", match.Groups[2].Value);
                logEvent.SetField("Weapon", match.Groups[3].Value);
                logEvent.Tags.Add("kill");
            }
        }

        // Chat messages
        if (line.Contains(" say ") || line.Contains(" say_team "))
        {
            var chatPattern = line.Contains(" say_team ") ? @"say_team ""(.+?)""" : @"say ""(.+?)""";
            var match = Regex.Match(line, chatPattern, RegexOptions.None, RegexTimeout);
            if (match.Success)
            {
                logEvent.SetField("ChatMessage", match.Groups[1].Value);
                logEvent.Tags.Add(line.Contains(" say_team ") ? "team_chat" : "all_chat");
            }
        }

        // Vote events
        if (line.Contains("Vote"))
        {
            if (line.Contains("called vote"))
            {
                logEvent.Tags.Add("vote_called");
            }
            else if (line.Contains("Vote passed"))
            {
                logEvent.Tags.Add("vote_passed");
            }
            else if (line.Contains("Vote failed"))
            {
                logEvent.Tags.Add("vote_failed");
            }
        }
    }

    private void ExtractServerEvents(string line, LogEvent logEvent)
    {
        // Server cvars
        if (line.Contains("server_cvar:"))
        {
            var match = Regex.Match(line, @"server_cvar: ""([^""]+)"" ""([^""]+)""");
            if (match.Success)
            {
                logEvent.SetField("Cvar", match.Groups[1].Value);
                logEvent.SetField("CvarValue", match.Groups[2].Value);
                logEvent.Tags.Add("cvar_change");
            }
        }

        // Rcon commands
        if (line.Contains("rcon from"))
        {
            var ip = ExtractIpAddress(line);
            if (ip != null)
            {
                logEvent.SetField("RconSource", ip);
            }
            logEvent.Tags.Add("rcon");
            logEvent.Severity = LogSeverity.Warning; // Flag rcon usage
        }

        // Server restart/shutdown
        if (line.Contains("Server restart") || line.Contains("Server is hibernating"))
        {
            logEvent.Tags.Add("server_restart");
        }
    }

    private void DetectImportantEvents(string line, LogEvent logEvent)
    {
        var lower = line.ToLowerInvariant();

        // VAC/Ban related
        if (lower.Contains("vac") || lower.Contains("banned"))
        {
            logEvent.Tags.Add("security");
            logEvent.Severity = LogSeverity.Warning;
        }

        // Cheating/exploit indicators
        if (lower.Contains("cheat") || lower.Contains("exploit") || lower.Contains("hack"))
        {
            logEvent.Tags.Add("suspicious");
            logEvent.Severity = LogSeverity.Warning;
        }

        // Admin actions
        if (lower.Contains("admin") || lower.Contains("kicked") || lower.Contains("banned"))
        {
            logEvent.Tags.Add("admin_action");
        }

        // Performance issues
        if (lower.Contains("lag") || lower.Contains("timeout") || lower.Contains("overflow"))
        {
            logEvent.Tags.Add("performance");
        }

        // Errors
        if (lower.Contains("error") || lower.Contains("failed") || lower.Contains("exception"))
        {
            logEvent.Tags.Add("error");
            if (logEvent.Severity < LogSeverity.Error)
                logEvent.Severity = LogSeverity.Error;
        }
    }
}