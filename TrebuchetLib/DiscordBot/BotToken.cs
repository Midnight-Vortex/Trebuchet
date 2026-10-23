using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TrebuchetLib.DiscordBot;

/// <summary>The format of a bot token and how it is kept out of logs.</summary>
public static partial class BotToken
{
    /// <summary>Takes what was pasted ("Bot " in front, quotes, spaces) and returns the token when it looks like one.</summary>
    public static bool TryNormalize(string? input, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim().Trim('"', '\'').Trim();
        if (text.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase)) text = text[4..].Trim();
        text = text.Trim('"', '\'').Trim();
        if (text.Length is < 50 or > 120 || !TokenPattern().IsMatch(text)) return false;
        token = text;
        return true;
    }

    /// <summary>The same token gives the same name. The token itself can not be read back from it.</summary>
    public static string Fingerprint(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash, 0, 8);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{20,}$")]
    internal static partial Regex TokenPattern();

    [GeneratedRegex(@"[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{20,}")]
    internal static partial Regex TokenSearchPattern();
}

/// <summary>Removes tokens from texts before they reach a log or a message.</summary>
public static class BotLogRedactor
{
    public const string Replacement = "[token removed]";
    private static readonly object Lock = new();
    private static readonly HashSet<string> Known = [];

    /// <summary>The token in use is removed even if it does not look like a token.</summary>
    public static void RegisterSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        lock (Lock) Known.Add(secret);
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string[] known;
        lock (Lock) known = [.. Known];
        foreach (var secret in known)
            text = text.Replace(secret, Replacement, StringComparison.Ordinal);
        return BotToken.TokenSearchPattern().Replace(text, Replacement);
    }
}
