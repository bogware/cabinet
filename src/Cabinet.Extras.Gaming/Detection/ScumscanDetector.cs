using System.Text.RegularExpressions;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Extras.Gaming.Detection;

/// <summary>
/// Scumscan: Detects toxic behavior, harassment, and inappropriate content
/// </summary>
public class ScumscanDetector : IDetectionSuite, IScumscanDetector
{
    private readonly ILogger<ScumscanDetector> _logger;

    // Toxic behavior patterns (kept generic for safety)
    private readonly Dictionary<string, (string Pattern, int Weight)> _toxicPatterns = new()
    {
        ["hate_speech"] = (@"\b(hate|racist|sexist|homophobic)\b", 10),
        ["threats"] = (@"\b(kill|hurt|harm|destroy)\s+(you|u|your|ur)\b", 8),
        ["harassment"] = (@"\b(harass|stalk|bully|abuse)\b", 7),
        ["profanity"] = (@"\b(fuck|shit|bitch|asshole)\b", 5),
        ["spam"] = (@"(.)\1{10,50}", 3), // Repeated characters (bounded to prevent ReDoS)
        ["caps_abuse"] = (@"[A-Z\s]{20,200}", 2), // Excessive caps (bounded to prevent ReDoS)
        ["personal_attack"] = (@"\b(stupid|idiot|moron|loser|noob|trash)\b", 4),
        ["discrimination"] = (@"\b(discriminat|prejudice|bigot)\b", 8),
        ["griefing"] = (@"\b(grief|troll|ruin|sabotage)\b", 6),
        ["cheating_accusation"] = (@"\b(hacker|cheater|aimbot|wallhack)\b", 3)
    };

    // Contextual indicators
    private readonly HashSet<string> _negativeContext = new()
    {
        "report", "ban", "kick", "mute", "toxic", "admin", "complaint", "abuse"
    };

    // Positive indicators (reduce false positives)
    private readonly HashSet<string> _positiveContext = new()
    {
        "gg", "wp", "thanks", "nice", "good", "help", "team", "please", "sorry"
    };

    public ScumscanDetector(ILogger<ScumscanDetector> logger)
    {
        _logger = logger;
    }

    public string Name => "scumscan";
    public string Description => "Detects toxic behavior, harassment, and inappropriate content in game logs";

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

        var playerScores = new Dictionary<string, PlayerToxicityProfile>();
        var eventsList = events.ToList();
        report.EventsAnalyzed = eventsList.Count;

