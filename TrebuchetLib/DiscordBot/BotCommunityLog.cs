using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// The texts of the community log: what the players of a server read. Short, friendly, with Discord timestamps
/// (<c>&lt;t:…:t&gt;</c> shows the time in the clock of whoever reads it, <c>&lt;t:…:R&gt;</c> "in 30 minutes" / "8 hours ago").
/// </summary>
public sealed class BotCommunityTexts
{
    // {0} the time text ("30 Minuten").
    public required string CountdownRestart { get; init; }
    public required string CountdownUpdate { get; init; }
    public required string CountdownStop { get; init; }
    /// <summary>{0} "Neustart" or "Stopp", {1} the unix time.</summary>
    public required string CountdownBody { get; init; }
    public required string WordRestart { get; init; }
    public required string WordStop { get; init; }
    public required string RestartingTitle { get; init; }
    public required string RestartingBody { get; init; }
    public required string StoppingTitle { get; init; }
    public required string StoppingBody { get; init; }
    public required string OnlineTitle { get; init; }
    /// <summary>{0} how long the server was offline.</summary>
    public required string OnlineAfterRestartBody { get; init; }
    public required string OnlineAfterStopBody { get; init; }
    public required string OneMinute { get; init; }
    /// <summary>{0} minutes.</summary>
    public required string Minutes { get; init; }
    /// <summary>{0} hours.</summary>
    public required string HoursOnly { get; init; }
    /// <summary>{0} hours, {1} minutes.</summary>
    public required string HoursMinutes { get; init; }
    /// <summary>{0} minutes, {1} seconds.</summary>
    public required string MinutesSeconds { get; init; }
    /// <summary>{0} seconds.</summary>
    public required string SecondsOnly { get; init; }

    public static BotCommunityTexts German { get; } = new()
    {
        CountdownRestart = "⏰ Der Server startet in {0} neu",
        CountdownUpdate = "⏰ Der Server startet in {0} für ein Update neu",
        CountdownStop = "⏰ Der Server wird in {0} gestoppt",
        CountdownBody = "Bitte sucht euch rechtzeitig einen sicheren Ort und loggt euch aus.\n{0} um <t:{1}:t> Uhr (<t:{1}:R>).",
        WordRestart = "Neustart",
        WordStop = "Stopp",
        RestartingTitle = "🔄 Der Server startet jetzt neu",
        RestartingBody = "Bis gleich – sobald er wieder da ist, steht es hier.",
        StoppingTitle = "⏹️ Der Server wird jetzt gestoppt",
        StoppingBody = "Er bleibt vorerst aus – sobald er wieder da ist, steht es hier.",
        OnlineTitle = "🟢 Der Server ist wieder online",
        OnlineAfterRestartBody = "Der Neustart ist durch – ihr könnt euch wieder einloggen.\n**Offline:** {0}",
        OnlineAfterStopBody = "Ihr könnt euch wieder einloggen.\n**Offline:** {0}",
        OneMinute = "1 Minute",
        Minutes = "{0} Minuten",
        HoursOnly = "{0} Std.",
        HoursMinutes = "{0} Std. {1} Min.",
        MinutesSeconds = "{0} Min. {1} Sek.",
        SecondsOnly = "{0} Sek."
    };

    public static BotCommunityTexts English { get; } = new()
    {
        CountdownRestart = "⏰ The server restarts in {0}",
        CountdownUpdate = "⏰ The server restarts for an update in {0}",
        CountdownStop = "⏰ The server stops in {0}",
        CountdownBody = "Please find a safe place and log out in time.\n{0} at <t:{1}:t> (<t:{1}:R>).",
        WordRestart = "Restart",
        WordStop = "Stop",
        RestartingTitle = "🔄 The server restarts now",
        RestartingBody = "See you soon – when it is back, you will read it here.",
        StoppingTitle = "⏹️ The server stops now",
        StoppingBody = "It stays down for now – when it is back, you will read it here.",
        OnlineTitle = "🟢 The server is back online",
        OnlineAfterRestartBody = "The restart is done – you can log in again.\n**Offline:** {0}",
        OnlineAfterStopBody = "You can log in again.\n**Offline:** {0}",
        OneMinute = "1 minute",
        Minutes = "{0} minutes",
        HoursOnly = "{0} h",
        HoursMinutes = "{0} h {1} min",
        MinutesSeconds = "{0} min {1} s",
        SecondsOnly = "{0} s"
    };

