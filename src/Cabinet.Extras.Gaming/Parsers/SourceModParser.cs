using System.Globalization;
using System.Text.RegularExpressions;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Extras.Gaming.Parsers;

/// <summary>
/// Parser for SourceMod admin logs
/// </summary>
public partial class SourceModParser : GamingLogParser
{
    public SourceModParser(ILogger<SourceModParser>? logger = null) : base(logger) { }

    public override string Name => "SourceMod";
    public override string SourceType => GamingSourceTypes.SourceMod;

    // "[" then "]" later on the first line. Each part stops at the character after it, so the
    // match is linear: ".*\[.*\]" took minutes on a line of brackets that never closed.
    [GeneratedRegex(@"^L\s+\d{2}/\d{2}/\d{4}[^\[\n]*\[[^\]\n]*\]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SourceModLogPattern();

    // Player pattern: "Name<id><steamid><team>". A backtracking engine retries "[^<]+" from every
    // quote, which is quadratic on a line of quotes, so this runs without backtracking.
    private static readonly Regex PlayerPattern = new(
        @"""([^<]+)<(\d+)><([^>]+)>(?:<([^>]+)>)?""",
        RegexOptions.NonBacktracking, RegexTimeout);

    public override bool CanParse(string line)
    {
        // SourceMod logs have patterns like: L MM/DD/YYYY - HH:MM:SS: [plugin.smx]
        if (line.Contains("[SM]") || line.Contains(".smx"))
        {
            return true;
        }

        try
        {
            return SourceModLogPattern().IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        ExtractSourceModTimestamp(line, logEvent);
        ExtractPluginInfo(line, logEvent);
        ExtractAdminActions(line, logEvent);
        ExtractPlayerActions(line, logEvent);
        ExtractCommands(line, logEvent);
        DetectSourceModEvents(line, logEvent);
    }

    private void ExtractSourceModTimestamp(string line, LogEvent logEvent)
    {
        // L MM/DD/YYYY - HH:MM:SS:
        var match = Regex.Match(line, @"L\s+(\d{2}/\d{2}/\d{4}\s+-\s+\d{2}:\d{2}:\d{2}):", RegexOptions.None, RegexTimeout);
        if (match.Success)
        {
            if (DateTime.TryParseExact(
                    Regex.Replace(match.Groups[1].Value, @"\s+", " ", RegexOptions.None, RegexTimeout),
                    "MM/dd/yyyy - HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    // Local time with no offset, read in the sender's time zone (see LogLineParser)
                    DateTimeStyles.None,
                    out var timestamp))
            {
                logEvent.Timestamp = timestamp;
            }
        }
    }

    private void ExtractPluginInfo(string line, LogEvent logEvent)
    {
        // Extract plugin name [plugin.smx]
        var match = Regex.Match(line, @"\[([^\]]+\.smx)\]", RegexOptions.None, RegexTimeout);
        if (match.Success)
        {
            logEvent.SetField("Plugin", match.Groups[1].Value);
            logEvent.Tags.Add("plugin");
        }

        // [SM] prefix
        if (line.Contains("[SM]"))
        {
            logEvent.Tags.Add("sourcemod_core");
        }

        // "[SM] Blaming: dir/plugin.smx" names the plugin behind the exception before it
        var blaming = Regex.Match(line, @"\[SM\] Blaming: (\S+\.smx)", RegexOptions.None, RegexTimeout);
        if (blaming.Success)
        {
            logEvent.SetField("Plugin", blaming.Groups[1].Value);
            logEvent.Tags.Add("plugin_error");
            logEvent.Severity = LogSeverity.Error;
        }
    }

    private void ExtractAdminActions(string line, LogEvent logEvent)
    {
        // Admin commands: ban, kick, mute, gag, etc.
        var adminActions = new Dictionary<string, string>
        {
            { "banned", "ban" },
            { "kicked", "kick" },
            { "slapped", "slap" },
            { "slayed", "slay" },
            { "muted", "mute" },
            { "gagged", "gag" },
            { "silenced", "silence" }
        };

        foreach (var action in adminActions)
        {
            if (line.Contains(action.Key, StringComparison.OrdinalIgnoreCase))
            {
                logEvent.Tags.Add("admin_action");
                logEvent.SetField("AdminAction", action.Value);

                // Extract admin name
                var adminPattern = @"""([^""]+)"".*" + Regex.Escape(action.Key);
                var match = Regex.Match(line, adminPattern, RegexOptions.None, RegexTimeout);
                if (match.Success)
                {
                    logEvent.SetField("Admin", match.Groups[1].Value);
                }

                // Extract target
                var targetPattern = action.Key + @".*""([^""]+)""";
                match = Regex.Match(line, targetPattern, RegexOptions.None, RegexTimeout);
                if (match.Success)
                {
                    logEvent.SetField("Target", match.Groups[1].Value);
                }

                // Extract reason if present
                var reasonPattern = @"reason:\s*""([^""]+)""|\(([^)]+)\)$";
                match = Regex.Match(line, reasonPattern, RegexOptions.None, RegexTimeout);
                if (match.Success)
                {
                    var reason = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    logEvent.SetField("Reason", reason);
                }

                // Extract duration for bans
                if (action.Value == "ban")
                {
                    var durationPattern = @"(\d+)\s*(?:minutes?|mins?|hours?|days?|permanently)";
                    match = Regex.Match(line, durationPattern, RegexOptions.IgnoreCase, RegexTimeout);
                    if (match.Success)
                    {
                        logEvent.SetField("Duration", match.Value);
                    }
                }

                break;
            }
        }

        // RCON command executed
        if (line.Contains("rcon", StringComparison.OrdinalIgnoreCase))
        {
            logEvent.Tags.Add("rcon");
            logEvent.Severity = LogSeverity.Warning;
        }
    }

    private static bool IsSteamId(string value) =>
        value.StartsWith("STEAM_", StringComparison.Ordinal) || value.StartsWith("[U:", StringComparison.Ordinal);

    private void ExtractPlayerActions(string line, LogEvent logEvent)
    {
        var matches = PlayerPattern.Matches(line);

        if (matches.Count > 0)
        {
            var firstMatch = matches[0];
            logEvent.SetField("PlayerName", firstMatch.Groups[1].Value);
            logEvent.SetField("PlayerId", firstMatch.Groups[2].Value);

            // The server console and bots have no Steam ID
            if (IsSteamId(firstMatch.Groups[3].Value))
            {
                logEvent.SetField("SteamId", firstMatch.Groups[3].Value);
            }
            if (firstMatch.Groups[4].Success)
            {
                logEvent.SetField("Team", firstMatch.Groups[4].Value);
            }

            // If there's a second player (target of action)
            if (matches.Count > 1)
            {
                var secondMatch = matches[1];
                logEvent.SetField("TargetName", secondMatch.Groups[1].Value);
                if (IsSteamId(secondMatch.Groups[3].Value))
                {
                    logEvent.SetField("TargetSteamId", secondMatch.Groups[3].Value);
                }
            }
        }

        // Vote events
        if (line.Contains("started a vote", StringComparison.OrdinalIgnoreCase))
        {
            logEvent.Tags.Add("vote_start");
        }
        else if (line.Contains("vote passed", StringComparison.OrdinalIgnoreCase))
        {
            logEvent.Tags.Add("vote_passed");
        }
        else if (line.Contains("vote failed", StringComparison.OrdinalIgnoreCase))
        {
            logEvent.Tags.Add("vote_failed");
        }
    }

    private void ExtractCommands(string line, LogEvent logEvent)
    {
        // sm_command usage
        var cmdPattern = @"sm_(\w+)";
        var match = Regex.Match(line, cmdPattern, RegexOptions.None, RegexTimeout);
        if (match.Success)
        {
            logEvent.SetField("Command", match.Value);
            logEvent.Tags.Add("command");
        }

        // Console command
        if (line.Contains("executed command:", StringComparison.OrdinalIgnoreCase))
        {
            var execPattern = @"executed command:\s*""?([^""]+)""?";
            match = Regex.Match(line, execPattern, RegexOptions.None, RegexTimeout);
            if (match.Success)
            {
                logEvent.SetField("ExecutedCommand", match.Groups[1].Value);
                logEvent.Tags.Add("command_executed");
            }
        }
    }

    private void DetectSourceModEvents(string line, LogEvent logEvent)
    {
        var lower = line.ToLowerInvariant();

        // Plugin errors
        if (lower.Contains("error") || lower.Contains("exception") || lower.Contains("failed to load"))
        {
            logEvent.Tags.Add("plugin_error");
            if (logEvent.Severity < LogSeverity.Error)
                logEvent.Severity = LogSeverity.Error;
        }

        // Database issues
        if (lower.Contains("database") || lower.Contains("mysql") || lower.Contains("sqlite"))
        {
            if (lower.Contains("error") || lower.Contains("failed"))
            {
                logEvent.Tags.Add("database_error");
                logEvent.Severity = LogSeverity.Error;
            }
        }

        // Permission issues
        if (lower.Contains("access denied") || lower.Contains("no access") || lower.Contains("permission"))
        {
            logEvent.Tags.Add("permission_issue");
        }

        // Anti-cheat detections
        if (lower.Contains("aimbot") || lower.Contains("wallhack") || lower.Contains("speedhack") ||
            lower.Contains("cheat") || lower.Contains("hack detected"))
        {
            logEvent.Tags.Add("cheat_detection");
            logEvent.Severity = LogSeverity.Warning;
        }

        // Admin abuse indicators
        if (lower.Contains("admin abuse") || lower.Contains("admin complaint"))
        {
            logEvent.Tags.Add("admin_abuse_report");
            logEvent.Severity = LogSeverity.Warning;
        }

        // Map changes
        if (lower.Contains("changing map") || lower.Contains("loading map"))
        {
            logEvent.Tags.Add("map_change");
        }
    }
}