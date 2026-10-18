namespace TrebuchetLib;

/// <summary>
/// Crash loop protection for 24/7 operation: a server crashing again and again (broken mod, bad config, port in
/// use) is restarted at most <see cref="Limit"/> times per <see cref="Window"/>.
/// </summary>
public sealed class CrashRestartGuard(int limit, TimeSpan window)
{
    private readonly Dictionary<int, List<DateTime>> _history = [];

    public int Limit { get; } = limit;
    public TimeSpan Window { get; } = window;

    public bool Allow(int instance, DateTime utcNow)
    {
        if (!_history.TryGetValue(instance, out var history))
            _history[instance] = history = [];
        history.RemoveAll(x => utcNow - x > Window);
        if (history.Count >= Limit) return false;
        history.Add(utcNow);
        return true;
    }
}
