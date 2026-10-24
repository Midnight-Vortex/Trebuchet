using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Tells the audit channel (if one is set) everything the bot does: each request that was asked, done, refused, slowed
/// down or cancelled, who asked and for which server, and what the bot noticed itself (a server that went down, the bot
/// that connected or lost Discord). Texts are queued and sent one by one with a pause between them, so a burst stays
/// under the limits of Discord, and what cannot be sent (no connection) is dropped: it is in the audit log anyway. The
/// exact reason of a refusal stays in the log file, as for the user who was refused. No token and no free text from
/// Discord can get in: names and texts come cleaned from the audit log.
/// </summary>
public sealed partial class BotAuditChannelFeed : IDisposable
{
    /// <summary>More than this waiting at once: the oldest are dropped.</summary>
    public const int QueueLimit = 100;
    public static readonly TimeSpan DefaultSpacing = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(10);

    private readonly BotAuditLog _audit;
    private readonly IBotChannelClient _channel;
    private readonly Func<BotPolicy?> _policy;
    private readonly Func<int, string?> _serverName;
    private readonly IBotWebhookClient? _webhooks;
    private readonly Func<string?>? _webhookUrl;
    private readonly ILogger? _logger;
    private readonly Channel<string> _queue;
    private readonly CancellationTokenSource _cancel = new();
    private Task? _sender;
    private int _disposed;
    private int _unsent;
    private readonly object _webhookLock = new();
    private bool _webhookFailing;
    private (string? Url, DateTime AtUtc) _cachedWebhook;

    /// <param name="webhooks">
    /// With a webhook URL (<paramref name="webhookUrl"/>) the lines are sent through it, under the name and with the picture
    /// of the settings. Without, the bot writes them into the audit channel under its own name.
    /// </param>
    public BotAuditChannelFeed(BotAuditLog audit, IBotChannelClient channel, Func<BotPolicy?> policy, Func<int, string?> serverName,
        ILogger? logger = null, IBotWebhookClient? webhooks = null, Func<string?>? webhookUrl = null)
    {
        _webhooks = webhooks;
        _webhookUrl = webhookUrl;
        _queue = Channel.CreateBounded<string>(
            new BoundedChannelOptions(QueueLimit) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Decrement(ref _unsent));
        _audit = audit;
        _channel = channel;
        _policy = policy;
        _serverName = serverName;
        _logger = logger;
    }

    /// <summary>Pause between two messages.</summary>
    public TimeSpan Spacing { get; init; } = DefaultSpacing;

    /// <summary>How long the webhook read from its protected file is used before it is read again.</summary>
    public TimeSpan WebhookCacheTime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The audit webhook, null when none is set or it can not be read.</summary>
    private string? Webhook()
    {
        if (_webhooks is null || _webhookUrl is null) return null;
        var now = DateTime.UtcNow;
        lock (_webhookLock)
        {
            if (now - _cachedWebhook.AtUtc < WebhookCacheTime) return _cachedWebhook.Url;
        }
        string? url;
        try
        {
            url = _webhookUrl();
        }
        catch (Exception)
        {
            url = null;
        }
        lock (_webhookLock) _cachedWebhook = (url, now);
        return url;
    }

    // A line goes out when there is a channel to write in or a webhook that knows its own.
    private bool IsActive(BotPolicy? policy) => policy is not null && (policy.AuditChannelId != 0 || Webhook() is not null);

    public void Start()
    {
        if (_sender is not null) return;
        _audit.EntryWritten += OnEntry;
        _sender = Task.Run(SendAsync);
    }

    /// <summary>A line of the bot itself (server state, connection). Dropped at once when there is no audit channel.</summary>
    public void Post(string text)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var policy = _policy();
            if (!IsActive(policy)) return;
            var clean = BotInputRules.SanitizeNote(text);
            if (clean.Length == 0) return;
            Interlocked.Increment(ref _unsent);
            if (!_queue.Writer.TryWrite(clean)) Interlocked.Decrement(ref _unsent);
        }
        catch (Exception)
        {
            // A note is never worth an error in the bot.
        }
    }

    private void OnEntry(object? sender, BotAuditEntry entry) => Post(Format(entry));

    /// <summary>A server changed its state: told in words, with the name of the server.</summary>
    public void PostStateChange(BotServerStatus status, BotServerState before)
    {
        var language = _policy()?.Language;
        Post(BotAuditTexts.For(language).StateChanged(status.Name, status.Instance, before, status.State, BotTexts.For(language)));
    }

    public void PostConnected() => Post(BotAuditTexts.For(_policy()?.Language).Connected);

    public void PostProblem(DiscordBotProblem problem) => Post(BotAuditTexts.For(_policy()?.Language).ProblemText(problem));

    /// <summary>
    /// One line for the channel, in the language of the bot: who asked for what, for which server (by name), and how it
    /// ended. Empty when the entry adds nothing.
    /// </summary>
    public string Format(BotAuditEntry entry)
    {
        var language = _policy()?.Language;
        string? name = null;
        if (InstancePattern().Match(entry.Parameters) is { Success: true } match
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var instance))
            name = _serverName(instance);
        return BotAuditTexts.For(language).Format(entry, name, language);
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
                while (_queue.Reader.TryRead(out var text))
                {
                    await PostAsync(text, token).ConfigureAwait(false);
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
            _logger?.LogWarning(ex, "The audit channel feed stopped");
        }
    }

    private async Task PostAsync(string text, CancellationToken token)
    {
        try
        {
            var policy = _policy();
            if (policy is null) return;
            if (Webhook() is { } url && _webhooks is not null)
            {
                // Under the name and with the picture of the settings: a bot account can not take another one per message.
                var name = string.IsNullOrWhiteSpace(policy.AuditName) ? null : policy.AuditName;
                var avatar = string.IsNullOrWhiteSpace(policy.AuditAvatarUrl) ? null : policy.AuditAvatarUrl;
                await _webhooks.SendAsync(url, new BotWebhookMessage(name, avatar, text, null), token).WaitAsync(PostTimeout, token).ConfigureAwait(false);
                _webhookFailing = false;
                return;
            }
            if (policy.AuditChannelId == 0) return;
            await _channel.PostNoteAsync(policy.AuditChannelId, text).WaitAsync(PostTimeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No connection, no permission, a webhook that was deleted: dropped, the audit log has it. A webhook that stops
            // working is worth one warning (not one per line).
            if (!_webhookFailing && Webhook() is not null)
                _logger?.LogWarning("Discord bot: the audit webhook refused a line: {Error}", DiscordNetLog.Describe(ex));
            _webhookFailing = Webhook() is not null;
            _logger?.LogDebug("Posting to the audit channel failed: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _audit.EntryWritten -= OnEntry;
        _queue.Writer.TryComplete();
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [GeneratedRegex(@"\binstance=(\d{1,3})\b")]
    private static partial Regex InstancePattern();
}
