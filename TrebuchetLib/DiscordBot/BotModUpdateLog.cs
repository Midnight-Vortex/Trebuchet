using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>A mod of the server mod lists as Steam knows it now.</summary>
/// <param name="TimeUpdated">When the author last updated it (unix seconds).</param>
/// <param name="ContentId">The version of its file: it changes with an upload of the author, not with a new picture or text (0: Steam sent none).</param>
/// <param name="NeedsUpdate">The installed file is older than this version (a mod that is not installed at all is not): the servers restart to install it.</param>
public sealed record BotModVersion(ulong Id, string Title, ulong TimeUpdated, ulong ContentId, string PreviewUrl, bool NeedsUpdate = false);

/// <summary>What was seen of a mod: its version, when it was updated, and the version the players were told about (0: none yet).</summary>
public sealed record BotModSeen(ulong Content, ulong Time, ulong Told = 0);

/// <summary>What the mod update log needs of the launcher. A fake replaces it in the tests.</summary>
public interface IBotModUpdates
{
    /// <summary>The versions of the mods of the server mod lists, after every check for updates.</summary>
    event EventHandler<IReadOnlyList<BotModVersion>>? ModVersionsChecked;

    /// <summary>The latest change note of a mod: null when Steam could not be asked, a note with no text when the author wrote none.</summary>
    Task<WorkshopChange?> GetChangeAsync(ulong modId, CancellationToken token);
}

/// <summary>The texts of the mod update messages: what the players read when a mod of the server got an update.</summary>
public sealed class BotModUpdateTexts
{
    /// <summary>{0} the name of the mod.</summary>
    public required string Title { get; init; }
    /// <summary>{0} the unix time of the update.</summary>
    public required string Updated { get; init; }
    public required string NotesHeader { get; init; }
    public required string NoNotes { get; init; }
    /// <summary>{0} the link to all change notes.</summary>
    public required string AllNotes { get; init; }
    public required string MoreTitle { get; init; }

    public static BotModUpdateTexts German { get; } = new()
    {
        Title = "🔄 Update für Mod: {0}",
        Updated = "Aktualisiert <t:{0}:f> (<t:{0}:R>)",
        NotesHeader = "**Patch-Notes:**",
        NoNotes = "*Der Autor hat keine Patch-Notes hinterlegt.*",
        AllNotes = "[Alle Änderungsnotizen]({0})",
        MoreTitle = "🔄 Weitere Mod-Updates"
    };

    public static BotModUpdateTexts English { get; } = new()
    {
        Title = "🔄 Update for mod: {0}",
        Updated = "Updated <t:{0}:f> (<t:{0}:R>)",
        NotesHeader = "**Patch notes:**",
        NoNotes = "*The author left no patch notes.*",
        AllNotes = "[All change notes]({0})",
        MoreTitle = "🔄 More mod updates"
    };

    public static BotModUpdateTexts For(string? language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? English : German;
}

/// <summary>Steam writes change notes in BBCode; Discord reads Markdown. Links are shown as text and domain, never as links.</summary>
public static partial class BotModNotes
{
    public static string ToMarkdown(string? notes, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(notes)) return string.Empty;
        // Only the beginning is shown: the work of the patterns below grows with the square of the length for a note made of tags.
        if (notes.Length > maxLength * 4) notes = notes[..(maxLength * 4)];
        var text = WebUtility.HtmlDecode(notes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = Headline().Replace(text, "**$1**");
        text = Bold().Replace(text, "**$1**");
        text = Italic().Replace(text, "*$1*");
        text = Underline().Replace(text, "__$1__");
        text = Strike().Replace(text, "~~$1~~");
        text = Code().Replace(text, "`$1`");
        // A link is shown as its text and the domain it goes to, like a person would read it aloud: nothing to click in a message
        // that a mod author wrote.
        text = LinkWithText().Replace(text, m => DomainOf(m.Groups[1].Value) is { } domain ? $"{m.Groups[2].Value} [{domain}]" : m.Groups[2].Value);
        text = LinkOnly().Replace(text, m => DomainOf(m.Groups[1].Value) ?? string.Empty);
        text = Images().Replace(text, string.Empty);
        text = ListItem().Replace(text, "• ");
        text = AnyTag().Replace(text, string.Empty);
        // A link an author wrote in Markdown is not one here either: "](" is shown as text (a link the players read is never other than it says).
        text = MaskedLink().Replace(text, "]\\(");
        text = ExtraLines().Replace(text, "\n\n");
        text = string.Join('\n', text.Split('\n').Select(x => x.TrimEnd())).Trim();
        return Shorten(text, maxLength);
    }

    private static string? DomainOf(string link) =>
        Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.Host : null;