    public static BotCommunityTexts For(string? language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? English : German;

    /// <summary>"1 Min. 20 Sek.", "45 Sek.", "1 Std. 5 Min.": how long the server was offline.</summary>
    public string Duration(TimeSpan span)
    {
        var inv = CultureInfo.InvariantCulture;
        var seconds = (long)Math.Round(Math.Max(0, span.TotalSeconds));
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var rest = seconds % 60;
        if (hours > 0) return minutes == 0 ? string.Format(inv, HoursOnly, hours) : string.Format(inv, HoursMinutes, hours, minutes);
        if (minutes > 0) return string.Format(inv, MinutesSeconds, minutes, rest);
        return string.Format(inv, SecondsOnly, rest);
    }

    /// <summary>"30 Minuten", "1 Minute".</summary>
    public string MinutesText(int minutes) =>
        minutes <= 1 ? OneMinute : string.Format(CultureInfo.InvariantCulture, Minutes, minutes);
}

/// <summary>
/// The community log: tells the players of a server what happens to it, in a channel of its own through a webhook with its
/// own name and picture: a planned restart or stop shortly before it (with the time), that it is going down now, and that
/// it is back with how long it was offline. Planned means the launcher knows about it (schedule, update, countdown, a stop
/// or restart from Discord or the window); a crash or a server that was down all along says nothing.
/// </summary>
/// <remarks>
/// It reads the states itself (like the status board, but without needing a status message or an admin channel) and sends
/// one embed per event, spaced out, bounded, dropped when Discord refuses: it is a convenience for the players and never
/// holds anything up.
/// </remarks>
public sealed class BotCommunityLog : IDisposable
{
    public const int QueueLimit = 50;
    public static readonly TimeSpan DefaultSpacing = TimeSpan.FromMilliseconds(1200);
    public static readonly TimeSpan CalmInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SameEventTolerance = TimeSpan.FromMinutes(2);

    public const uint Orange = 0xE67E22;
    public const uint Red = 0xED4245;
    public const uint Green = 0x57F287;

    private sealed class Memory
    {
        public BotServerState? Last;
        public BotPendingAction LastPending;
        // The time the announced restart or stop was told for: the same event comes with a time that differs by seconds
        // (the schedule says 06:00:00, the countdown ends at 06:00:02), a postponement by much more.
        public DateTime? AnnouncedAt;
        public DateTime? OfflineSince;
        public bool Told;
        public bool ToldRestart;
    }

    private readonly IBotActions _actions;
    private readonly IBotWebhookClient _client;
    private readonly Func<string?> _webhook;
    private readonly Func<BotPolicy?> _policy;
    private readonly Func<DateTime> _clock;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private readonly Dictionary<int, Memory> _memory = [];
    private readonly Channel<BotWebhookEmbed> _queue;
    private readonly CancellationTokenSource _cancel = new();
    private Task? _sender;
    private int _disposed;
    private int _unsent;
    private bool _failing;
    private (string? Url, DateTime AtUtc) _cachedWebhook;

    public BotCommunityLog(IBotActions actions, IBotWebhookClient client, Func<string?> webhook, Func<BotPolicy?> policy,
        Func<DateTime>? clock = null, ILogger? logger = null)
    {
        _actions = actions;
        _client = client;
        _webhook = webhook;
        _policy = policy;
        _clock = clock ?? (() => DateTime.UtcNow);
        _logger = logger;
        _queue = Channel.CreateBounded<BotWebhookEmbed>(
            new BoundedChannelOptions(QueueLimit) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Decrement(ref _unsent));
    }

