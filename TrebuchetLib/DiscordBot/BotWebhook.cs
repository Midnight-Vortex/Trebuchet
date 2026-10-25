using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>The format of a Discord webhook URL, and how it is kept out of logs.</summary>
public static partial class BotWebhook
{
    /// <summary>
    /// For <c>expectedGuildId</c>: the webhook may belong to any Discord server. Only for what the players read (the community log, often
    /// on a server of its own); what is only for the team (the audit log) stays on the server of the bot.
    /// </summary>
    public const ulong AnyServer = ulong.MaxValue;

    /// <summary>
    /// Takes what was pasted (spaces, quotes, a query like <c>?wait=true</c>) and returns the clean URL when it is one.
    /// A numeric <c>thread_id</c> stays (the webhook of a thread or a forum post), everything else of the query goes.
    /// </summary>
    public static bool TryNormalize(string? input, out string url) => TryNormalize(input, out url, out _);

    /// <param name="threadId">The thread the messages go into, null: the channel itself.</param>
    public static bool TryNormalize(string? input, out string url, out ulong? threadId)
    {
        url = string.Empty;
        threadId = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim().Trim('"', '\'').Trim();
        var hash = text.IndexOf('#');
        if (hash >= 0) text = text[..hash];
        var query = string.Empty;
        var cut = text.IndexOf('?');
        if (cut >= 0)
        {
            query = text[(cut + 1)..];
            text = text[..cut];
        }
        var match = UrlPattern().Match(text);
        if (!match.Success) return false;
        url = $"https://discord.com/api/webhooks/{match.Groups[1].Value}/{match.Groups[2].Value}";
        if (ThreadPattern().Match(query) is { Success: true } thread && ulong.TryParse(thread.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0)
        {
            threadId = id;
            url += $"?thread_id={id.ToString(CultureInfo.InvariantCulture)}";
        }
        return true;
    }

    /// <summary>The URL without the thread: the webhook itself.</summary>
    public static string WithoutThread(string url)
    {
        var cut = url.IndexOf('?');
        return cut < 0 ? url : url[..cut];
    }

    /// <summary>A webhook URL anywhere in a text (a name or a link that must never carry one).</summary>
    public static bool Contains(string? text) => !string.IsNullOrEmpty(text) && SearchPattern().IsMatch(text);

    /// <summary>The webhook URL and its secret part are removed from a text before it reaches a log.</summary>
    public static void RegisterSecret(string url)
    {
        BotLogRedactor.RegisterSecret(url);
        BotLogRedactor.RegisterSecret(WithoutThread(url));
        var match = UrlPattern().Match(WithoutThread(url));
        if (match.Success) BotLogRedactor.RegisterSecret(match.Groups[2].Value);
    }

    [GeneratedRegex(@"^https://(?:(?:canary|ptb)\.)?discord(?:app)?\.com/api/webhooks/([0-9]{17,20})/([A-Za-z0-9_-]{20,120})/?$")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"discord(?:app)?\.com/api(?:/v\d+)?/webhooks/[0-9]{17,20}/[A-Za-z0-9_-]{20,}", RegexOptions.IgnoreCase)]
    private static partial Regex SearchPattern();

    [GeneratedRegex(@"(?:^|&)thread_id=([0-9]{1,20})(?:&|$)")]
    private static partial Regex ThreadPattern();
}

/// <summary>An embed of a webhook message: what the community sees (title, text with Discord timestamps, color, time).</summary>
/// <param name="Url">A link the title points to (https), null: none.</param>
/// <param name="ThumbnailUrl">A small picture at the side (https), null: none.</param>
public sealed record BotWebhookEmbed(string Title, string Description, uint Color, DateTime? TimestampUtc, string? Url = null, string? ThumbnailUrl = null);

/// <summary>
/// A message through a webhook. <paramref name="Username"/> and <paramref name="AvatarUrl"/> replace the name and the picture
/// of the webhook for this message (null: the ones set in Discord). Mentions never ping.
/// </summary>
public sealed record BotWebhookMessage(string? Username, string? AvatarUrl, string? Text, BotWebhookEmbed? Embed);

