using System.Text.RegularExpressions;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Detection;

/// <summary>
/// Hackwatch: Detects potential cheating, exploits, and suspicious patterns
/// </summary>
public class HackwatchDetector : IDetectionSuite, IHackwatchDetector
{
    private readonly ILogger<HackwatchDetector> _logger;

    // Cheat-related keywords and patterns
    private readonly Dictionary<string, CheatIndicator> _cheatIndicators = new()
    {
        ["vac_ban"] = new CheatIndicator
        {
            Pattern = @"\b(VAC|vac)\s*(ban|banned|secure)",
            Severity = 10,
            Type = "VAC Ban"
        },
        ["aimbot"] = new CheatIndicator
        {
            Pattern = @"\b(aim|aim\s*bot|aimbot|aim\s*hack|auto\s*aim)\b",
            Severity = 8,
            Type = "Aimbot"
        },
        ["wallhack"] = new CheatIndicator
        {
            Pattern = @"\b(wall|wall\s*hack|wallhack|esp|see\s*through)\b",
            Severity = 8,
            Type = "Wallhack"
        },
        ["speedhack"] = new CheatIndicator
        {
            Pattern = @"\b(speed|speed\s*hack|speedhack|fast\s*move|teleport)\b",
            Severity = 7,
            Type = "Speedhack"
        },
        ["exploit"] = new CheatIndicator
        {
            Pattern = @"\b(exploit|glitch|bug\s*abuse|dupe|duplicate)\b",
            Severity = 6,
            Type = "Exploit"
        },
        ["impossible_stats"] = new CheatIndicator
        {
            Pattern = @"headshot.*(\d{3,}%|100%)|accuracy.*(\d{3,}%|100%)",
            Severity = 9,
            Type = "Impossible Statistics"
        },
        ["cheat_software"] = new CheatIndicator
        {
            Pattern = @"\b(cheat\s*engine|trainer|injector|bypass|crack)\b",
            Severity = 9,
            Type = "Cheat Software"
        },
        ["rate_manipulation"] = new CheatIndicator
        {
            Pattern = @"rate|interp|cmdrate|updaterate|speedhack|lag\s*switch",
            Severity = 5,
            Type = "Rate Manipulation"
        }
    };

    // Suspicious event patterns
    private readonly HashSet<string> _suspiciousEvents = new()
    {
        "kicked", "banned", "violation", "detection", "suspicious",
        "impossible", "overflow", "flood", "spam", "abuse"
    };

    public HackwatchDetector(ILogger<HackwatchDetector> logger)
    {
        _logger = logger;
    }

    public string Name => "hackwatch";
    public string Description => "Detects potential cheating, exploits, and suspicious patterns";

    public async Task<DetectionReport> AnalyzeAsync(
        IEnumerable<LogEvent> events,
        DetectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var report = new DetectionReport
        {
            SuiteName = Name,
            GeneratedAt = DateTime.UtcNow
        };

        var playerProfiles = new Dictionary<string, PlayerSuspicionProfile>();
        var eventsList = events.ToList();
        report.EventsAnalyzed = eventsList.Count;

        // First pass: Build player profiles and detect obvious cheats
        foreach (var evt in eventsList)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var playerId = evt.GetFieldString("SteamId") ??
                          evt.GetFieldString("PlayerName") ??
                          "Unknown";

            if (playerId == "Unknown") continue;

            if (!playerProfiles.ContainsKey(playerId))
            {
                playerProfiles[playerId] = new PlayerSuspicionProfile
                {
                    PlayerId = playerId,
                    PlayerName = evt.GetFieldString("PlayerName") ?? playerId
                };
            }

            var profile = playerProfiles[playerId];
            profile.Events.Add(evt);

            // Check for cheat indicators
            var cheatAnalysis = AnalyzeForCheats(evt);
            if (cheatAnalysis != null)
            {
                profile.SuspicionScore += cheatAnalysis.Severity;
                profile.Indicators.Add(cheatAnalysis);

                if (cheatAnalysis.Severity >= (int)options.MinSeverity)
                {
                    var detection = new Detection
                    {
                        Timestamp = evt.Timestamp,
                        Severity = CalculateSeverity(cheatAnalysis.Severity),
                        Type = cheatAnalysis.Type,
                        Description = $"Potential {cheatAnalysis.Type} detected for player {profile.PlayerName}",
                        ConfidenceScore = cheatAnalysis.Confidence,
                        Evidence = new Dictionary<string, object>
                        {
                            ["PlayerId"] = playerId,
                            ["PlayerName"] = profile.PlayerName,
                            ["IndicatorType"] = cheatAnalysis.Type,
                            ["Pattern"] = cheatAnalysis.MatchedPattern,
                            ["Context"] = evt.RawLine
                        },
                        RelatedEvents = new List<string> { evt.Id }
                    };

                    report.Detections.Add(detection);
                }
            }

            // Track statistics for anomaly detection
            TrackPlayerStatistics(evt, profile);
        }