    /// <summary>Pause between two messages.</summary>
    public TimeSpan Spacing { get; init; } = DefaultSpacing;

    /// <summary>How long the webhook read from its protected file is used before it is read again.</summary>
    public TimeSpan WebhookCacheTime { get; init; } = TimeSpan.FromSeconds(5);

    public void Start()
    {
        if (_sender is not null) return;
        _sender = Task.Run(SendAsync);
    }

    /// <summary>The webhook is read from its protected file at most every few seconds (not for every look at a server).</summary>
    private string? Webhook()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (now - _cachedWebhook.AtUtc < WebhookCacheTime) return _cachedWebhook.Url;
        }
        string? url;
        try
        {
            url = _webhook();
        }
        catch (Exception)
        {
            url = null;
        }
        lock (_lock) _cachedWebhook = (url, now);
        return url;
    }

    /// <summary>
    /// Looks at the servers and tells what changed. Never throws. Returns when it wants to look again at the latest: a
    /// server that starts or stops every 15 seconds, a calm one every 5 minutes, and exactly when a planned restart comes
    /// within the lead time.
    /// </summary>
    public async Task<TimeSpan> RefreshAsync(CancellationToken token)
    {
        var next = CalmInterval;
        try
        {
            var policy = _policy();
            if (policy is not { CommunityEnabled: true } || Webhook() is null)
            {
                // Off (or no webhook): nothing is remembered, so switching it on starts with a fresh look and says nothing old.
                lock (_lock) _memory.Clear();
                return next;
            }
            var statuses = await _actions.GetStatusesAsync(token).ConfigureAwait(false);
            if (statuses.Count == 0 && _actions.InstanceCount > 0) return BusyInterval;
            var now = _clock();
            var managed = statuses.Where(x => policy.IsManaged(x.Instance)).Take(25).ToList();
            foreach (var status in managed)
            {
                var due = Observe(status, now, managed.Count > 1, policy);
                if (due < next) next = due;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Discord bot: the community log could not look at the servers: {Error}", DiscordNetLog.Describe(ex));
            next = BusyInterval;
        }
        return next;
    }

    /// <summary>One look at one server: counts the announcement and the changes, returns when to look again.</summary>
    internal TimeSpan Observe(BotServerStatus status, DateTime now, bool several, BotPolicy policy)
    {
        var texts = BotCommunityTexts.For(policy.Language);
        var due = status.State is BotServerState.Starting or BotServerState.Stopping ? BusyInterval : CalmInterval;
        lock (_lock)
        {
            if (!_memory.TryGetValue(status.Instance, out var memory)) _memory[status.Instance] = memory = new Memory();
            var before = memory.Last;
            memory.Last = status.State;

            if (before is BotServerState.Online && status.State != BotServerState.Online)
            {
                memory.OfflineSince = now;
                memory.Told = false;
                // A crash says nothing: the launcher did not plan it.
                if (status.State != BotServerState.Crashed)
                {
                    var restart = status.Restarting || memory.LastPending is BotPendingAction.Restart or BotPendingAction.Update;
                    Enqueue(restart
                        ? Embed(texts.RestartingTitle, texts.RestartingBody, Red, now, status, several)
                        : Embed(texts.StoppingTitle, texts.StoppingBody, Red, now, status, several));
                    memory.Told = true;
                    memory.ToldRestart = restart;
                }
                memory.AnnouncedAt = null;
            }
            else if (before is not null && before != BotServerState.Online && status.State == BotServerState.Online)
            {
                if (memory.Told && memory.OfflineSince is { } since)
                {
                    var body = string.Format(CultureInfo.InvariantCulture, memory.ToldRestart ? texts.OnlineAfterRestartBody : texts.OnlineAfterStopBody,
                        texts.Duration(now - since));
                    Enqueue(Embed(texts.OnlineTitle, body, Green, now, status, several));
                }
                memory.Told = false;
                memory.OfflineSince = null;
            }

            if (status.State == BotServerState.Online)
            {
                memory.LastPending = status.Pending;
                if (status.Pending != BotPendingAction.None && status.PendingAtUtc is { } at && at > now)
                {
                    var remaining = at - now;
                    if (remaining <= policy.CommunityLead)
                    {
                        if (memory.AnnouncedAt is not { } told || Math.Abs((at - told).TotalMinutes) > SameEventTolerance.TotalMinutes)
                        {
                            memory.AnnouncedAt = at;
                            Enqueue(Countdown(status, at, remaining, texts, now, several));
                        }
                        // Looked at again when the server goes down, at the latest.
                        if (remaining + TimeSpan.FromSeconds(5) < due) due = remaining + TimeSpan.FromSeconds(5);
                    }
                    else
                    {
                        // The time the announcement is due: wake exactly then, not up to five minutes later.
                        var wait = remaining - policy.CommunityLead;
                        if (wait < due) due = wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1);
                    }
                }
            }
        }
        return due;
    }

    private static BotWebhookEmbed Countdown(BotServerStatus status, DateTime at, TimeSpan remaining, BotCommunityTexts texts, DateTime now, bool several)
    {
        var inv = CultureInfo.InvariantCulture;
        var minutes = texts.MinutesText(Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
        var title = string.Format(inv, status.Pending switch
        {
            BotPendingAction.Stop => texts.CountdownStop,
            BotPendingAction.Update => texts.CountdownUpdate,
            _ => texts.CountdownRestart
        }, minutes);
        var unix = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(inv);
        var body = string.Format(inv, texts.CountdownBody, status.Pending == BotPendingAction.Stop ? texts.WordStop : texts.WordRestart, unix);
        return Embed(title, body, Orange, now, status, several);
    }

    // With several servers the name tells which one it is about; with one server the name of the log says it.
    private static BotWebhookEmbed Embed(string title, string body, uint color, DateTime now, BotServerStatus status, bool several) =>
        new(title, several ? $"**{status.Name}**\n{body}" : body, color, now);

    private void Enqueue(BotWebhookEmbed embed)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Increment(ref _unsent);
        if (!_queue.Writer.TryWrite(embed)) Interlocked.Decrement(ref _unsent);
    }

    /// <summary>Waits until everything queued so far was sent or dropped (the tests, and the end of the bot).</summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _unsent) > 0)
        {
            if (DateTime.UtcNow > end) return false;
            await Task.Delay(5).ConfigureAwait(false);
        }
        return true;
    }

    private async Task SendAsync()
    {
        var token = _cancel.Token;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var embed))
                {
                    await PostAsync(embed, token).ConfigureAwait(false);
                    Interlocked.Decrement(ref _unsent);
                    if (Spacing > TimeSpan.Zero) await Task.Delay(Spacing, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Discord bot: the community log stopped: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    private async Task PostAsync(BotWebhookEmbed embed, CancellationToken token)
    {
        try
        {
            var policy = _policy();
            var url = Webhook();
            if (policy is not { CommunityEnabled: true } || url is null) return;
            var name = string.IsNullOrWhiteSpace(policy.CommunityName) ? null : policy.CommunityName;
            var avatar = string.IsNullOrWhiteSpace(policy.CommunityAvatarUrl) ? null : policy.CommunityAvatarUrl;
            await _client.SendAsync(url, new BotWebhookMessage(name, avatar, null, embed), token).WaitAsync(SendTimeout, token).ConfigureAwait(false);
            _failing = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Once per outage a warning, then quiet: a deleted webhook must not fill the log with every message.
            if (!_failing) _logger?.LogWarning("Discord bot: the community log webhook refused a message: {Error}", DiscordNetLog.Describe(ex));
            _failing = true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
