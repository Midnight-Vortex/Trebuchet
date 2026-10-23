namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Bounds how long a restart, stop or update can be put off from Discord. Postponing replaces the pause of the
/// launcher, so asking again and again would postpone for ever (and an update round waits for paused servers). Every
/// chain of postponements of a server gets a start time; it may not reach further than that start plus the maximum of the
/// settings. The chain ends when the server restarted (the postponed thing happened) or when nothing was postponed
/// for a while.
/// </summary>
public sealed class BotPostponeBudget
{
    /// <summary>A pause that ended this long ago is a new chain (nothing was waiting for it any more).</summary>
    public static readonly TimeSpan IdleReset = TimeSpan.FromMinutes(30);

    private sealed class Chain
    {
        public DateTime AnchorUtc;
        public DateTime? ServerStartedUtc;
        public DateTime LastUntilUtc;
    }

    /// <summary>The state of one server's chain before a reservation, to take it back when the action did not happen.</summary>
    public readonly record struct Snapshot(bool Existed, DateTime AnchorUtc, DateTime? ServerStartedUtc, DateTime LastUntilUtc);

    private readonly object _lock = new();
    private readonly Dictionary<int, Chain> _chains = [];

    public Snapshot Save(int instance)
    {
        lock (_lock)
            return _chains.TryGetValue(instance, out var chain)
                ? new Snapshot(true, chain.AnchorUtc, chain.ServerStartedUtc, chain.LastUntilUtc)
                : default;
    }

    /// <summary>Takes a reservation back: an action that was refused (busy, not running) used none of the budget.</summary>
    public void Restore(int instance, Snapshot snapshot)
    {
        lock (_lock)
        {
            if (!snapshot.Existed)
                _chains.Remove(instance);
            else
                _chains[instance] = new Chain { AnchorUtc = snapshot.AnchorUtc, ServerStartedUtc = snapshot.ServerStartedUtc, LastUntilUtc = snapshot.LastUntilUtc };
        }
    }

    /// <returns>The minutes that may be postponed now: the requested ones, less when the chain reaches its limit, 0 when it is used up.</returns>
    public int Reserve(int instance, DateTime? serverStartedUtc, int requestedMinutes, TimeSpan maxPause, DateTime utcNow)
    {
        if (requestedMinutes < 1) return 0;
        lock (_lock)
        {
            if (!_chains.TryGetValue(instance, out var chain)
                || chain.ServerStartedUtc != serverStartedUtc
                || utcNow - chain.LastUntilUtc > IdleReset)
            {
                chain = new Chain { AnchorUtc = utcNow, ServerStartedUtc = serverStartedUtc, LastUntilUtc = utcNow };
                _chains[instance] = chain;
            }

            var limit = chain.AnchorUtc + maxPause;
            var granted = (int)Math.Min(requestedMinutes, Math.Floor((limit - utcNow).TotalMinutes));
            if (granted < 1) return 0;
            chain.LastUntilUtc = utcNow + TimeSpan.FromMinutes(granted);
            return granted;
        }
    }
}
