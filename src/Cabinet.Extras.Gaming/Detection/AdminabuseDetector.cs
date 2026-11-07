using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Detection;

/// <summary>
/// Adminabuse: Detects potential admin abuse patterns through statistical analysis
/// </summary>
public class AdminabuseDetector : IDetectionSuite, IAdminabuseDetector
{
    private readonly ILogger<AdminabuseDetector> _logger;

    // Admin action types and their normal usage thresholds
    private readonly Dictionary<string, AdminActionThreshold> _actionThresholds = new()
    {
        ["ban"] = new AdminActionThreshold { Normal = 2, Warning = 5, Critical = 10 },
        ["kick"] = new AdminActionThreshold { Normal = 5, Warning = 10, Critical = 20 },
        ["mute"] = new AdminActionThreshold { Normal = 3, Warning = 8, Critical = 15 },
        ["gag"] = new AdminActionThreshold { Normal = 3, Warning = 8, Critical = 15 },
        ["slap"] = new AdminActionThreshold { Normal = 10, Warning = 20, Critical = 40 },
        ["slay"] = new AdminActionThreshold { Normal = 5, Warning = 15, Critical = 30 },
        ["silence"] = new AdminActionThreshold { Normal = 2, Warning = 5, Critical = 10 }
    };

    public AdminabuseDetector(ILogger<AdminabuseDetector> logger)
    {
        _logger = logger;
    }

    public string Name => "adminabuse";
    public string Description => "Detects potential admin abuse patterns through statistical analysis";

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

        var adminProfiles = new Dictionary<string, AdminProfile>();
        var playerTargets = new Dictionary<string, PlayerTargetProfile>();
        var eventsList = events.ToList();
        report.EventsAnalyzed = eventsList.Count;

        // Filter and analyze admin action events
        var adminEvents = eventsList
            .Where(e => e.Tags.Contains("admin_action") ||
                       e.Fields.ContainsKey("AdminAction") ||
                       e.Fields.ContainsKey("Admin"))
            .OrderBy(e => e.Timestamp)
            .ToList();

        if (!adminEvents.Any())
        {
            _logger.LogInformation("No admin actions found in the analyzed events");
            report.Summary["Message"] = "No admin actions detected in the analyzed time period";
            return report;
        }

        // Build admin profiles
        foreach (var evt in adminEvents)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var adminName = evt.GetFieldString("Admin") ?? "Unknown";
            var action = evt.GetFieldString("AdminAction") ??
                        evt.GetFieldString("Action") ??
                        "unknown";
            var target = evt.GetFieldString("Target") ??
                        evt.GetFieldString("TargetName") ??
                        evt.GetFieldString("Victim") ??
                        "Unknown";
            var reason = evt.GetFieldString("Reason");

            if (!adminProfiles.ContainsKey(adminName))
            {
                adminProfiles[adminName] = new AdminProfile
                {
                    AdminName = adminName,
                    FirstAction = evt.Timestamp,
                    LastAction = evt.Timestamp
                };
            }

            var profile = adminProfiles[adminName];
            profile.LastAction = evt.Timestamp;
            profile.TotalActions++;

            // Track action types
            if (!profile.ActionCounts.ContainsKey(action))
                profile.ActionCounts[action] = 0;
            profile.ActionCounts[action]++;

            // Track targets
            if (!profile.TargetCounts.ContainsKey(target))
                profile.TargetCounts[target] = 0;
            profile.TargetCounts[target]++;

            // Track reasons
            if (string.IsNullOrWhiteSpace(reason))
            {
                profile.ActionsWithoutReason++;
            }
            else
            {
                profile.Reasons.Add(reason);
            }

            profile.Events.Add(evt);

