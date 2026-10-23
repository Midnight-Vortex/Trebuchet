using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Keeps the public status message of every server instance in the admin channel up to date: posts it, edits it when
/// it changed (at most every few seconds), posts a new one when it was deleted. Without an admin channel or without a
/// valid policy it does nothing: the status is only ever shown where the bot was told to.
/// </summary>
public sealed class BotStatusBoard
{
    private sealed class Entry
    {
        public string Signature = string.Empty;
        public DateTime LastWriteUtc;
        public bool Dirty;
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

    /// <summary>Shortest time between two edits of the same message (Discord limits edits).</summary>
    public TimeSpan MinEditInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Call every few seconds and after an action. Never throws.</summary>
    public async Task RefreshAsync(CancellationToken token, bool force = false)
    {
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
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
                return;
            // The messages of another channel are not ours to edit.
            if (channelId != _channelId)
            {
                _entries.Clear();
                _channelId = channelId;
            }

            var statuses = await _actions.GetStatusesAsync(token).ConfigureAwait(false);
            var texts = BotTexts.For(policy.Language);
            var now = _clock();
            foreach (var status in statuses.Take(25))
            {
                token.ThrowIfCancellationRequested();
                await RefreshOneAsync(status, policy, texts, channelId, now, force).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Updating the Discord status messages failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forget the posted messages (the admin channel changed). Takes effect with the next refresh.</summary>
    public void Reset() => Volatile.Write(ref _resetRequested, 1);

    private async Task RefreshOneAsync(BotServerStatus status, BotPolicy policy, BotTexts texts, ulong channelId, DateTime now, bool force)
    {
        try
        {
            var message = BotStatusMessageBuilder.Build(status, policy, texts, now);
            var signature = Signature(message);
            if (!_entries.TryGetValue(status.Instance, out var entry))
                _entries[status.Instance] = entry = new Entry();

            var messageId = _settings.GetStatusMessageId(status.Instance);
            var unchanged = entry.Signature == signature && messageId != 0;
            if (unchanged && !force) return;
            if (!force && messageId != 0 && now - entry.LastWriteUtc < MinEditInterval)
            {
                // Edited a moment ago: the next refresh writes it.
                entry.Dirty = true;
                return;
            }

            if (messageId != 0 && !await _channel.EditStatusAsync(channelId, messageId, message).ConfigureAwait(false))
                messageId = 0;
            if (messageId == 0)
            {
                var posted = await _channel.PostStatusAsync(channelId, message).ConfigureAwait(false);
                if (posted is not { } newId) return;
                _settings.SetStatusMessageId(status.Instance, newId);
            }
            entry.Signature = signature;
            entry.LastWriteUtc = now;
            entry.Dirty = false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Updating the Discord status message of instance {Instance} failed", status.Instance);
        }
    }

    private static string Signature(BotStatusMessage message) => string.Join('\u001f',
        message.Title,
        message.Color.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string.Join('\u001e', message.Fields.Select(f => f.Name + "\u001d" + f.Value + "\u001d" + f.Inline)),
        string.Join('\u001e', message.Buttons.Select(b => b.CustomId + "\u001d" + b.Label + "\u001d" + b.Style)));
}
