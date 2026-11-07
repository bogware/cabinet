using System.Globalization;
using System.Text.RegularExpressions;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Extras.Gaming.Parsers;

/// <summary>
/// Parser for SteamCMD update and installation logs
/// </summary>
public partial class SteamCMDParser : GamingLogParser
{
    public SteamCMDParser(ILogger<SteamCMDParser>? logger = null) : base(logger) { }

    public override string Name => "SteamCMD";
    public override string SourceType => GamingSourceTypes.SteamCMD;

    [GeneratedRegex(@"^\[.*\]\s+(?:Downloading|Installing|Validating|Updating|Loading|Connecting)")]
    private static partial Regex SteamCMDPattern();

    public override bool CanParse(string line)
    {
        return SteamCMDPattern().IsMatch(line) ||
               line.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("app_update", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("workshop", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Update state", StringComparison.OrdinalIgnoreCase);
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        ExtractSteamTimestamp(line, logEvent);
        ExtractAppInfo(line, logEvent);
        ExtractDownloadProgress(line, logEvent);
        ExtractWorkshopInfo(line, logEvent);
        ExtractConnectionInfo(line, logEvent);
        DetectSteamCMDEvents(line, logEvent);
    }

    private void ExtractSteamTimestamp(string line, LogEvent logEvent)
    {
        // [2024-01-01 12:34:56] format
        var match = Regex.Match(line, @"\[(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})\]", RegexOptions.None, RegexTimeout);
        if (match.Success)
        {
            if (DateTime.TryParse(
                    match.Groups[1].Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var timestamp))
            {
                logEvent.Timestamp = timestamp;
            }
        }
    }

    private void ExtractAppInfo(string line, LogEvent logEvent)
    {
        // App ID extraction
        var appIdMatch = Regex.Match(line, @"app[_\s]+(?:id|ID|update)[\s:]+(\d+)", RegexOptions.None, RegexTimeout);
        if (appIdMatch.Success)
        {
            logEvent.SetField("AppId", appIdMatch.Groups[1].Value);
            logEvent.Tags.Add("app_update");
        }

        // App name extraction
        var appNameMatch = Regex.Match(line, @"Installing\s+'([^']+)'|Downloading\s+'([^']+)'", RegexOptions.None, RegexTimeout);
        if (appNameMatch.Success)
        {
            var appName = appNameMatch.Groups[1].Success ? appNameMatch.Groups[1].Value : appNameMatch.Groups[2].Value;
            logEvent.SetField("AppName", appName);
        }

        // Version/build ID
        var buildMatch = Regex.Match(line, @"build[_\s]+(?:id|ID)[\s:]+(\d+)", RegexOptions.None, RegexTimeout);
        if (buildMatch.Success)
        {
            logEvent.SetField("BuildId", buildMatch.Groups[1].Value);
        }
    }

    private void ExtractDownloadProgress(string line, LogEvent logEvent)
    {
        // Download progress: "Downloading update (57 of 123 MB)..."
        var progressMatch = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*(?:of|/)\s*(\d+(?:\.\d+)?)\s*(MB|GB|KB)", RegexOptions.None, RegexTimeout);
        if (progressMatch.Success)
        {
            logEvent.SetField("ProgressCurrent", progressMatch.Groups[1].Value);
            logEvent.SetField("ProgressTotal", progressMatch.Groups[2].Value);
            logEvent.SetField("ProgressUnit", progressMatch.Groups[3].Value);
            logEvent.Tags.Add("download_progress");
        }

        // Percentage progress
        var percentMatch = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*%", RegexOptions.None, RegexTimeout);
        if (percentMatch.Success)
        {
            logEvent.SetField("ProgressPercent", percentMatch.Groups[1].Value);
        }

        // Download speed
        var speedMatch = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*(MB/s|KB/s|GB/s)", RegexOptions.None, RegexTimeout);
        if (speedMatch.Success)
        {
            logEvent.SetField("DownloadSpeed", speedMatch.Groups[1].Value);
            logEvent.SetField("SpeedUnit", speedMatch.Groups[2].Value);
        }
    }

    private void ExtractWorkshopInfo(string line, LogEvent logEvent)
    {
        // Workshop item ID
        var workshopMatch = Regex.Match(line, @"workshop[_\s]+(?:item|download)[_\s]+(\d+)", RegexOptions.None, RegexTimeout);
        if (workshopMatch.Success)
        {
            logEvent.SetField("WorkshopId", workshopMatch.Groups[1].Value);
            logEvent.Tags.Add("workshop");
        }

        // Workshop collection
        var collectionMatch = Regex.Match(line, @"collection[_\s]+(\d+)", RegexOptions.None, RegexTimeout);
        if (collectionMatch.Success)
        {
            logEvent.SetField("CollectionId", collectionMatch.Groups[1].Value);
            logEvent.Tags.Add("workshop_collection");
        }
    }

    private void ExtractConnectionInfo(string line, LogEvent logEvent)
    {
        // Connection to Steam servers
        if (line.Contains("Connecting to", StringComparison.OrdinalIgnoreCase))
        {
            var serverMatch = Regex.Match(line, @"Connecting to\s+([^\s]+)", RegexOptions.None, RegexTimeout);
            if (serverMatch.Success)
            {
                logEvent.SetField("ConnectingTo", serverMatch.Groups[1].Value);
                logEvent.Tags.Add("connection");
            }
        }

        // Login information
        if (line.Contains("Logging in", StringComparison.OrdinalIgnoreCase))
        {
            var userMatch = Regex.Match(line, @"user\s+'([^']+)'", RegexOptions.None, RegexTimeout);
            if (userMatch.Success)
            {
                logEvent.SetField("Username", userMatch.Groups[1].Value);
            }
            logEvent.Tags.Add("login");
        }

        // Connection status
        if (line.Contains("Connected to Steam", StringComparison.OrdinalIgnoreCase))
        {
            logEvent.Tags.Add("connected");
        }
        else if (line.Contains("Connection failed", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            logEvent.Tags.Add("connection_failed");
            logEvent.Severity = LogSeverity.Error;
        }
    }

    private void DetectSteamCMDEvents(string line, LogEvent logEvent)
    {
        var lower = line.ToLowerInvariant();

        // Installation states
        if (lower.Contains("success"))
        {
            logEvent.Tags.Add("success");
        }
        else if (lower.Contains("failed") || lower.Contains("error"))
        {
            logEvent.Tags.Add("failure");
            if (logEvent.Severity < LogSeverity.Error)
                logEvent.Severity = LogSeverity.Error;
        }

        // Validation
        if (lower.Contains("validating"))
        {
            logEvent.Tags.Add("validation");
        }
        else if (lower.Contains("validation failed"))
        {
            logEvent.Tags.Add("validation_failed");
            logEvent.Severity = LogSeverity.Error;
        }

        // Update states
        if (lower.Contains("update complete"))
        {
            logEvent.Tags.Add("update_complete");
        }
        else if (lower.Contains("update required") || lower.Contains("update available"))
        {
            logEvent.Tags.Add("update_available");
        }
        else if (lower.Contains("already up to date") || lower.Contains("up-to-date"))
        {
            logEvent.Tags.Add("up_to_date");
        }

        // Disk space issues
        if (lower.Contains("disk space") || lower.Contains("not enough space"))
        {
            logEvent.Tags.Add("disk_space_issue");
            logEvent.Severity = LogSeverity.Error;
        }

        // Network issues
        if (lower.Contains("connection timeout") || lower.Contains("network error"))
        {
            logEvent.Tags.Add("network_issue");
            logEvent.Severity = LogSeverity.Error;
        }

        // Authentication
        if (lower.Contains("authentication") || lower.Contains("login failed"))
        {
            logEvent.Tags.Add("auth_issue");
            logEvent.Severity = LogSeverity.Error;
        }

        // Rate limiting
        if (lower.Contains("rate limit") || lower.Contains("too many requests"))
        {
            logEvent.Tags.Add("rate_limited");
            logEvent.Severity = LogSeverity.Warning;
        }
    }
}