        // Second pass: Anomaly detection
        foreach (var profile in playerProfiles.Values)
        {
            var anomalies = DetectAnomalies(profile, options.Threshold);
            foreach (var anomaly in anomalies)
            {
                if ((int)anomaly.Severity >= options.MinSeverity)
                {
                    report.Detections.Add(anomaly);
                }
            }
        }

        // Generate summary
        report.Summary = GenerateSummary(playerProfiles, report.Detections);

        _logger.LogInformation("Hackwatch completed: {Events} events analyzed, {Detections} detections",
            report.EventsAnalyzed, report.Detections.Count);

        return await Task.FromResult(report);
    }

    private CheatAnalysis? AnalyzeForCheats(LogEvent evt)
    {
        var text = evt.RawLine.ToLowerInvariant();

        foreach (var indicator in _cheatIndicators)
        {
            if (Regex.IsMatch(text, indicator.Value.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                var analysis = new CheatAnalysis
                {
                    EventId = evt.Id,
                    Timestamp = evt.Timestamp,
                    Type = indicator.Value.Type,
                    Severity = indicator.Value.Severity,
                    MatchedPattern = indicator.Key,
                    Confidence = CalculateConfidence(indicator.Value.Severity, evt)
                };

                // Boost confidence for certain tags
                if (evt.Tags.Contains("security") || evt.Tags.Contains("vac"))
                {
                    analysis.Confidence = Math.Min(0.95, analysis.Confidence + 0.2);
                }

                return analysis;
            }
        }

        // Check for suspicious events
        foreach (var suspicious in _suspiciousEvents)
        {
            if (text.Contains(suspicious))
            {
                return new CheatAnalysis
                {
                    EventId = evt.Id,
                    Timestamp = evt.Timestamp,
                    Type = "Suspicious Activity",
                    Severity = 3,
                    MatchedPattern = suspicious,
                    Confidence = 0.4
                };
            }
        }

        return null;
    }

    private void TrackPlayerStatistics(LogEvent evt, PlayerSuspicionProfile profile)
    {
        // Track kills
        if (evt.Tags.Contains("kill"))
        {
            profile.Stats.Kills++;

            // Check for headshot — look at Headshot field, headshot tag, or DamageType, not the Weapon field
            var isHeadshot = evt.Tags.Contains("headshot")
                || evt.GetFieldString("Headshot") != null
                || string.Equals(evt.GetFieldString("HitGroup"), "head", StringComparison.OrdinalIgnoreCase)
                || (evt.RawLine.Contains("headshot", StringComparison.OrdinalIgnoreCase));
            if (isHeadshot)
            {
                profile.Stats.Headshots++;
            }
        }

        // Track deaths
        var victim = evt.GetFieldString("Victim");
        if (victim == profile.PlayerName)
        {
            profile.Stats.Deaths++;
        }

        // Track connection info
        if (evt.Tags.Contains("player_connect"))
        {
            profile.Stats.ConnectionCount++;
            profile.Stats.FirstSeen = evt.Timestamp;
        }

        if (evt.Tags.Contains("player_disconnect"))
        {
            profile.Stats.DisconnectionCount++;
            profile.Stats.LastSeen = evt.Timestamp;
        }

        // Track commands
        var command = evt.GetFieldString("Command") ?? evt.GetFieldString("ExecutedCommand");
        if (command != null)
        {
            profile.Stats.CommandsExecuted++;
        }
    }

    private List<Detection> DetectAnomalies(PlayerSuspicionProfile profile, double threshold)
    {
        var anomalies = new List<Detection>();

        // Anomaly 1: Impossible K/D ratio
        if (profile.Stats.Deaths > 0)
        {
            var kd = (double)profile.Stats.Kills / profile.Stats.Deaths;
            if (kd > 10.0) // Suspiciously high K/D
            {
                anomalies.Add(CreateAnomalyDetection(
                    profile,
                    "Impossible K/D Ratio",
                    $"K/D ratio of {kd:F2} is statistically improbable",
                    Math.Min(0.95, 0.5 + (kd / 20.0))
                ));
            }
        }

        // Anomaly 2: Impossible headshot percentage
        if (profile.Stats.Kills > 10)
        {
            var headshotPercent = (double)profile.Stats.Headshots / profile.Stats.Kills * 100;
            if (headshotPercent > 80) // More than 80% headshots is suspicious
            {
                anomalies.Add(CreateAnomalyDetection(
                    profile,
                    "Impossible Headshot Rate",
                    $"Headshot percentage of {headshotPercent:F1}% exceeds normal bounds",
                    Math.Min(0.95, 0.6 + (headshotPercent - 80) / 40.0)
                ));
            }
        }

        // Anomaly 3: Rapid reconnections (possible ban evasion)
        if (profile.Stats.ConnectionCount > 5 && profile.Stats.FirstSeen != default && profile.Stats.LastSeen != default)
        {
            var timeSpan = profile.Stats.LastSeen - profile.Stats.FirstSeen;
            if (timeSpan.TotalMinutes > 0)
            {
                // +1 is intentional: avoids division by zero when TotalHours < 1 and
                // provides a conservative rate estimate for sub-hour time spans.
                var reconnectsPerHour = profile.Stats.ConnectionCount / (timeSpan.TotalHours + 1);
                if (reconnectsPerHour > 10)
                {
                    anomalies.Add(CreateAnomalyDetection(
                        profile,
                        "Rapid Reconnections",
                        $"Player reconnected {profile.Stats.ConnectionCount} times in {timeSpan.TotalMinutes:F0} minutes",
                        0.7
                    ));
                }
            }
        }

        // Anomaly 4: Command spam (possible exploit attempts)
        if (profile.Stats.CommandsExecuted > 100)
        {
            anomalies.Add(CreateAnomalyDetection(
                profile,
                "Command Flooding",
                $"Executed {profile.Stats.CommandsExecuted} commands - possible exploit attempt",
                0.6
            ));
        }

        return anomalies;
    }

    private Detection CreateAnomalyDetection(
        PlayerSuspicionProfile profile,
        string type,
        string description,
        double confidence)
    {
        return new Detection
        {
            Timestamp = DateTime.UtcNow,
            Severity = CalculateSeverity(profile.SuspicionScore),
            Type = type,
            Description = $"{description} for player {profile.PlayerName}",
            ConfidenceScore = confidence,
            Evidence = new Dictionary<string, object>
            {
                ["PlayerId"] = profile.PlayerId,
                ["PlayerName"] = profile.PlayerName,
                ["Kills"] = profile.Stats.Kills,
                ["Deaths"] = profile.Stats.Deaths,
                ["Headshots"] = profile.Stats.Headshots,
                ["SuspicionScore"] = profile.SuspicionScore
            },
            RelatedEvents = profile.Events.Select(e => e.Id).ToList()
        };
    }

    private double CalculateConfidence(int severity, LogEvent evt)
    {
        var baseConfidence = severity / 10.0 * 0.5 + 0.3;

        // Adjust based on event metadata
        if (evt.Severity >= LogSeverity.Warning)
            baseConfidence += 0.1;

        if (evt.Tags.Contains("security") || evt.Tags.Contains("admin_action"))
            baseConfidence += 0.15;

        return Math.Min(0.95, baseConfidence);
    }

    private DetectionSeverity CalculateSeverity(double score)
    {
        return score switch
        {
            >= 20 => DetectionSeverity.Critical,
            >= 15 => DetectionSeverity.High,
            >= 8 => DetectionSeverity.Medium,
            _ => DetectionSeverity.Low
        };
    }

    private Dictionary<string, object> GenerateSummary(
        Dictionary<string, PlayerSuspicionProfile> profiles,
        List<Detection> detections)
    {
        var summary = new Dictionary<string, object>
        {
            ["TotalPlayersAnalyzed"] = profiles.Count,
            ["SuspiciousPlayers"] = profiles.Count(p => p.Value.SuspicionScore > 5),
            ["TotalDetections"] = detections.Count,
            ["CriticalDetections"] = detections.Count(d => d.Severity == DetectionSeverity.Critical)
        };

        // Most suspicious players
        var topSuspicious = profiles
            .OrderByDescending(p => p.Value.SuspicionScore)
            .Take(5)
            .Select(p => new
            {
                Player = p.Value.PlayerName,
                Score = p.Value.SuspicionScore,
                Indicators = p.Value.Indicators.Count,
                TopIndicator = p.Value.Indicators.FirstOrDefault()?.Type ?? "None"
            })
            .ToList();

        summary["TopSuspiciousPlayers"] = topSuspicious;

        // Detection type breakdown
        var detectionTypes = detections
            .GroupBy(d => d.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .OrderByDescending(t => t.Count)
            .ToList();

        summary["DetectionTypes"] = detectionTypes;

        return summary;
    }

    // Helper classes
    private class CheatIndicator
    {
        public string Pattern { get; set; } = "";
        public int Severity { get; set; }
        public string Type { get; set; } = "";
    }

    private class CheatAnalysis
    {
        public string EventId { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string Type { get; set; } = "";
        public int Severity { get; set; }
        public string MatchedPattern { get; set; } = "";
        public double Confidence { get; set; }
    }

    private class PlayerSuspicionProfile
    {
        public string PlayerId { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public double SuspicionScore { get; set; }
        public List<CheatAnalysis> Indicators { get; set; } = new();
        public List<LogEvent> Events { get; set; } = new();
        public PlayerStatistics Stats { get; set; } = new();
    }

    private class PlayerStatistics
    {
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Headshots { get; set; }
        public int ConnectionCount { get; set; }
        public int DisconnectionCount { get; set; }
        public int CommandsExecuted { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    // IHackwatchDetector implementation
    public async Task<DetectionResult?> AnalyzeAsync(LogEvent logEvent)
    {
        var options = new DetectionOptions { MinSeverity = 3 };
        var report = await AnalyzeAsync(new[] { logEvent }, options);

        if (report.Detections.Any())
        {
            var detection = report.Detections.First();
            return new DetectionResult
            {
                Id = Guid.Parse(detection.Id),
                Timestamp = detection.Timestamp,
                DetectionType = "Hackwatch",
                PlayerId = logEvent.GetFieldString("PlayerId") ?? logEvent.GetFieldString("SteamID"),
                PlayerName = logEvent.GetFieldString("PlayerName"),
                Server = logEvent.Host,
                Severity = detection.Severity,
                Description = detection.Description,
                Evidence = detection.Evidence.GetValueOrDefault("text")?.ToString(),
                ConfidenceScore = detection.ConfidenceScore,
                Metadata = detection.Evidence,
                SourceEvent = logEvent
            };
        }

        return null;
    }

    public async Task<List<DetectionResult>> AnalyzeBatchAsync(IEnumerable<LogEvent> events)
    {
        var results = new List<DetectionResult>();
        foreach (var evt in events)
        {
            var result = await AnalyzeAsync(evt);
            if (result != null)
            {
                results.Add(result);
            }
        }
        return results;
    }
}