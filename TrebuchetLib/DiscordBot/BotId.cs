using System.Globalization;

namespace TrebuchetLib.DiscordBot;

/// <summary>Discord IDs (snowflakes) of users, roles, channels and servers.</summary>
public static class BotId
{
    /// <summary>Real IDs have 17 to 20 digits. 0 and small numbers are typos, never an ID.</summary>
    public const ulong MinValue = 10_000_000_000_000_000UL;

    public static bool IsValid(ulong id) => id >= MinValue;

    /// <summary>Accepts digits only: no sign, spaces, separators or text around the number.</summary>
    public static bool TryParse(string? text, out ulong id)
    {
        id = 0;
        if (string.IsNullOrEmpty(text) || text.Length is < 17 or > 20) return false;
        foreach (var c in text)
            if (c is < '0' or > '9') return false;
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && IsValid(id);
    }
}
