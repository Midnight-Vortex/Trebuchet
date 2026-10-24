using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Keeps the public status message of every server instance in the admin channel up to date: posts it once and edits
/// that one message from then on (state, buttons and the time of the check), posts a new one when it was deleted.
/// A change is written at once (at most every few seconds, Discord limits edits), and the message is edited at least
/// every <see cref="HeartbeatInterval"/> even when nothing changed, so its time shows that the check still runs and a
/// deleted message is noticed. While a server starts or stops it is read more often. Without an admin channel or
/// without a valid policy it does nothing: the status is only ever shown where the bot was told to.
/// </summary>
public sealed class BotStatusBoard
{
    private sealed class Entry
    {
        public string Signature = string.Empty;
        public DateTime LastWriteUtc;
        public int Failures;
        public DateTime RetryAfterUtc;
        /// <summary>The state the audit channel was told last (null: not seen yet).</summary>
        public BotServerState? LastState;
        /// <summary>A state that differs from <see cref="LastState"/> and since when it holds.</summary>
        public (BotServerState State, DateTime Since)? Candidate;
    }

    private readonly IBotActions _actions;
    private readonly IBotChannelClient _channel;
    private readonly IBotSettings _settings;
    private readonly Func<DateTime> _clock;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, Entry> _entries = [];
    private ulong _channelId;
    private int _resetRequested;

    public BotStatusBoard(IBotActions actions, IBotChannelClient channel, IBotSettings settings, Func<DateTime>? clock = null,
        ILogger? logger = null)
    {
        _actions = actions;
        _channel = channel;
        _settings = settings;
        _clock = clock ?? (() => DateTime.UtcNow);
        _logger = logger;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _names = new();

    /// <summary>The name the board shows for a server (the last one it read), null while it has not seen it.</summary>
    public string? NameOf(int instance) => _names.TryGetValue(instance, out var name) ? name : null;

    /// <summary>
    /// A server changed its state (the state before is the second value). Called on the thread of the refresh, once per
    /// change that held for <see cref="SettleTime"/>.
    /// </summary>
    public Action<BotServerStatus, BotServerState>? Notes { get; init; }

    /// <summary>How long a new state has to hold before the audit channel is told: a flicker between two steps is not news.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Shortest time between two edits of the same message (Discord limits edits).</summary>
    public TimeSpan MinEditInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest time a message stays untouched: the regular check of a calm server.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the state is read while a server starts or stops. Changes also arrive as events: this only makes up
    /// for one that got lost.
    /// </summary>
    public TimeSpan BusyInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Call when something changed (an action, a state change of a server) and when the time it returned is over. Never
    /// throws. Returns when the next call is due at the latest.
    /// </summary>
    public async Task<TimeSpan> RefreshAsync(CancellationToken token, bool force = false)
    {
        var next = HeartbeatInterval;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return next;
        }

        try
        {
            if (Interlocked.Exchange(ref _resetRequested, 0) == 1)
            {
                _entries.Clear();
                _channelId = 0;
            }
            var policy = _settings.Policy;
            var channelId = _settings.AdminChannelId;
            if (policy is null || channelId == 0 || !BotId.IsValid(channelId))
                return next;
            // The messages of another channel are not ours to edit.
            if (channelId != _channelId)
            {
                _entries.Clear();
                _channelId = channelId;
            }

            var statuses = await _actions.GetStatusesAsync(token).ConfigureAwait(false);
            // Nothing came back although there are servers: the launcher did not answer, ask again soon.
            if (statuses.Count == 0 && _actions.InstanceCount > 0) next = BusyInterval;
            var texts = BotTexts.For(policy.Language);
            var now = _clock();
            // Only the servers chosen in the settings, under the names chosen there.
            foreach (var status in statuses.Where(x => policy.IsManaged(x.Instance)).Take(25))
            {
                token.ThrowIfCancellationRequested();
                _names[status.Instance] = status.Name;
                var due = await RefreshOneAsync(status, texts, channelId, now, force).ConfigureAwait(false);
                if (due < next) next = due;
            }
            var retired = await RetireUnmanagedAsync(policy, texts, channelId, now).ConfigureAwait(false);
            if (retired < next) next = retired;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Updating the Discord status messages failed");
            next = BusyInterval;
        }
        finally
        {
            _gate.Release();
        }
        return next;
    }

    /// <summary>Forget the posted messages (the admin channel changed). Takes effect with the next refresh.</summary>
    public void Reset() => Volatile.Write(ref _resetRequested, 1);

    // Returns when this message has to be looked at again.
    private async Task<TimeSpan> RefreshOneAsync(BotServerStatus status, BotTexts texts, ulong channelId, DateTime now, bool force)
    {
        var settle = TellStateChange(status, now);
        var due = await RefreshMessageAsync(status, texts, channelId, now, force).ConfigureAwait(false);
        return settle is { } wait && wait < due ? wait : due;
    }

    /// <summary>
    /// A state that holds for <see cref="SettleTime"/> is told once, with the name of the server. A flicker (a server that is
    /// "stopped" for a moment between two steps of its stop) is not: it would be told three times for one stop.
    /// </summary>
    /// <returns>When the state has to be looked at again to decide, null when nothing is undecided.</returns>
    private TimeSpan? TellStateChange(BotServerStatus status, DateTime now)
    {
        if (!_entries.TryGetValue(status.Instance, out var entry))
            _entries[status.Instance] = entry = new Entry();
        if (entry.LastState is not { } told)
        {
            // The first look tells nothing, it is the starting point.
            entry.LastState = status.State;
            return null;
        }
        if (status.State == told)
        {
            entry.Candidate = null;
            return null;
        }
        if (entry.Candidate is not { } candidate || candidate.State != status.State)
            entry.Candidate = candidate = (status.State, now);
        var held = now - candidate.Since;
        if (held < SettleTime) return AtLeastASecond(SettleTime - held);
        entry.LastState = status.State;
        entry.Candidate = null;
        Notes?.Invoke(status, told);
        return null;
    }

