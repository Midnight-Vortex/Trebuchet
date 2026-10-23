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
