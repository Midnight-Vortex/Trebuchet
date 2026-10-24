using System.Globalization;
using System.Text;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Checks and cleans everything a user can type or pick. The bot never builds a command, a path or a script from it:
/// numbers are checked against a range, texts only go to the log and are cleaned of everything that could break it.
/// </summary>
public static class BotInputRules
{
    public const int MaxReasonLength = 100;
    public const int MaxLogTextLength = 200;
    /// <summary>A line of the audit channel: a request with the name of a server, a reason and the outcome in words.</summary>
    public const int MaxNoteLength = 500;
    /// <summary>The name a server instance gets in the messages of the bot.</summary>
    public const int MaxNameLength = 40;
    /// <summary>The highest instance number Discord options and button IDs can carry.</summary>
    public const int MaxInstance = 99;

    // The warning times of the menu of a restart or stop, like the buttons of the dialog in Trebuchet.
    private static readonly IReadOnlyList<int> RestartDelays = [0, 1, 5, 10, 15, 30, 60, 120];
    private static readonly IReadOnlyList<int> StopDelays = [0, 1, 5, 10, 15, 30, 60];

    /// <summary>The longest warning time of a restart (two hours) or stop (one hour) started from Discord.</summary>
    public static int MaxDelayMinutes(BotCommand command) => command == BotCommand.RestartNow ? RestartDelays[^1] : StopDelays[^1];

    /// <summary>The warning times offered for a restart or stop. 0 is "right away".</summary>
    public static IReadOnlyList<int> DelayChoices(BotCommand command) => command == BotCommand.RestartNow ? RestartDelays : StopDelays;

    /// <summary>The picked value of the menu has to be one of the offered warning times.</summary>
    public static bool TryParseDelayChoice(string? value, BotCommand command, out int minutes)
    {
        minutes = 0;
        if (command is not (BotCommand.RestartNow or BotCommand.Stop)) return false;
        if (string.IsNullOrEmpty(value) || value.Length > 3 || !value.All(char.IsAsciiDigit)) return false;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        if (!DelayChoices(command).Contains(parsed)) return false;
        minutes = parsed;
        return true;
    }

    /// <summary>
    /// A name for a server instance: one clean line of at most <see cref="MaxNameLength"/> characters without anything
    /// that Discord would turn into a mention, a link, a channel or formatting, and without the braces of the macros.
    /// </summary>
    public static string SanitizeName(string? text)
    {
        var clean = Clean(text, MaxNameLength * 3);
        var builder = new StringBuilder(clean.Length);
        foreach (var c in clean)
            if ("{}<>@#[]`|*~\\:".IndexOf(c) < 0)
                builder.Append(c);
        // Taking characters out can leave two spaces next to each other.
        return Truncate(Clean(builder.ToString(), MaxNameLength * 3).Trim(), MaxNameLength);
    }

    /// <summary>The server instance has to exist. The list shown in Discord is only a convenience.</summary>
    public static bool TryParseInstance(long value, int instanceCount, out int instance)
    {
        instance = -1;
        if (value < 0 || value >= instanceCount) return false;
        instance = (int)value;
        return true;
    }

    /// <summary>Whole minutes between 1 and the maximum of the settings.</summary>
    public static bool TryParseMinutes(long value, BotPolicy policy, out int minutes)
    {
        minutes = 0;
        if (value < 1 || value > policy.MaxPause.TotalMinutes) return false;
        minutes = (int)value;
        return true;
    }

    /// <summary>The picked value of the selection has to be one of the offered minutes.</summary>
    public static bool TryParsePostponeChoice(string? value, BotPolicy policy, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrEmpty(value) || value.Length > 5 || !value.All(char.IsAsciiDigit)) return false;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        if (!policy.PostponeChoices.Contains(parsed)) return false;
        minutes = parsed;
        return true;
    }

    /// <summary>
    /// A free text (the reason of an action): no control or invisible characters, no line breaks, no braces (the macros of
    /// messages), at most <see cref="MaxReasonLength"/> characters.
    /// </summary>
    public static string SanitizeReason(string? text)
    {
        var clean = Clean(text, MaxReasonLength * 2).Replace("{", string.Empty).Replace("}", string.Empty);
        return Truncate(clean.Trim(), MaxReasonLength);
    }

    /// <summary>A text for the log, e.g. a display name: one line, no way to fake a log line.</summary>
    public static string SanitizeForLog(string? text) => Truncate(Clean(text, MaxLogTextLength * 2).Trim(), MaxLogTextLength);

    /// <summary>A line for the audit channel: like <see cref="SanitizeForLog"/>, but long enough for a whole sentence.</summary>
    public static string SanitizeNote(string? text) => Truncate(Clean(text, MaxNoteLength * 2).Trim(), MaxNoteLength);

    private static string Clean(string? text, int limit)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(Math.Min(text.Length, limit));
        var lastWasSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (builder.Length >= limit) break;
            var category = Rune.GetUnicodeCategory(rune);
            // Control, format (zero width, text direction), line/paragraph separators, unassigned, private use.
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                or System.Globalization.UnicodeCategory.OtherNotAssigned or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.Surrogate)
            {
                // Line breaks and tabs still separate words.
                if (rune.Value is '\n' or '\r' or '\t' && !lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }
            if (category == System.Globalization.UnicodeCategory.SpaceSeparator)
            {
                if (lastWasSpace) continue;
                builder.Append(' ');
                lastWasSpace = true;
                continue;
            }
            builder.Append(rune.ToString());
            lastWasSpace = false;
        }
        return builder.ToString();
    }

    private static string Truncate(string text, int length)
    {
        if (text.Length <= length) return text;
        var cut = text[..length];
        // Do not end in the middle of a surrogate pair.
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut.TrimEnd();
    }
}
