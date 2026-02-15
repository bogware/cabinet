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