            // Track player targeting
            if (!playerTargets.ContainsKey(target))
            {
                playerTargets[target] = new PlayerTargetProfile { PlayerName = target };
            }
            playerTargets[target].TimesTargeted++;
            playerTargets[target].TargetedBy.Add(adminName);
        }

        // Analyze for abuse patterns
        foreach (var profile in adminProfiles.Values)
        {
            var abuseIndicators = AnalyzeAdminBehavior(profile, options);

            foreach (var indicator in abuseIndicators)
            {
                if ((int)indicator.Severity >= options.MinSeverity)
                {
                    report.Detections.Add(indicator);
                }
            }
        }

        // Check for targeted harassment
        foreach (var target in playerTargets.Values)
        {
            if (target.TimesTargeted > 5)
            {
                var mostFrequentAdmin = target.TargetedBy
                    .GroupBy(a => a)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault();

                if (mostFrequentAdmin != null && mostFrequentAdmin.Count() > 3)
                {
                    var detection = new Detection
                    {
                        Timestamp = DateTime.UtcNow,
                        Severity = DetectionSeverity.High,
                        Type = "Targeted Harassment",
                        Description = $"Player {target.PlayerName} was repeatedly targeted by admin {mostFrequentAdmin.Key} ({mostFrequentAdmin.Count()} times)",
                        ConfidenceScore = 0.8,
                        Evidence = new Dictionary<string, object>
                        {
                            ["Target"] = target.PlayerName,
                            ["Admin"] = mostFrequentAdmin.Key,
                            ["TargetCount"] = mostFrequentAdmin.Count(),
                            ["TotalTargetedCount"] = target.TimesTargeted
                        }
                    };

                    report.Detections.Add(detection);
                }
            }
        }

        // Generate summary
        report.Summary = GenerateSummary(adminProfiles, playerTargets, report.Detections);

        _logger.LogInformation("Adminabuse analysis completed: {Admins} admins analyzed, {Detections} detections",
            adminProfiles.Count, report.Detections.Count);

        return await Task.FromResult(report);
    }

    private List<Detection> AnalyzeAdminBehavior(AdminProfile profile, DetectionOptions options)
    {
        var detections = new List<Detection>();

        // Guard: if no actions recorded, nothing to analyze (avoids division by zero)
        if (profile.TotalActions == 0)
            return detections;

        var timeSpan = profile.LastAction - profile.FirstAction;
        var hoursActive = Math.Max(1, timeSpan.TotalHours);

        // Calculate actions per hour
        var actionsPerHour = profile.TotalActions / hoursActive;

        // Check 1: Excessive action rate
        var actionThreshold = options.CustomSettings.TryGetValue("ActionsPerHourThreshold", out var val) && val is int intVal ? intVal : 20;
        if (actionsPerHour > actionThreshold)
        {
            detections.Add(new Detection
            {
                Timestamp = profile.LastAction,
                Severity = actionsPerHour > 50 ? DetectionSeverity.Critical : DetectionSeverity.High,
                Type = "Excessive Admin Actions",
                Description = $"Admin {profile.AdminName} performed {actionsPerHour:F1} actions per hour (threshold: 20)",
                ConfidenceScore = Math.Min(0.95, 0.5 + (actionsPerHour / 100)),
                Evidence = new Dictionary<string, object>
                {
                    ["Admin"] = profile.AdminName,
                    ["ActionsPerHour"] = actionsPerHour,
                    ["TotalActions"] = profile.TotalActions,
                    ["TimeSpan"] = timeSpan.ToString()
                },
                RelatedEvents = profile.Events.Select(e => e.Id).Take(10).ToList()
            });
        }

        // Check 2: Actions without reasons
        var noReasonPercent = (double)profile.ActionsWithoutReason / profile.TotalActions * 100;
        if (noReasonPercent > 50 && profile.TotalActions > 5)
        {
            detections.Add(new Detection
            {
                Timestamp = profile.LastAction,
                Severity = noReasonPercent > 80 ? DetectionSeverity.High : DetectionSeverity.Medium,
                Type = "Actions Without Justification",
                Description = $"Admin {profile.AdminName} performed {noReasonPercent:F1}% of actions without providing reasons",
                ConfidenceScore = 0.7,
                Evidence = new Dictionary<string, object>
                {
                    ["Admin"] = profile.AdminName,
                    ["ActionsWithoutReason"] = profile.ActionsWithoutReason,
                    ["TotalActions"] = profile.TotalActions,
                    ["Percentage"] = noReasonPercent
                }
            });
        }

        // Check 3: Repeated targeting
        var mostTargeted = profile.TargetCounts
            .OrderByDescending(t => t.Value)
            .FirstOrDefault();

        if (mostTargeted.Value > 5)
        {
            var targetPercent = (double)mostTargeted.Value / profile.TotalActions * 100;
            if (targetPercent > 30)
            {
                detections.Add(new Detection
                {
                    Timestamp = profile.LastAction,
                    Severity = DetectionSeverity.High,
                    Type = "Repeated Player Targeting",
                    Description = $"Admin {profile.AdminName} repeatedly targeted player {mostTargeted.Key} ({mostTargeted.Value} times, {targetPercent:F1}% of all actions)",
                    ConfidenceScore = 0.85,
                    Evidence = new Dictionary<string, object>
                    {
                        ["Admin"] = profile.AdminName,
                        ["Target"] = mostTargeted.Key,
                        ["TargetCount"] = mostTargeted.Value,
                        ["Percentage"] = targetPercent
                    }
                });
            }
        }

        // Check 4: Severity escalation pattern
        var severityPattern = AnalyzeSeverityEscalation(profile);
        if (severityPattern != null)
        {
            detections.Add(severityPattern);
        }

        // Check 5: Unusual action distribution
        foreach (var action in profile.ActionCounts)
        {
            if (_actionThresholds.TryGetValue(action.Key.ToLower(), out var threshold))
            {
                var actionRate = action.Value / hoursActive;
                if (actionRate > threshold.Critical)
                {
                    detections.Add(new Detection
                    {
                        Timestamp = profile.LastAction,
                        Severity = DetectionSeverity.High,
                        Type = $"Excessive {action.Key} Usage",
                        Description = $"Admin {profile.AdminName} used '{action.Key}' {actionRate:F1} times per hour (critical threshold: {threshold.Critical})",
                        ConfidenceScore = 0.75,
                        Evidence = new Dictionary<string, object>
                        {
                            ["Admin"] = profile.AdminName,
                            ["Action"] = action.Key,
                            ["Count"] = action.Value,
                            ["RatePerHour"] = actionRate
                        }
                    });
                }
            }
        }

        return detections;
    }

    private Detection? AnalyzeSeverityEscalation(AdminProfile profile)
    {
        // Check if admin escalates punishments unnecessarily
        var severityOrder = new[] { "warn", "mute", "gag", "kick", "ban" };
        var escalationCount = 0;
        string? lastAction = null;

        foreach (var evt in profile.Events.OrderBy(e => e.Timestamp))
        {
            var action = evt.GetFieldString("AdminAction")?.ToLower();
            if (action != null && severityOrder.Contains(action))
            {
                if (lastAction != null)
                {
                    var lastIndex = Array.IndexOf(severityOrder, lastAction);
                    var currentIndex = Array.IndexOf(severityOrder, action);
                    if (currentIndex > lastIndex + 1) // Skipped severity levels
                    {
                        escalationCount++;
                    }
                }
                lastAction = action;
            }
        }

        if (escalationCount > 3)
        {
            return new Detection
            {
                Timestamp = profile.LastAction,
                Severity = DetectionSeverity.Medium,
                Type = "Severity Escalation Pattern",
                Description = $"Admin {profile.AdminName} shows pattern of escalating punishments without intermediate steps",
                ConfidenceScore = 0.6,
                Evidence = new Dictionary<string, object>
                {
                    ["Admin"] = profile.AdminName,
                    ["EscalationCount"] = escalationCount
                }
            };
        }

        return null;
    }

    private Dictionary<string, object> GenerateSummary(
        Dictionary<string, AdminProfile> adminProfiles,
        Dictionary<string, PlayerTargetProfile> playerTargets,
        List<Detection> detections)
    {
        var summary = new Dictionary<string, object>
        {
            ["TotalAdmins"] = adminProfiles.Count,
            ["TotalActions"] = adminProfiles.Sum(a => a.Value.TotalActions),
            ["SuspiciousAdmins"] = detections.Select(d => d.Evidence.GetValueOrDefault("Admin")).Distinct().Count(),
            ["TotalDetections"] = detections.Count
        };

        // Most active admins
        var topAdmins = adminProfiles
            .OrderByDescending(a => a.Value.TotalActions)
            .Take(5)
            .Select(a => new
            {
                Admin = a.Key,
                Actions = a.Value.TotalActions,
                MostUsedAction = a.Value.ActionCounts.OrderByDescending(ac => ac.Value).FirstOrDefault().Key,
                NoReasonPercent = a.Value.TotalActions > 0
                    ? (double)a.Value.ActionsWithoutReason / a.Value.TotalActions * 100
                    : 0.0
            })
            .ToList();

        summary["TopAdmins"] = topAdmins;

        // Most targeted players
        var topTargets = playerTargets
            .OrderByDescending(t => t.Value.TimesTargeted)
            .Take(5)
            .Select(t => new
            {
                Player = t.Key,
                TimesTargeted = t.Value.TimesTargeted,
                UniqueAdmins = t.Value.TargetedBy.Distinct().Count()
            })
            .ToList();

        summary["MostTargetedPlayers"] = topTargets;

        // Action distribution
        var actionDistribution = adminProfiles
            .SelectMany(a => a.Value.ActionCounts)
            .GroupBy(ac => ac.Key)
            .Select(g => new { Action = g.Key, Count = g.Sum(x => x.Value) })
            .OrderByDescending(a => a.Count)
            .ToList();

        summary["ActionDistribution"] = actionDistribution;

        return summary;
    }

    // Helper classes
    private class AdminProfile
    {
        public string AdminName { get; set; } = "";
        public int TotalActions { get; set; }
        public Dictionary<string, int> ActionCounts { get; set; } = new();
        public Dictionary<string, int> TargetCounts { get; set; } = new();
        public int ActionsWithoutReason { get; set; }
        public List<string> Reasons { get; set; } = new();
        public DateTime FirstAction { get; set; }
        public DateTime LastAction { get; set; }
        public List<LogEvent> Events { get; set; } = new();
    }

    private class PlayerTargetProfile
    {
        public string PlayerName { get; set; } = "";
        public int TimesTargeted { get; set; }
        public List<string> TargetedBy { get; set; } = new();
    }

    private class AdminActionThreshold
    {
        public double Normal { get; set; }
        public double Warning { get; set; }
        public double Critical { get; set; }
    }

    // IAdminabuseDetector implementation
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
                DetectionType = "Adminabuse",
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