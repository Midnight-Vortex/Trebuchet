namespace TrebuchetLib.DiscordBot;

public enum BotThrottle
{
    None,
    /// <summary>The user sent the previous command a moment ago.</summary>
    Cooldown,
    /// <summary>Too many critical actions for the server in a short time.</summary>
    CriticalLimit
}

/// <summary>
/// Protects against flooding and guessing: a wait between commands, a limit for critical actions and a lock for users
/// whose requests are refused again and again. The time comes in as a parameter, so everything can be tested.
/// </summary>
public sealed class BotRateLimiter
{
    private const int MaxTrackedUsers = 5000;
    private const int MaxTrackedInteractions = 2000;

    private readonly object _lock = new();
    private readonly Dictionary<ulong, DateTime> _lastCommand = [];
    private readonly Dictionary<ulong, List<DateTime>> _denied = [];
    private readonly Dictionary<ulong, DateTime> _lockedUntil = [];
    private readonly Dictionary<int, List<DateTime>> _critical = [];
    private readonly HashSet<ulong> _interactions = [];
    private readonly Queue<ulong> _interactionOrder = new();

    public bool IsLockedOut(ulong userId, DateTime utcNow)
    {
        lock (_lock)
        {
            if (!_lockedUntil.TryGetValue(userId, out var until)) return false;
            if (until > utcNow) return true;
            _lockedUntil.Remove(userId);
            return false;
        }
    }

    /// <summary>Counts a refused request. Returns true when this one locks the user out.</summary>
    public bool RegisterDenied(ulong userId, DateTime utcNow, BotPolicy policy)
    {
        lock (_lock)
        {
            Trim(_denied, utcNow - policy.LockoutDuration);
            if (!_denied.TryGetValue(userId, out var list)) _denied[userId] = list = [];
            list.RemoveAll(x => utcNow - x > policy.LockoutDuration);
            list.Add(utcNow);
            if (list.Count < policy.LockoutAttempts) return false;
            list.Clear();
            var already = _lockedUntil.TryGetValue(userId, out var until) && until > utcNow;
            _lockedUntil[userId] = utcNow + policy.LockoutDuration;
            return !already;
        }
    }

    /// <summary>
    /// The first request of a command waits for the cooldown. A follow-up (confirmation, selection) answers a question
    /// of the bot and does not. A limited command is counted when it is confirmed.
    /// </summary>
    public BotThrottle TryAcquire(ulong userId, BotCommand command, int? instance, bool followUp, DateTime utcNow, BotPolicy policy)
    {
        lock (_lock)
        {
            if (!followUp)
            {
                if (_lastCommand.TryGetValue(userId, out var last) && utcNow - last < policy.Cooldown)
                    return BotThrottle.Cooldown;
                if (_lastCommand.Count >= MaxTrackedUsers) Trim(_lastCommand, utcNow - policy.Cooldown);
                _lastCommand[userId] = utcNow;
            }

            if (!BotCommands.IsLimited(command)) return BotThrottle.None;
            var key = instance ?? -1;
            if (!_critical.TryGetValue(key, out var list)) _critical[key] = list = [];
            list.RemoveAll(x => utcNow - x > policy.CriticalWindow);
            if (list.Count >= policy.CriticalLimit) return BotThrottle.CriticalLimit;
            // Only the confirmed action uses up the limit.
            if (followUp || !BotCommands.NeedsConfirmation(command)) list.Add(utcNow);
            return BotThrottle.None;
        }
    }

    /// <summary>True the first time an interaction is seen. Discord interactions have unique IDs.</summary>
    public bool TryMarkInteraction(ulong interactionId)
    {
        if (interactionId == 0) return true;
        lock (_lock)
        {
            if (!_interactions.Add(interactionId)) return false;
            _interactionOrder.Enqueue(interactionId);
            while (_interactionOrder.Count > MaxTrackedInteractions)
                _interactions.Remove(_interactionOrder.Dequeue());
            return true;
        }
    }

    private static void Trim(Dictionary<ulong, DateTime> dictionary, DateTime before)
    {
        foreach (var key in dictionary.Where(x => x.Value < before).Select(x => x.Key).ToList())
            dictionary.Remove(key);
    }

    private void Trim(Dictionary<ulong, List<DateTime>> dictionary, DateTime before)
    {
        if (dictionary.Count < MaxTrackedUsers) return;
        foreach (var key in dictionary.Where(x => x.Value.Count == 0 || x.Value[^1] < before).Select(x => x.Key).ToList())
            dictionary.Remove(key);
    }
}