    private static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text[..max];
        // Not in the middle of a surrogate pair, and at a break when there is one near the end.
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        var at = Math.Max(cut.LastIndexOf('\n'), cut.LastIndexOf(' '));
        if (at > max * 2 / 3) cut = cut[..at];
        return cut.TrimEnd() + "…";
    }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"\[h[1-6]\](.*?)\[/h[1-6]\]", Options)] private static partial Regex Headline();
    [GeneratedRegex(@"\[b\](.*?)\[/b\]", Options)] private static partial Regex Bold();
    [GeneratedRegex(@"\[i\](.*?)\[/i\]", Options)] private static partial Regex Italic();
    [GeneratedRegex(@"\[u\](.*?)\[/u\]", Options)] private static partial Regex Underline();
    [GeneratedRegex(@"\[strike\](.*?)\[/strike\]", Options)] private static partial Regex Strike();
    [GeneratedRegex(@"\[(?:code|noparse)\](.*?)\[/(?:code|noparse)\]", Options)] private static partial Regex Code();
    [GeneratedRegex(@"\[url=([^\]\s]+)\](.*?)\[/url\]", Options)] private static partial Regex LinkWithText();
    [GeneratedRegex(@"\[url\](.*?)\[/url\]", Options)] private static partial Regex LinkOnly();
    [GeneratedRegex(@"\[img[^\]]*\].*?\[/img\]|\[previewyoutube=[^\]]*\]\[/previewyoutube\]|\[hr\]", Options)] private static partial Regex Images();
    [GeneratedRegex(@"\[\*\]\s*", Options)] private static partial Regex ListItem();
    // Only the tags of Steam's BBCode: "[Fix]" in a note is text, not a tag.
    [GeneratedRegex(@"\[/?(?:h[1-6]|b|i|u|strike|spoiler|noparse|hr|url|list|olist|quote|code|table|tr|th|td|tbody|img|previewyoutube|p|center|left|right|dynamiclink|steamgroup)(?:=[^\]]*)?\]", Options)] private static partial Regex AnyTag();
    [GeneratedRegex(@"\]\(", Options)] private static partial Regex MaskedLink();
    [GeneratedRegex(@"\n{3,}", Options)] private static partial Regex ExtraLines();
}

/// <summary>Remembers which version of each mod was seen last (and when it was updated), to survive a restart. Not a secret.</summary>
public sealed class BotModUpdateStore(string? path)
{
    internal string? FilePath => path;

