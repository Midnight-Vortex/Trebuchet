using System.Text.RegularExpressions;
using Discord;
using Discord.Webhook;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>The format of a Discord webhook URL, and how it is kept out of logs.</summary>
public static partial class BotWebhook
{
    /// <summary>Takes what was pasted (spaces, quotes, a query like <c>?wait=true</c>) and returns the clean URL when it is one.</summary>
    public static bool TryNormalize(string? input, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim().Trim('"', '\'').Trim();
        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0) text = text[..cut];
        var match = UrlPattern().Match(text);
        if (!match.Success) return false;
        url = $"https://discord.com/api/webhooks/{match.Groups[1].Value}/{match.Groups[2].Value}";
        return true;
    }

    /// <summary>The webhook URL and its secret part are removed from a text before it reaches a log.</summary>
    public static void RegisterSecret(string url)
    {
        BotLogRedactor.RegisterSecret(url);
        var match = UrlPattern().Match(url);
        if (match.Success) BotLogRedactor.RegisterSecret(match.Groups[2].Value);
    }

    [GeneratedRegex(@"^https://(?:(?:canary|ptb)\.)?discord(?:app)?\.com/api/webhooks/([0-9]{17,20})/([A-Za-z0-9_-]{20,120})/?$")]
    private static partial Regex UrlPattern();
}

/// <summary>An embed of a webhook message: what the community sees (title, text with Discord timestamps, color, time).</summary>
public sealed record BotWebhookEmbed(string Title, string Description, uint Color, DateTime? TimestampUtc);

/// <summary>
/// A message through a webhook. <paramref name="Username"/> and <paramref name="AvatarUrl"/> replace the name and the picture
/// of the webhook for this message (null: the ones set in Discord). Mentions never ping.
/// </summary>
public sealed record BotWebhookMessage(string? Username, string? AvatarUrl, string? Text, BotWebhookEmbed? Embed);

/// <summary>Sends messages through a webhook. A fake replaces it in the tests.</summary>
public interface IBotWebhookClient
{
    /// <exception cref="Exception">Discord refused it or there is no connection: the caller logs and drops the message.</exception>
    Task SendAsync(string webhookUrl, BotWebhookMessage message, CancellationToken token);
}

/// <summary>The real webhook client (Discord.Net.Webhook). One short-lived client per message: no state, no leak.</summary>
public sealed class DiscordNetWebhookClient(ILogger? logger = null) : IBotWebhookClient
{
    public async Task SendAsync(string webhookUrl, BotWebhookMessage message, CancellationToken token)
    {
        if (!BotWebhook.TryNormalize(webhookUrl, out var url)) throw new ArgumentException("Not a Discord webhook URL", nameof(webhookUrl));
        using var client = new DiscordWebhookClient(url);
        var embeds = message.Embed is { } e ? new[] { ToEmbed(e) } : null;
        try
        {
            await client.SendMessageAsync(
                text: string.IsNullOrEmpty(message.Text) ? null : message.Text,
                embeds: embeds,
                username: string.IsNullOrWhiteSpace(message.Username) ? null : message.Username,
                avatarUrl: string.IsNullOrWhiteSpace(message.AvatarUrl) ? null : message.AvatarUrl,
                options: new RequestOptions { CancelToken = token },
                allowedMentions: BotDiscordRenderer.NoMentions()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The text of the exception may carry the URL: only what the redactor leaves.
            logger?.LogDebug("Discord bot: the webhook refused a message: {Error}", DiscordNetLog.Describe(ex));
            throw;
        }
    }

    internal static Embed ToEmbed(BotWebhookEmbed embed)
    {
        var builder = new EmbedBuilder()
            .WithTitle(BotDiscordRenderer.Cut(embed.Title, 256))
            .WithDescription(BotDiscordRenderer.Cut(embed.Description, 4000))
            .WithColor(new Color(embed.Color));
        if (embed.TimestampUtc is { } time) builder.WithTimestamp(new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)));
        return builder.Build();
    }
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

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
    }
}

/// <summary>The two webhooks of the bot: the audit channel and the community channel.</summary>
public sealed class BotWebhooks(BotWebhookStore audit, BotWebhookStore community)
{
    /// <summary>The two files sit next to the file of the token (same folder, same edition).</summary>
    public static BotWebhooks ForTokenPath(string tokenPath) => new(
        new BotWebhookStore(Path.ChangeExtension(tokenPath, ".audit-webhook")),
        new BotWebhookStore(Path.ChangeExtension(tokenPath, ".community-webhook")));

    public BotWebhookStore Audit { get; } = audit;
    public BotWebhookStore Community { get; } = community;
}
