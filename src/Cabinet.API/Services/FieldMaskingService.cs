using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cabinet.Core.Models;

namespace Cabinet.API.Services;

/// <summary>Applies field masking rules to protect PII at query time</summary>
public sealed class FieldMaskingService
{
    private readonly List<FieldMaskingRule> _rules = new();

    public void AddRule(FieldMaskingRule rule) => _rules.Add(rule);

    public IReadOnlyList<FieldMaskingRule> GetRules() => _rules.AsReadOnly();

    /// <summary>Whether anything is masked for this role.</summary>
    public bool AppliesTo(SystemRole viewerRole) => _rules.Any(rule => rule.Enabled && !rule.ExemptRoles.Contains(viewerRole));

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public Dictionary<string, string> MaskFields(Dictionary<string, string> fields, SystemRole viewerRole)
    {
        if (fields == null || _rules.Count == 0) return fields!;

        var result = new Dictionary<string, string>(fields);

        // Apply rules in priority order (highest first) for deterministic behavior
        foreach (var rule in _rules.Where(r => r.Enabled).OrderByDescending(r => r.Priority))
        {
            if (rule.ExemptRoles.Contains(viewerRole)) continue;

            foreach (var key in result.Keys.ToList())
            {
                if (!MatchesPattern(key, rule.FieldPattern)) continue;

                if (rule.Strategy == MaskingStrategy.Remove)
                {
                    result.Remove(key);
                    continue;
                }

                result[key] = rule.Strategy switch
                {
                    MaskingStrategy.Redact => rule.Replacement,
                    MaskingStrategy.Hash => HashValue(result[key]),
                    MaskingStrategy.Partial => PartialMask(result[key]),
                    MaskingStrategy.Pattern when rule.ValuePattern != null =>
                        SafeRegexReplace(result[key], rule.ValuePattern, rule.Replacement),
                    _ => rule.Replacement
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Masks an event for a role. The raw line usually holds the same values as the fields, so
    /// masking only the fields would hide nothing: each value masked in a field is masked in the
    /// line too. A rule with a value pattern also applies to the line directly, which catches
    /// values no parser extracted. Returns the same event if nothing applies.
    /// </summary>
    public LogEvent MaskEvent(LogEvent logEvent, SystemRole viewerRole)
    {
        var rules = _rules.Where(rule => rule.Enabled && !rule.ExemptRoles.Contains(viewerRole)).ToList();
        if (rules.Count == 0)
        {
            return logEvent;
        }

        var original = logEvent.Fields
            .Where(field => field.Value != null)
            .ToDictionary(field => field.Key, field => field.Value!.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var masked = MaskFields(original, viewerRole);

        var copy = new LogEvent
        {
            Id = logEvent.Id,
            Timestamp = logEvent.Timestamp,
            IndexTime = logEvent.IndexTime,
            SourceType = logEvent.SourceType,
            Host = logEvent.Host,
            Severity = logEvent.Severity,
            RawLine = logEvent.RawLine,
            SourceFile = logEvent.SourceFile,
            LineNumber = logEvent.LineNumber,
            ParserName = logEvent.ParserName,
            Index = logEvent.Index,
            Tags = logEvent.Tags,
        };

        foreach (var field in logEvent.Fields)
        {
            if (!original.TryGetValue(field.Key, out var value))
            {
                copy.Fields[field.Key] = field.Value;
                continue;
            }

            masked.TryGetValue(field.Key, out var replacement);
            if (replacement == value)
            {
                copy.Fields[field.Key] = field.Value;
                continue;
            }

            if (replacement != null)
            {
                copy.Fields[field.Key] = replacement;
            }

            if (value.Length > 0)
            {
                copy.RawLine = copy.RawLine.Replace(value, replacement ?? "***", StringComparison.Ordinal);
            }
        }

        foreach (var rule in rules.Where(rule => rule.ValuePattern != null).OrderByDescending(rule => rule.Priority))
        {
            copy.RawLine = SafeRegexReplace(copy.RawLine, rule.ValuePattern!, rule.Replacement);
        }

        return copy;
    }

    private static bool MatchesPattern(string fieldName, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.StartsWith('*') && pattern.EndsWith('*'))
            return fieldName.Contains(pattern[1..^1], StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith('*'))
            return fieldName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        if (pattern.StartsWith('*'))
            return fieldName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        return string.Equals(fieldName, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string HashValue(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    private static string SafeRegexReplace(string value, string pattern, string replacement)
    {
        try
        {
            return Regex.Replace(value, pattern, replacement, RegexOptions.None, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return replacement; // Fall back to full replacement on timeout
        }
    }

    private static string PartialMask(string value)
    {
        if (value.Length <= 4) return new string('*', value.Length);
        return string.Concat(value.AsSpan(0, 2), new string('*', value.Length - 4), value.AsSpan(value.Length - 2));
    }
}
