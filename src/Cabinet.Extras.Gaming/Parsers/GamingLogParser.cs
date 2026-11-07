using System.Text.RegularExpressions;
using Cabinet.Parsers;
using Microsoft.Extensions.Logging;

namespace Cabinet.Extras.Gaming.Parsers;

/// <summary>
/// Base for parsers of game server logs, adding player field extraction.
/// </summary>
public abstract class GamingLogParser : BaseLogParser
{
    protected GamingLogParser(ILogger? logger = null)
        : base(logger)
    {
    }

    /// <summary>Extracts a Steam ID such as <c>STEAM_1:0:123</c> or <c>[U:1:123]</c>.</summary>
    protected string? ExtractSteamId(string text)
    {
        try
        {
            var match = Regex.Match(text, @"STEAM_[0-9]:[0-9]:[0-9]+|\[U:[0-9]:[0-9]+\]", RegexOptions.None, RegexTimeout);
            return match.Success ? match.Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Extracts the player name from text such as <c>Name&lt;123&gt;&lt;STEAM_ID&gt;</c>.</summary>
    protected string? ExtractPlayerName(string text)
    {
        try
        {
            var match = Regex.Match(text, @"([^<]+)<\d+><[^>]+>", RegexOptions.None, RegexTimeout);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
