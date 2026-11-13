using System.Text;

namespace Cabinet.Extras.Gaming.Moderation;

/// <summary>What a moderator might do about a player, least to most severe.</summary>
public enum ModerationAction
{
    None,
    Warn,
    Mute,
    Kick,
    Ban,
}

/// <summary>
/// A suggested action, with the SourceMod command a moderator could type. Cabinet never sends it:
/// a person decides, and runs it themselves.
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="Reason">Why, in a sentence.</param>
/// <param name="BanMinutes">For a ban, how long.</param>
/// <param name="Command">The SourceMod command, or null when there's nothing to do.</param>
public sealed record ModerationSuggestion(ModerationAction Action, string Reason, int? BanMinutes, string? Command);

/// <summary>Turns what a player did into a suggestion a moderator can act on.</summary>
public static class PlayerModeration
{
    /// <summary>
    /// Suggests an action from a player's toxic chat and their history. Severe lines are threats,
    /// hate speech and the like; a history of bans makes the next step harsher.
    /// </summary>
    /// <param name="toxicLines">Chat lines flagged at any severity.</param>
    /// <param name="severeLines">Of those, how many were high or critical.</param>
    /// <param name="priorBans">Bans against them already.</param>
    /// <param name="priorKicks">Kicks against them already.</param>
    /// <param name="steamId">Their Steam ID, which bans need.</param>
    /// <param name="name">Their name, for messages.</param>
    public static ModerationSuggestion Suggest(int toxicLines, int severeLines, int priorBans, int priorKicks, string? steamId, string? name)
    {
        var who = string.IsNullOrWhiteSpace(name) ? steamId ?? "the player" : name;
        if (severeLines >= 1 && priorBans >= 1)
        {
            return Build(ModerationAction.Ban, $"{severeLines} severe line(s) after {priorBans} earlier ban(s)", 7 * 24 * 60, steamId, who);
        }

        if (severeLines >= 2 || (severeLines >= 1 && priorKicks >= 2))
        {
            return Build(ModerationAction.Ban, $"{severeLines} severe line(s){(priorKicks > 0 ? $" and {priorKicks} earlier kick(s)" : string.Empty)}", 24 * 60, steamId, who);
        }

        if (severeLines == 1 || toxicLines >= 5)
        {
            return Build(ModerationAction.Kick, severeLines == 1 ? "a severe line" : $"{toxicLines} toxic lines", null, steamId, who);
        }

        if (toxicLines >= 2)
        {
            return Build(ModerationAction.Mute, $"{toxicLines} toxic lines", null, steamId, who);
        }

        return toxicLines == 1
            ? Build(ModerationAction.Warn, "one toxic line", null, steamId, who)
            : new ModerationSuggestion(ModerationAction.None, "Nothing flagged", null, null);
    }

    private static ModerationSuggestion Build(ModerationAction action, string reason, int? minutes, string? steamId, string who)
    {
        var target = Target(steamId, who);
        var because = Safe(reason);
        var command = target == null ? null : action switch
        {
            ModerationAction.Warn => $"sm_psay \"{target}\" \"Please keep chat civil: {because}\"",
            ModerationAction.Mute => $"sm_gag \"{target}\" \"{because}\"",
            ModerationAction.Kick => $"sm_kick \"{target}\" \"{because}\"",

            // A ban by name could catch someone else with the same name
            ModerationAction.Ban when steamId != null && IsSteamId(steamId) => $"sm_addban {minutes} \"{steamId}\" \"{because}\"",
            _ => null,
        };
        return new ModerationSuggestion(action, reason, minutes, command);
    }

    /// <summary>
    /// The target as SourceMod reads it: #STEAM_... for a Steam ID, else the name. Null if neither
    /// is usable.
    /// </summary>
    private static string? Target(string? steamId, string who)
    {
        if (steamId != null && IsSteamId(steamId))
        {
            return $"#{steamId}";
        }

        var name = Safe(who);
        return name.Length > 0 ? name : null;
    }

    /// <summary>Whether this is a Steam ID: STEAM_X:Y:Z or [U:1:N].</summary>
    public static bool IsSteamId(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, @"^(STEAM_[0-5]:[01]:\d{1,12}|\[U:1:\d{1,12}\])$",
            System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Text that can go inside a quoted console argument. The Source console splits commands at
    /// ; and newlines, so a name holding them could make a pasted command run another one.
    /// </summary>
    public static string Safe(string text)
    {
        var safe = new StringBuilder(Math.Min(text.Length, 100));
        foreach (var c in text)
        {
            if (safe.Length >= 100)
            {
                break;
            }

            if (c is '"' or ';' or '\\' or '`' || char.IsControl(c))
            {
                continue;
            }

            safe.Append(c);
        }

        return safe.ToString().Trim();
    }
}