    private async Task<TimeSpan> RefreshMessageAsync(BotServerStatus status, BotTexts texts, ulong channelId, DateTime now, bool force)
    {
        try
        {
            var message = BotStatusMessageBuilder.Build(status, texts, now);
            var signature = Signature(message);
            if (!_entries.TryGetValue(status.Instance, out var entry))
                _entries[status.Instance] = entry = new Entry();
            // A server that is starting or stopping is read more often, a calm one every few minutes.
            var calm = status.State is BotServerState.Starting or BotServerState.Stopping ? BusyInterval : HeartbeatInterval;

            // A message that keeps failing (no permission, deleted channel) is not tried again every refresh.
            if (!force && now < entry.RetryAfterUtc) return AtLeastASecond(entry.RetryAfterUtc - now);

            var messageId = _settings.GetStatusMessageId(status.Instance);
            var changed = entry.Signature != signature || messageId == 0;
            var untilStale = entry.LastWriteUtc + HeartbeatInterval - now;
            // Nothing new and not yet time for the regular edit.
            if (!force && !changed && untilStale > TimeSpan.Zero) return AtLeastASecond(untilStale < calm ? untilStale : calm);
            if (!force && messageId != 0 && now - entry.LastWriteUtc < MinEditInterval)
            {
                // Edited a moment ago: it is written as soon as the interval is over.
                return AtLeastASecond(MinEditInterval - (now - entry.LastWriteUtc));
            }

            if (messageId != 0)
            {
                var edit = await _channel.EditStatusAsync(channelId, messageId, message).ConfigureAwait(false);
                // A failure that is not "the message is gone" (rate limit, network) must not post a duplicate: try again.
                if (edit == BotEditResult.Failed)
                {
                    BackOff(entry, now);
                    return AtLeastASecond(entry.RetryAfterUtc - now);
                }
                if (edit == BotEditResult.Gone) messageId = 0;
            }
            if (messageId == 0)
            {
                var posted = await _channel.PostStatusAsync(channelId, message).ConfigureAwait(false);
                if (posted is not { } newId)
                {
                    BackOff(entry, now);
                    return AtLeastASecond(entry.RetryAfterUtc - now);
                }
                _settings.SetStatusMessageId(status.Instance, newId);
            }
            entry.Signature = signature;
            entry.LastWriteUtc = now;
            entry.Failures = 0;
            entry.RetryAfterUtc = default;
            return calm;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Updating the Discord status message of instance {Instance} failed", status.Instance);
            if (!_entries.TryGetValue(status.Instance, out var failed)) return BusyInterval;
            BackOff(failed, now);
            return AtLeastASecond(failed.RetryAfterUtc - now);
        }
    }

    // A server that was taken out of the settings keeps its old message: it is replaced by a note without buttons, once.
    private async Task<TimeSpan> RetireUnmanagedAsync(BotPolicy policy, BotTexts texts, ulong channelId, DateTime now)
    {
        var next = HeartbeatInterval;
        if (policy.ManagedInstances is null) return next;
        for (var instance = 0; instance < _actions.InstanceCount && instance <= BotInputRules.MaxInstance; instance++)
        {
            if (policy.IsManaged(instance)) continue;
            var messageId = _settings.GetStatusMessageId(instance);
            if (messageId == 0) continue;
            if (!_entries.TryGetValue(instance, out var entry)) _entries[instance] = entry = new Entry();
            if (now < entry.RetryAfterUtc)
            {
                next = Min(next, AtLeastASecond(entry.RetryAfterUtc - now));
                continue;
            }
            var edit = await _channel.EditStatusAsync(channelId, messageId,
                new BotStatusMessage(texts.NotManagedTitle, 0x95A5A6, [], [], texts.UpdatedFooter, now)).ConfigureAwait(false);
            if (edit == BotEditResult.Failed)
            {
                BackOff(entry, now);
                next = Min(next, AtLeastASecond(entry.RetryAfterUtc - now));
                continue;
            }
            _settings.SetStatusMessageId(instance, 0);
            _entries.Remove(instance);
        }
        return next;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan AtLeastASecond(TimeSpan span) => span < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : span;

    /// <summary>30 seconds, doubling up to 10 minutes: a lasting problem costs a request now and then, not every refresh.</summary>
    private static void BackOff(Entry entry, DateTime now)
    {
        entry.Failures++;
        var seconds = Math.Min(600, 30 * Math.Pow(2, Math.Min(entry.Failures - 1, 5)));
        entry.RetryAfterUtc = now + TimeSpan.FromSeconds(seconds);
    }

    private static string Signature(BotStatusMessage message) => string.Join('\u001f',
        message.Title,
        message.Color.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string.Join('\u001e', message.Fields.Select(f => f.Name + "\u001d" + f.Value + "\u001d" + f.Inline)),
        string.Join('\u001e', message.Buttons.Select(b => b.CustomId + "\u001d" + b.Label + "\u001d" + b.Style + "\u001d" + b.Disabled)));
}