/// <summary>Discord or the webhook refused: the message is dropped. The text is safe for a log (no URL, no token).</summary>
public sealed class BotWebhookException(string message) : Exception(message);

/// <summary>Sends messages through a webhook. A fake replaces it in the tests.</summary>
public interface IBotWebhookClient
{
    /// <param name="expectedGuildId">
    /// The server the webhook must belong to (the server of the bot): a webhook of another server is never written to, so
    /// a webhook left over from an old setup or pasted from a foreign server stays silent. <see cref="BotWebhook.AnyServer"/>:
    /// any server (the webhook is not asked, the message goes out).
    /// </param>
    /// <exception cref="Exception">Discord refused it or there is no connection: the caller logs and drops the message.</exception>
    Task SendAsync(string webhookUrl, BotWebhookMessage message, ulong expectedGuildId, CancellationToken token);
}

public static class BotWebhookClientExtensions
{
    /// <summary>
    /// Sends with one time budget for everything (asking the webhook, the message, waiting for the limit of Discord). When the
    /// time is up the call is cancelled, so a message the caller gave up on is not delivered later.
    /// </summary>
    public static async Task SendWithinAsync(this IBotWebhookClient client, string webhookUrl, BotWebhookMessage message, ulong expectedGuildId,
        TimeSpan budget, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(budget);
        try
        {
            await client.SendAsync(webhookUrl, message, expectedGuildId, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new BotWebhookException("Discord did not answer in time.");
        }
    }
}

/// <summary>
/// The real webhook client: plain HTTPS to the webhook URL with one shared connection (no new client and no blocking call
/// per message). Before the first message the webhook is asked which server it belongs to, and that is remembered.
/// </summary>
public sealed class HttpBotWebhookClient : IBotWebhookClient, IDisposable
{
    private const int MaxKnownWebhooks = 4;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, ulong> _guilds = [];