        foreach (var evt in eventsList)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var analysis = AnalyzeEvent(evt, options);
            if (analysis != null)
            {
                // Track by player
                var playerId = evt.GetFieldString("SteamId") ??
                              evt.GetFieldString("PlayerName") ??
                              "Unknown";

                if (!playerScores.ContainsKey(playerId))
                {
                    playerScores[playerId] = new PlayerToxicityProfile
                    {
                        PlayerId = playerId,
                        PlayerName = evt.GetFieldString("PlayerName") ?? playerId
                    };
                }

                var profile = playerScores[playerId];
                profile.TotalScore += analysis.Score;
                profile.ViolationCount++;
                profile.Violations.Add(analysis);

                // Create detection if severe enough
                if (analysis.Score >= options.MinSeverity)
                {
                    var detection = new Detection
                    {
                        Timestamp = evt.Timestamp,
                        Severity = CalculateSeverity(analysis.Score),
                        Type = "Toxic Behavior",
                        Description = $"Player {profile.PlayerName} exhibited toxic behavior: {analysis.Type}",
                        ConfidenceScore = analysis.Confidence,
                        Evidence = new Dictionary<string, object>
                        {
                            ["PlayerId"] = playerId,
                            ["PlayerName"] = profile.PlayerName,
                            ["ViolationType"] = analysis.Type ?? "Unknown",
                            ["Score"] = analysis.Score,
                            ["Context"] = analysis.Context,
                            ["RawLine"] = evt.RawLine
                        },
                        RelatedEvents = new List<string> { evt.Id }
                    };

                    report.Detections.Add(detection);
                }
            }
        }

        // Generate summary
        report.Summary = GenerateSummary(playerScores, report.Detections.Count);

        _logger.LogInformation("Scumscan completed: {Events} events analyzed, {Detections} detections",
            report.EventsAnalyzed, report.Detections.Count);

        return await Task.FromResult(report);
    }

    private ToxicityAnalysis? AnalyzeEvent(LogEvent evt, DetectionOptions options)
    {
        // Focus on chat messages and player actions
        if (!evt.Tags.Contains("chat") &&
            !evt.Tags.Contains("all_chat") &&
            !evt.Tags.Contains("team_chat") &&
            !evt.Fields.ContainsKey("ChatMessage"))
        {
            return null;
        }

        var text = evt.GetFieldString("ChatMessage") ?? evt.RawLine;
        var lowerText = text.ToLowerInvariant();

        var analysis = new ToxicityAnalysis
        {
            EventId = evt.Id,
            Timestamp = evt.Timestamp,
            Text = text
        };

        // Check toxic patterns
        foreach (var pattern in _toxicPatterns)
        {
            if (Regex.IsMatch(lowerText, pattern.Value.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                analysis.Score += pattern.Value.Weight;
                analysis.MatchedPatterns.Add(pattern.Key);

                if (analysis.Type == null || pattern.Value.Weight > 5)
                {
                    analysis.Type = pattern.Key.Replace("_", " ");
                }
            }
        }

        // Context analysis
        var contextScore = 0;
        foreach (var negative in _negativeContext)
        {
            if (lowerText.Contains(negative))
            {
                contextScore++;
            }
        }

        foreach (var positive in _positiveContext)
        {
            if (lowerText.Contains(positive))
            {
                contextScore--;
            }
        }

        analysis.Score += Math.Max(0, contextScore);

        // Calculate confidence based on pattern matches
        if (analysis.MatchedPatterns.Any())
        {
            analysis.Confidence = Math.Min(0.95, 0.5 + (analysis.MatchedPatterns.Count * 0.15));
        }

        // Extract context window
        if (analysis.Score > 0)
        {
            analysis.Context = ExtractContext(text, 50);
            return analysis;
        }

        return null;
    }

    private string ExtractContext(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text.Substring(0, maxLength) + "...";
    }

    private DetectionSeverity CalculateSeverity(double score)
    {
        return score switch
        {
            >= 15 => DetectionSeverity.Critical,
            >= 10 => DetectionSeverity.High,
            >= 5 => DetectionSeverity.Medium,
            _ => DetectionSeverity.Low
        };
    }

    private Dictionary<string, object> GenerateSummary(
        Dictionary<string, PlayerToxicityProfile> playerScores,
        int totalDetections)
    {
        var summary = new Dictionary<string, object>
        {
            ["TotalPlayersAnalyzed"] = playerScores.Count,
            ["TotalDetections"] = totalDetections,
            ["ToxicPlayers"] = playerScores.Count(p => p.Value.TotalScore > 5)
        };

        // Top toxic players
        var topToxic = playerScores
            .OrderByDescending(p => p.Value.TotalScore)
            .Take(5)
            .Select(p => new
            {
                Player = p.Value.PlayerName,
                Score = p.Value.TotalScore,
                Violations = p.Value.ViolationCount
            })
            .ToList();

        summary["TopToxicPlayers"] = topToxic;

        // Violation type breakdown
        var violationTypes = playerScores
            .SelectMany(p => p.Value.Violations)
            .GroupBy(v => v.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .OrderByDescending(v => v.Count)
            .ToList();

        summary["ViolationBreakdown"] = violationTypes;

        return summary;
    }

    // IScumscanDetector implementation
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
                DetectionType = "Scumscan",
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

    // Helper classes
    private class PlayerToxicityProfile
    {
        public string PlayerId { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public double TotalScore { get; set; }
        public int ViolationCount { get; set; }
        public List<ToxicityAnalysis> Violations { get; set; } = new();
    }

    private class ToxicityAnalysis
    {
        public string EventId { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string Text { get; set; } = "";
        public string? Type { get; set; }
        public double Score { get; set; }
        public double Confidence { get; set; }
        public List<string> MatchedPatterns { get; set; } = new();
        public string Context { get; set; } = "";
    }
}