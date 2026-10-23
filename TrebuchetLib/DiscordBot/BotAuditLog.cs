using System.Globalization;
using System.Text;

namespace TrebuchetLib.DiscordBot;

public enum BotAuditResult
{
    Allowed,
    Denied,
    Throttled,
    LockedOut,
    Executed,
    Failed,
    Cancelled
}

/// <param name="Detail">Local only: the exact reason of a refusal, the outcome of an action.</param>
public sealed record BotAuditEntry(
    DateTime UtcTime,
    BotAuditResult Result,
    ulong UserId,
    string UserName,
    ulong GuildId,
    ulong ChannelId,
    BotCommand? Command,
    string Parameters,
    string Detail);

/// <summary>
/// The record of everything the bot was asked and did, allowed or refused. One file per month, only appended to, one
/// line per entry. Texts that come from Discord are cleaned so nobody can fake a line. It never holds the token.
/// </summary>
public sealed class BotAuditLog(string directory, int memoryEntries = 200)
{
    private readonly object _lock = new();
    private readonly List<BotAuditEntry> _recent = [];

    public event EventHandler<BotAuditEntry>? EntryWritten;

    public string Directory { get; } = directory;

    public IReadOnlyList<BotAuditEntry> Recent
    {
        get { lock (_lock) return _recent.ToList(); }
    }

    public static string GetFileName(DateTime utc) => $"discord-bot-audit-{utc.ToString("yyyyMM", CultureInfo.InvariantCulture)}.log";

    /// <summary>Never throws: a full disk must not stop the bot, and the entry stays in the list in memory.</summary>
    public void Write(BotAuditEntry entry)
    {
        var clean = entry with
        {
            UserName = Field(entry.UserName),
            Parameters = Field(BotLogRedactor.Redact(entry.Parameters)),
            Detail = Field(BotLogRedactor.Redact(entry.Detail))
        };
        lock (_lock)
        {
            _recent.Add(clean);
            if (_recent.Count > memoryEntries) _recent.RemoveRange(0, _recent.Count - memoryEntries);
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path.Combine(Directory, GetFileName(clean.UtcTime)), Format(clean) + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Kept in memory, shown in the panel.
            }
        }
        EntryWritten?.Invoke(this, clean);
    }

    // One line, and no "|": the display name comes from Discord and could otherwise fake the fields that follow it.
    private static string Field(string? text) => BotInputRules.SanitizeForLog(text).Replace('|', '/');

    public static string Format(BotAuditEntry entry) => string.Join(" | ",
        entry.UtcTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        entry.Result.ToString().ToUpperInvariant(),
        $"user={entry.UserId} ({entry.UserName})",
        $"guild={entry.GuildId} channel={entry.ChannelId}",
        entry.Command?.ToString() ?? "-",
        entry.Parameters,
        entry.Detail);
}