    public HttpBotWebhookClient(ILogger? logger = null, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        // No redirects: the address is fixed, and an answer from somewhere else is no answer.
        _http = handler is null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DiscordBot", null));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(https://github.com/Midnight-Vortex/Trebuchet)"));
    }

    public async Task SendAsync(string webhookUrl, BotWebhookMessage message, ulong expectedGuildId, CancellationToken token)
    {
        if (!BotWebhook.TryNormalize(webhookUrl, out var url))
            throw new ArgumentException("Not a Discord webhook URL", nameof(webhookUrl));
        BotWebhook.RegisterSecret(url);
        var webhook = BotWebhook.WithoutThread(url);
        try
        {
            if (expectedGuildId != BotWebhook.AnyServer)
            {
                var guild = await GuildOfAsync(webhook, token).ConfigureAwait(false);
                if (expectedGuildId == 0 || guild != expectedGuildId)
                    throw new BotWebhookException("The webhook belongs to another Discord server than the bot's.");
            }
            await PostAsync(url, Payload(message), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BotWebhookException)
        {
            // The text of the exception may carry the URL: only what the redactor leaves.
            _logger?.LogDebug("Discord bot: the webhook could not be reached: {Error}", DiscordNetLog.Describe(ex));
            throw new BotWebhookException("The webhook could not be reached: " + DiscordNetLog.Describe(ex));
        }
    }

    // The webhook tells which server it belongs to (guild_id); one question per webhook, then it is known.
    private async Task<ulong> GuildOfAsync(string webhook, CancellationToken token)
    {
        lock (_lock)
        {
            if (_guilds.TryGetValue(webhook, out var known)) return known;
        }
        using var response = await SendAsync(HttpMethod.Get, webhook, null, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Refused(response.StatusCode, body);
        ulong guild = 0;
        try
        {
            if (JsonNode.Parse(body) is JsonObject { } json && json["guild_id"] is JsonValue value
                && ulong.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                guild = id;
        }
        catch (JsonException)
        {
        }
        if (guild == 0) throw new BotWebhookException("Discord did not say which server the webhook belongs to.");
        lock (_lock)
        {
            if (_guilds.Count >= MaxKnownWebhooks) _guilds.Clear();
            _guilds[webhook] = guild;
        }
        return guild;
    }

    private async Task PostAsync(string url, string payload, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(HttpMethod.Post, url, payload, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            // The limit of Discord is reached: once more after the time it names, if that is short.
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0 && RetryAfter(response, body) is { } wait)
            {
                await Task.Delay(wait, token).ConfigureAwait(false);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                // The webhook is gone: ask again next time (it may have been created again under the same address).
                lock (_lock) _guilds.Remove(BotWebhook.WithoutThread(url));
            }
            throw Refused(response.StatusCode, body);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? payload, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RequestTimeout);
        // The version of the API is named: the unversioned address stands for an old default.
        using var request = new HttpRequestMessage(method, Versioned(url));
        if (payload is not null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new BotWebhookException("Discord did not answer in time.");
        }
    }

    internal static string Versioned(string url) => url.Replace("/api/webhooks/", "/api/v10/webhooks/", StringComparison.Ordinal);

    private static TimeSpan? RetryAfter(HttpResponseMessage response, string body)
    {
        double seconds = -1;
        try
        {
            if (JsonNode.Parse(body) is JsonObject { } json && json["retry_after"] is JsonValue value
                && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                seconds = parsed;
        }
        catch (JsonException)
        {
        }
        if (seconds < 0 && response.Headers.RetryAfter?.Delta is { } delta) seconds = delta.TotalSeconds;
        if (seconds < 0 || double.IsNaN(seconds)) return null;
        var wait = TimeSpan.FromSeconds(seconds + 0.1);
        return wait <= MaxRetryAfter ? wait : null;
    }

    // Only the status and the code of Discord: the answer may name the field that was wrong, never anything secret.
    private static BotWebhookException Refused(HttpStatusCode status, string body)
    {
        var code = string.Empty;
        var message = string.Empty;
        try
        {
            if (JsonNode.Parse(body) is JsonObject json)
            {
                if (json["code"] is JsonValue c) code = " code " + BotInputRules.SanitizeForLog(c.ToString());
                if (json["message"] is JsonValue m) message = ": " + BotDiscordRenderer.Cut(BotInputRules.SanitizeForLog(m.ToString()), 120);
            }
        }
        catch (JsonException)
        {
        }
        return new BotWebhookException($"Discord refused the webhook (HTTP {(int)status}{code}){message}");
    }

    /// <summary>The JSON of a message: name and picture of the message, text or embed, and no mention that pings.</summary>
    internal static string Payload(BotWebhookMessage message)
    {
        var json = new JsonObject();
        if (!string.IsNullOrWhiteSpace(message.Username)) json["username"] = message.Username;
        if (!string.IsNullOrWhiteSpace(message.AvatarUrl)) json["avatar_url"] = message.AvatarUrl;
        if (!string.IsNullOrEmpty(message.Text)) json["content"] = BotDiscordRenderer.Cut(message.Text, 2000);
        if (message.Embed is { } embed) json["embeds"] = new JsonArray(ToEmbed(embed));
        json["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() };
        return json.ToJsonString();
    }

    private static bool IsHttps(string? link) =>
        !string.IsNullOrEmpty(link) && link.Length <= 2000 && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    internal static JsonObject ToEmbed(BotWebhookEmbed embed)
    {
        var json = new JsonObject
        {
            ["title"] = BotDiscordRenderer.Cut(embed.Title, 256),
            ["description"] = BotDiscordRenderer.Cut(embed.Description, 4000),
            ["color"] = (int)(embed.Color & 0xFFFFFF)
        };
        if (embed.TimestampUtc is { } time)
            json["timestamp"] = DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        if (IsHttps(embed.Url)) json["url"] = embed.Url;
        if (IsHttps(embed.ThumbnailUrl)) json["thumbnail"] = new JsonObject { ["url"] = embed.ThumbnailUrl };
        return json;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// A webhook URL lets anybody write into its channel, so it is kept like the token: protected in its own file, never part
/// of the settings, of exports or of logs, and never shown again after saving.
/// </summary>
public sealed class BotWebhookStore
{
    private readonly string _path;
    private readonly ISecretProtector? _protector;

    public BotWebhookStore(string path, ISecretProtector? protector = null)
    {
        _path = path;
        _protector = protector ?? (OperatingSystem.IsWindows() ? new DpapiSecretProtector() : null);
    }

    internal string FilePath => _path;

    public bool HasWebhook => File.Exists(_path);

    public bool CanSave => _protector is not null;

    /// <exception cref="ArgumentException">Not a webhook URL.</exception>
    /// <exception cref="PlatformNotSupportedException">No protection available.</exception>
    public void Save(string input)
    {
        if (!BotWebhook.TryNormalize(input, out var url))
            throw new ArgumentException("This is not a Discord webhook URL.", nameof(input));
        if (_protector is null)
            throw new PlatformNotSupportedException("The webhook can not be stored here.");

        var protectedBytes = _protector.Protect(System.Text.Encoding.UTF8.GetBytes(url));
        var folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, _path, overwrite: true);
        BotWebhook.RegisterSecret(url);
    }

    /// <returns>The URL, or null when none is set or it can not be read (other user, damaged file).</returns>
    public string? Load()
    {
        if (_protector is null || !File.Exists(_path)) return null;
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(_protector.Unprotect(File.ReadAllBytes(_path)));
            if (!BotWebhook.TryNormalize(text, out var url)) return null;
            BotWebhook.RegisterSecret(url);
            return url;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The webhook as a log reads it again and again. A file that exists but can not be read (another Windows user, for
    /// example a bot run as a task, or a damaged file) is told once: otherwise the log stays silent and nobody knows why.
    /// </summary>
    public Func<string?> AsSource(string name, ILogger? logger)
    {
        var failures = 0;
        return () =>
        {
            var url = Load();
            if (url is not null)
            {
                Volatile.Write(ref failures, 0);
                return url;
            }
            // Told at the second look in a row: the file may just be replaced by the settings at this very moment.
            if (HasWebhook && Interlocked.Increment(ref failures) == 2)
                logger?.LogWarning("Discord bot: the {Name} webhook is saved but can not be read here (another Windows user, or a damaged file): save it again in the settings", name);
            return null;
        };
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
    }
}

/// <summary>The three webhooks of the bot: the audit channel, the community channel and the one for mod updates.</summary>
public sealed class BotWebhooks(BotWebhookStore audit, BotWebhookStore community, BotWebhookStore modUpdates, BotModUpdateStore modVersions,
    BotAccountIdentityStore? identity = null)
{
    /// <summary>The files sit next to the file of the token (same folder, same edition).</summary>
    public static BotWebhooks ForTokenPath(string tokenPath) => new(
        new BotWebhookStore(Path.ChangeExtension(tokenPath, ".audit-webhook")),
        new BotWebhookStore(Path.ChangeExtension(tokenPath, ".community-webhook")),
        new BotWebhookStore(Path.ChangeExtension(tokenPath, ".modupdates-webhook")),
        new BotModUpdateStore(Path.ChangeExtension(tokenPath, ".mod-versions")),
        new BotAccountIdentityStore(Path.ChangeExtension(tokenPath, ".identity")));

    public BotWebhookStore Audit { get; } = audit;
    public BotWebhookStore Community { get; } = community;
    public BotWebhookStore ModUpdates { get; } = modUpdates;
    /// <summary>Which version of each mod was seen last (not a secret).</summary>
    public BotModUpdateStore ModVersions { get; } = modVersions;
    /// <summary>Which picture the bot account was given (next to the token, not a secret).</summary>
    public BotAccountIdentityStore? Identity { get; } = identity;
}
