using System.Security.Cryptography;

namespace TrebuchetLib.DiscordBot;

/// <summary>An action the bot asked the user about (confirm a stop, pick the minutes of a postponement).</summary>
/// <param name="Minutes">Warning time chosen for a restart or stop.</param>
/// <param name="ChannelId">The channel the question was asked in. The answer only counts from there.</param>
public sealed record BotPending(ulong UserId, BotCommand Command, int Instance, DateTime ExpiresUtc, int Minutes = 0, ulong ChannelId = 0);

/// <summary>
/// Open questions of the bot. The buttons and menus of a question carry a random code, nothing else: the action
/// itself stays here, so it can not be forged, and it only works for the user it was offered to, once, for a short time.
/// </summary>
public sealed class BotPendingActions
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(30);
    private const int MaxEntries = 500;

    private readonly object _lock = new();
    private readonly Dictionary<string, BotPending> _entries = [];

    public string Create(ulong userId, BotCommand command, int instance, DateTime utcNow, TimeSpan? lifetime = null,
        int minutes = 0, ulong channelId = 0)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_lock)
        {
            foreach (var key in _entries.Where(x => x.Value.ExpiresUtc <= utcNow).Select(x => x.Key).ToList())
                _entries.Remove(key);
            if (_entries.Count >= MaxEntries)
                _entries.Remove(_entries.OrderBy(x => x.Value.ExpiresUtc).First().Key);
            _entries[nonce] = new BotPending(userId, command, instance, utcNow + (lifetime ?? DefaultLifetime), minutes, channelId);
        }
        return nonce;
    }

    /// <summary>Takes the action out (it works once). Another user, or an expired code, gets nothing.</summary>
    public bool TryTake(string? nonce, ulong userId, DateTime utcNow, out BotPending pending)
    {
        pending = null!;
        if (string.IsNullOrEmpty(nonce)) return false;
        lock (_lock)
        {
            if (!_entries.TryGetValue(nonce, out var entry)) return false;
            if (entry.ExpiresUtc <= utcNow)
            {
                _entries.Remove(nonce);
                return false;
            }
            if (entry.UserId != userId) return false;
            _entries.Remove(nonce);
            pending = entry;
            return true;
        }
    }

    /// <summary>Drops a question without answering it (cancel).</summary>
    public bool Discard(string? nonce, ulong userId)
    {
        if (string.IsNullOrEmpty(nonce)) return false;
        lock (_lock)
            return _entries.TryGetValue(nonce, out var entry) && entry.UserId == userId && _entries.Remove(nonce);
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }
}