    /// <returns>Null when nothing was remembered yet (the first time): the first look is the baseline, it tells nothing.</returns>
    public Dictionary<ulong, BotModSeen>? Load()
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<ulong, BotModSeen>>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <returns>False when it could not be written (the next look then sees the same updates once more).</returns>
    public bool Save(IReadOnlyDictionary<ulong, BotModSeen> versions)
    {
        if (path is null) return true;
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(versions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// The mod update log: tells the players when a mod of the server mod lists got an update, with the notes its author wrote, through a
/// webhook of its own (which may belong to another Discord server, like the one of the community log).
/// </summary>
/// <remarks>
/// It looks at every check for updates of the launcher and compares the time of the last update of each mod with the one it
/// saw before. The first look only remembers (nothing is told for what is old), and the versions are remembered also while the log is off,
/// so switching it on does not tell old updates. Like the community log it is a convenience for the players: bounded, dropped when
/// Discord refuses, and nothing waits for it.
/// </remarks>
public sealed class BotModUpdateLog : IDisposable
{
    public const int QueueLimit = 50;
    public static readonly TimeSpan DefaultSpacing = TimeSpan.FromMilliseconds(1200);
    // An update older than this is no news (a mod that came back to a list, a bot that was away for days).
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(48);
    // The note of an update is written when it is uploaded; an older note belongs to an earlier update.
    internal static readonly TimeSpan NoteTolerance = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan NoteWait = TimeSpan.FromSeconds(20);
    internal const int MaxPerLook = 10;
    // More mods than this are not kept in mind (the oldest go first).
    internal const int MaxRemembered = 500;
    internal const int MaxNoteLength = 700;
    private const uint Gold = 0xF1A33B;

    private readonly IBotModUpdates _updates;
    private readonly IBotWebhookClient _client;
    private readonly Func<string?> _webhook;
    private readonly Func<BotPolicy?> _policy;
    private readonly BotModUpdateStore _store;
    private readonly Func<DateTime> _clock;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    // One look at a time: two checks that end together must not tell the same update twice.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<BotWebhookEmbed> _queue;
    private readonly CancellationTokenSource _cancel = new();
    private Task? _sender;
    private int _disposed;
    private int _unsent;
    private int _running;
    private bool _failing;
    private (string? Url, DateTime AtUtc) _cachedWebhook;
    // What was seen is kept here (the file only lets it survive a restart): a file that can not be written must not make the same update
    // look new at every look.
    private Dictionary<ulong, BotModSeen>? _known;
    private bool _loaded;

    public BotModUpdateLog(IBotModUpdates updates, IBotWebhookClient client, Func<string?> webhook, Func<BotPolicy?> policy,
        BotModUpdateStore store, Func<DateTime>? clock = null, ILogger? logger = null)
    {
        _updates = updates;
        _client = client;
        _webhook = webhook;
        _policy = policy;
        _store = store;
        _clock = clock ?? (() => DateTime.UtcNow);
        _logger = logger;
        _queue = Channel.CreateBounded<BotWebhookEmbed>(
            new BoundedChannelOptions(QueueLimit) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Decrement(ref _unsent));
    }

    /// <summary>Pause between two messages.</summary>
    public TimeSpan Spacing { get; init; } = DefaultSpacing;

    /// <summary>How long one message may take in all (asking the webhook, sending, waiting for the limit of Discord).</summary>
    public TimeSpan SendBudget { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the webhook read from its protected file is used before it is read again.</summary>
    public TimeSpan WebhookCacheTime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the note of a mod is waited for.</summary>
    public TimeSpan NoteTimeout { get; init; } = NoteWait;

    public void Start()
    {
        if (_sender is not null) return;
        _sender = Task.Run(SendAsync);
        _updates.ModVersionsChecked += OnChecked;
    }

    // Runs on the thread of the check for updates: only hands the versions on.
    private void OnChecked(object? sender, IReadOnlyList<BotModVersion> versions)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Increment(ref _running);
        _ = Task.Run(async () =>
        {
            try
            {
                await LookAsync(versions, _cancel.Token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        });
    }

    /// <summary>
    /// One look at the versions: remembers them and tells the updates since the last look. Never throws.
    /// </summary>
    public async Task LookAsync(IReadOnlyList<BotModVersion> versions, CancellationToken token)
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
            var policy = _policy();
            if (!_loaded)
            {
                _known = _store.Load();
                _loaded = true;
            }
            var known = _known;
            var enabled = policy is { ModUpdatesEnabled: true } && Webhook() is not null;
            var now = _clock();
            // What was seen before stays (a mod that is missing from one answer of Steam is not forgotten), what is seen now replaces it.
            var current = known is null ? [] : new Dictionary<ulong, BotModSeen>(known);
            var news = new List<BotModVersion>();
            foreach (var mod in versions)
            {
                // No file from Steam (an error, or it left the workshop) says nothing about a new version.
                if (mod.Id == 0 || mod.ContentId == 0 || mod.TimeUpdated == 0) continue;
                BotModSeen? before = null;
                known?.TryGetValue(mod.Id, out before);
                var told = before?.Told ?? 0;
                var seen = new BotModSeen(mod.ContentId, mod.TimeUpdated, told);
                // An older answer of Steam (a lagging server) is no new version, and what was seen stays: its newer answer is not one either.
                if (before is not null && mod.TimeUpdated < before.Time) seen = before;
                // An update is a new file (a new picture or text changes the time of the update, not the file) that the players were not told
                // about yet: one the servers still have to install (found at the very first look too: the restart for it is under way), or one
                // that appeared since the last look.
                else if (enabled && mod.ContentId != told && now - FromUnix(mod.TimeUpdated) <= MaxAge
                         && (mod.NeedsUpdate || before is not null && mod.ContentId != before.Content && mod.TimeUpdated > before.Time))
                {
                    news.Add(mod);
                    seen = seen with { Told = mod.ContentId };
                }
                current[mod.Id] = seen;
            }
            foreach (var oldest in current.OrderBy(x => x.Value.Time).Take(Math.Max(0, current.Count - MaxRemembered)).Select(x => x.Key).ToList())
                current.Remove(oldest);
            // What is remembered is what was seen: also while the log is off, so it never tells what happened before it was on.
            if (!Same(known, current))
            {
                _known = current;
                if (!_store.Save(current))
                    _logger?.LogDebug("Discord bot: the versions of the mods could not be remembered");
            }
            if (news.Count == 0 || policy is null) return;

            var texts = BotModUpdateTexts.For(policy.Language);
            foreach (var mod in news.OrderBy(x => x.TimeUpdated).Take(MaxPerLook))
            {
                token.ThrowIfCancellationRequested();
                _logger?.LogInformation("Discord bot: tells the players about the update of mod {Title} ({Id})", Clean(mod.Title), mod.Id);
                Enqueue(Compose(mod, await NoteOfAsync(mod, token).ConfigureAwait(false), texts));
            }
            if (news.Count > MaxPerLook)
            {
                var rest = news.OrderBy(x => x.TimeUpdated).Skip(MaxPerLook).Select(x => Clean(x.Title)).ToList();
                Enqueue(new BotWebhookEmbed(texts.MoreTitle, BotDiscordRenderer.Cut(string.Join(", ", rest), 1500), Gold, null));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Discord bot: the mod update log could not look at the mods: {Error}", DiscordNetLog.Describe(ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool Same(Dictionary<ulong, BotModSeen>? a, Dictionary<ulong, BotModSeen> b) =>
        a is not null && a.Count == b.Count && b.All(x => a.TryGetValue(x.Key, out var other) && other == x.Value);

    // The note of the update: null when Steam could not be asked (the message then has no notes part), a note without text when the
    // author wrote none for this update (the latest note is an older one).
    private async Task<WorkshopChange?> NoteOfAsync(BotModVersion mod, CancellationToken token)
    {
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(NoteTimeout);
            var change = await _updates.GetChangeAsync(mod.Id, wait.Token).ConfigureAwait(false);
            if (change is null) return null;
            var updated = FromUnix(mod.TimeUpdated);
            return (change.AtUtc - updated).Duration() <= NoteTolerance ? change : new WorkshopChange(updated, string.Empty);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug("Discord bot: the note of a mod could not be read: {Error}", DiscordNetLog.Describe(ex));
            return null;
        }
    }

    internal static BotWebhookEmbed Compose(BotModVersion mod, WorkshopChange? change, BotModUpdateTexts texts)
    {
        var inv = CultureInfo.InvariantCulture;
        var body = new StringBuilder(string.Format(inv, texts.Updated, mod.TimeUpdated.ToString(inv)));
        body.Append("\n\n");
        if (change is not null)
        {
            var notes = BotModNotes.ToMarkdown(change.Notes, MaxNoteLength);
            if (notes.Length > 0) body.Append(texts.NotesHeader).Append('\n').Append(notes);
            else body.Append(texts.NoNotes);
            body.Append("\n\n");
        }
        body.Append(string.Format(inv, texts.AllNotes, $"https://steamcommunity.com/sharedfiles/filedetails/changelog/{mod.Id.ToString(inv)}"));
        var thumbnail = Uri.TryCreate(mod.PreviewUrl, UriKind.Absolute, out var preview) && preview.Scheme == Uri.UriSchemeHttps ? preview.AbsoluteUri : null;
        return new BotWebhookEmbed(string.Format(inv, texts.Title, Clean(mod.Title)), body.ToString(), Gold, null,
            $"https://steamcommunity.com/sharedfiles/filedetails/?id={mod.Id.ToString(inv)}", thumbnail);
    }

    // The name of a mod is typed by its author: one line, no control characters.
    private static string Clean(string title)
    {
        var line = new string(title.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return line.Length == 0 ? "?" : line;
    }

    private static DateTime FromUnix(ulong seconds) =>
        DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(seconds, 253402300799UL)).UtcDateTime;

    /// <summary>The webhook is read from its protected file at most every few seconds.</summary>
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
        // Whatever the source is: this URL never reaches a log.
        if (url is not null) BotWebhook.RegisterSecret(url);
        lock (_lock) _cachedWebhook = (url, now);
        return url;
    }

    private void Enqueue(BotWebhookEmbed embed)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Increment(ref _unsent);
        if (!_queue.Writer.TryWrite(embed)) Interlocked.Decrement(ref _unsent);
    }

    /// <summary>Waits until every look under way is done and everything queued was sent or dropped (the tests, and the end of the bot).</summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _unsent) > 0 || Volatile.Read(ref _running) > 0)
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
            _logger?.LogWarning("Discord bot: the mod update log stopped: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    private async Task PostAsync(BotWebhookEmbed embed, CancellationToken token)
    {
        try
        {
            var policy = _policy();
            var url = Webhook();
            if (policy is not { ModUpdatesEnabled: true } || url is null) return;
            var name = string.IsNullOrWhiteSpace(policy.ModUpdatesName) ? null : policy.ModUpdatesName;
            var avatar = string.IsNullOrWhiteSpace(policy.ModUpdatesAvatarUrl) ? null : policy.ModUpdatesAvatarUrl;
            await _client.SendWithinAsync(url, new BotWebhookMessage(name, avatar, null, embed), BotWebhook.AnyServer, SendBudget, token).ConfigureAwait(false);
            _failing = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Once per outage a warning, then quiet: a deleted webhook must not fill the log with every message.
            if (!_failing) _logger?.LogWarning("Discord bot: the mod update webhook refused a message: {Error}", DiscordNetLog.Describe(ex));
            _failing = true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _updates.ModVersionsChecked -= OnChecked;
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
