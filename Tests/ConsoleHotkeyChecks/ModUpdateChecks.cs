using System.Text.Json.Nodes;
using TrebuchetLib;
using TrebuchetLib.DiscordBot;

/// <summary>
/// The mod update log: what the players read when a mod of the server mod lists got an update (the notes of its author as Discord text,
/// the message, the look at every check for updates) and that it is never told twice, never for old news, and never disturbs anything.
/// </summary>
static class ModUpdateChecks
{
    private const ulong Operator = 200000000000000002;
    private const ulong Guild = 100000000000000001;
    private const string WebhookUrl = "https://discord.com/api/webhooks/223456789012345678/abcdefghijklmnopqrstuvwxyz0123456789ABCE";

    private sealed class FakeWebhook : IBotWebhookClient
    {
        public List<(string Url, BotWebhookMessage Message, ulong Guild)> Sent { get; } = [];
        public Exception? Failure { get; set; }

        public Task SendAsync(string webhookUrl, BotWebhookMessage message, ulong expectedGuildId, CancellationToken token)
        {
            if (Failure is not null) throw Failure;
            lock (Sent) Sent.Add((webhookUrl, message, expectedGuildId));
            return Task.CompletedTask;
        }

        public BotWebhookEmbed[] Embeds
        {
            get { lock (Sent) return Sent.Select(x => x.Message.Embed!).ToArray(); }
        }
    }

    private sealed class FakeUpdates : IBotModUpdates
    {
        public event EventHandler<IReadOnlyList<BotModVersion>>? ModVersionsChecked;
        public Dictionary<ulong, WorkshopChange?> Notes { get; } = [];
        public List<ulong> Asked { get; } = [];
        public bool Hang { get; set; }
        public void Raise(IReadOnlyList<BotModVersion> versions) => ModVersionsChecked?.Invoke(this, versions);
        public int Listeners => ModVersionsChecked?.GetInvocationList().Length ?? 0;

        public async Task<WorkshopChange?> GetChangeAsync(ulong modId, CancellationToken token)
        {
            lock (Asked) Asked.Add(modId);
            if (Hang) await Task.Delay(Timeout.Infinite, token);
            return Notes.TryGetValue(modId, out var note) ? note : null;
        }
    }

    private sealed class LogLines : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static BotPolicy PolicyOf(Action<DiscordBotConfig>? change = null)
    {
        var config = new DiscordBotConfig { GuildId = Guild, SuperAdminUserId = Operator };
        config.AddRole(300000000000000003);
        config.SetAdminChannel(400000000000000004);
        change?.Invoke(config);
        var created = BotPolicy.Create(config);
        return created.Policy ?? throw new Exception("settings of the check are invalid: " + string.Join(", ", created.Issues.Select(x => x.Kind)));
    }

    private static long Unix(DateTime time) => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var folder = Path.Combine(Path.GetTempPath(), "tb-modupdates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Notes(Check);
            Message(Check);
            await LookAsync(Check, folder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        return count;
    }

    // Steam's BBCode as Discord text.
    private static void Notes(Action<bool, string> check)
    {
        check(BotModNotes.ToMarkdown(null, 100) == string.Empty && BotModNotes.ToMarkdown("  \n ", 100) == string.Empty, "No note is no text");
        check(BotModNotes.ToMarkdown("[h1]Version 2.1[/h1]\n[b]Fixes[/b] and [i]more[/i] [u]stuff[/u] [strike]old[/strike]", 500)
              == "**Version 2.1**\n**Fixes** and *more* __stuff__ ~~old~~", "Headlines, bold, italic, underline and strike become Markdown");
        check(BotModNotes.ToMarkdown("[list]\n[*] Fixed the crash\n[*] Added a hat\n[/list]", 500) == "• Fixed the crash\n• Added a hat",
            "A list becomes bullets: " + BotModNotes.ToMarkdown("[list]\n[*] Fixed the crash\n[*] Added a hat\n[/list]", 500));
        check(BotModNotes.ToMarkdown("See [url=https://apiconan.totchinuko.fr/docs?x=1]Patch Note[/url] now", 500) == "See Patch Note [apiconan.totchinuko.fr] now"
              && BotModNotes.ToMarkdown("[url]https://example.org/a[/url]", 500) == "example.org",
            "A link is shown as its text and the domain, never as a link: " + BotModNotes.ToMarkdown("See [url=https://apiconan.totchinuko.fr/docs?x=1]Patch Note[/url] now", 500));
        check(BotModNotes.ToMarkdown("[url=javascript:alert(1)]click[/url] [url=ftp://x/y]ftp[/url]", 500) == "click ftp", "Only a web link names its domain");
        check(BotModNotes.ToMarkdown("[img]https://example.org/a.png[/img]Text[hr]", 500) == "Text", "Pictures and rules are dropped");
        check(BotModNotes.ToMarkdown("[Fix] the crash, [WIP] hats", 500) == "[Fix] the crash, [WIP] hats", "A word in brackets is text, not a tag");
        check(BotModNotes.ToMarkdown("[quote=Someone]Said this[/quote] &amp; that &lt;3", 500) == "Said this & that <3", "Other tags go, the entities are read");
        check(BotModNotes.ToMarkdown("a\n\n\n\n\nb   \nc", 500) == "a\n\nb\nc", "Empty lines are kept to one");
        var long1 = BotModNotes.ToMarkdown(new string('x', 50) + " " + new string('y', 50), 80);
        check(long1.EndsWith('…') && long1.Length <= 81 && long1.StartsWith(new string('x', 50), StringComparison.Ordinal), "A long note is cut with …: " + long1.Length);
        var faces = string.Concat(Enumerable.Repeat("😀", 100));
        check(BotModNotes.ToMarkdown(faces, 51) is { Length: > 0 and <= 52 } cut && cut.EndsWith('…') && !char.IsHighSurrogate(cut[^2]),
            "...never in the middle of a character");
    }

    // The embed: as the sample, in both languages.
    private static void Message(Action<bool, string> check)
    {
        var updated = new DateTime(2026, 10, 25, 6, 47, 0, DateTimeKind.Utc);
        var unix = Unix(updated);
        var mod = new BotModVersion(3412345678, "Schatten und Licht - Servermod", (ulong)unix, "https://images.steamusercontent.com/ugc/abc/def/");
        var embed = BotModUpdateLog.Compose(mod, new WorkshopChange(updated, "Hotfixes usw"), BotModUpdateTexts.German);
        check(embed.Title == "🔄 Update für Mod: Schatten und Licht - Servermod"
              && embed.Description == $"Aktualisiert <t:{unix}:f> (<t:{unix}:R>)\n\n**Patch-Notes:**\nHotfixes usw\n\n[Alle Änderungsnotizen](https://steamcommunity.com/sharedfiles/filedetails/changelog/3412345678)",
            "The message reads as in the sample: " + embed.Description);
        check(embed.Url == "https://steamcommunity.com/sharedfiles/filedetails/?id=3412345678" && embed.ThumbnailUrl == "https://images.steamusercontent.com/ugc/abc/def/"
              && embed.TimestampUtc is null, "The title leads to the mod and the picture of the mod is the thumbnail");
        var none = BotModUpdateLog.Compose(mod, new WorkshopChange(updated, ""), BotModUpdateTexts.German);
        check(none.Description == $"Aktualisiert <t:{unix}:f> (<t:{unix}:R>)\n\n*Der Autor hat keine Patch-Notes hinterlegt.*\n\n[Alle Änderungsnotizen](https://steamcommunity.com/sharedfiles/filedetails/changelog/3412345678)",
            "An update without a note says so: " + none.Description);
        var unknown = BotModUpdateLog.Compose(mod, null, BotModUpdateTexts.German);
        check(!unknown.Description.Contains("Patch-Notes", StringComparison.Ordinal) && unknown.Description.StartsWith($"Aktualisiert <t:{unix}:f>", StringComparison.Ordinal)
              && unknown.Description.EndsWith("changelog/3412345678)", StringComparison.Ordinal), "A note that could not be read leaves the notes part out");
        var english = BotModUpdateLog.Compose(mod, new WorkshopChange(updated, "[b]Fixed[/b]"), BotModUpdateTexts.For("en"));
        check(english.Title == "🔄 Update for mod: Schatten und Licht - Servermod" && english.Description.StartsWith($"Updated <t:{unix}:f> (<t:{unix}:R>)\n\n**Patch notes:**\n**Fixed**", StringComparison.Ordinal)
              && english.Description.Contains("[All change notes](", StringComparison.Ordinal), "...and in English");
        var odd = BotModUpdateLog.Compose(mod with { Title = "Line\none\r\ttwo ", PreviewUrl = "http://insecure.example/a.png" }, null, BotModUpdateTexts.German);
        check(odd.Title == "🔄 Update für Mod: Line one  two" && odd.ThumbnailUrl is null, "A name is one line, and only an https picture is used: " + odd.Title);
        check(BotModUpdateLog.Compose(mod with { Title = "  " }, null, BotModUpdateTexts.German).Title == "🔄 Update für Mod: ?", "A mod without a name still has a message");

        var json = HttpBotWebhookClient.ToEmbed(embed);
        check(json["url"]!.GetValue<string>() == embed.Url && json["thumbnail"]!["url"]!.GetValue<string>() == embed.ThumbnailUrl, "The webhook embed carries the link and the thumbnail");
        var plain = HttpBotWebhookClient.ToEmbed(new BotWebhookEmbed("t", "d", 1, null, "javascript:x", "http://a/b"));
        check(plain["url"] is null && plain["thumbnail"] is null, "...only https ones");
    }

    private static async Task LookAsync(Action<bool, string> check, string folder)
    {
        var now = new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc);
        var updates = new FakeUpdates();
        var hook = new FakeWebhook();
        var logger = new LogLines();
        var policy = PolicyOf(c => { c.ModUpdatesEnabled = true; c.ModUpdatesName = "Mod Watcher"; c.ModUpdatesAvatarUrl = "https://example.org/watcher.png"; });
        string? url = WebhookUrl;
        var store = new BotModUpdateStore(Path.Combine(folder, "a.mod-versions"));
        using var log = new BotModUpdateLog(updates, hook, () => url, () => policy, store, () => now, logger)
        {
            Spacing = TimeSpan.Zero, WebhookCacheTime = TimeSpan.Zero, NoteTimeout = TimeSpan.FromMilliseconds(300)
        };
        log.Start();
        check(updates.Listeners == 1, "The log listens to the checks for updates");

        BotModVersion Mod(ulong id, string title, DateTime at) => new(id, title, (ulong)Unix(at), "https://images.steamusercontent.com/ugc/" + id + "/");
        var hourAgo = now.AddHours(-1);
        var servermod = Mod(1001, "Servermod", hourAgo);
        var hud = Mod(1002, "Enhanced HUD", hourAgo);

        // The first look only remembers: nothing is told for what is old.
        await log.LookAsync([servermod, hud], CancellationToken.None);
        check(hook.Embeds.Length == 0 && store.Load() is { Count: 2 }, "The first look tells nothing and remembers the versions");
        await log.LookAsync([servermod, hud], CancellationToken.None);
        check(hook.Embeds.Length == 0 && updates.Asked.Count == 0, "Nothing new, nothing told (and nobody asked for a note)");

        // A new version with its note.
        var newTime = now.AddMinutes(-2);
        updates.Notes[1001] = new WorkshopChange(newTime.AddSeconds(3), "Hotfixes usw");
        await log.LookAsync([Mod(1001, "Servermod", newTime), hud], CancellationToken.None);
        await log.FlushAsync(TimeSpan.FromSeconds(5));
        var told = hook.Embeds;
        check(told.Length == 1 && told[0].Title == "🔄 Update für Mod: Servermod" && told[0].Description.Contains("**Patch-Notes:**\nHotfixes usw", StringComparison.Ordinal)
              && told[0].ThumbnailUrl == "https://images.steamusercontent.com/ugc/1001/", "An update is told with its note: " + (told.Length > 0 ? told[0].Description : "nothing"));
        check(hook.Sent[0].Url == WebhookUrl && hook.Sent[0].Guild == BotWebhook.AnyServer && hook.Sent[0].Message.Username == "Mod Watcher"
              && hook.Sent[0].Message.AvatarUrl == "https://example.org/watcher.png" && hook.Sent[0].Message.Text is null,
            "...through the webhook (of any server) under its own name and picture");
        await log.LookAsync([Mod(1001, "Servermod", newTime), hud], CancellationToken.None);
        await log.FlushAsync(TimeSpan.FromSeconds(5));
        check(hook.Embeds.Length == 1, "...once");
        // A fresh log with the same file (the bot was restarted): it remembers.
        using (var again = new BotModUpdateLog(updates, hook, () => url, () => policy, new BotModUpdateStore(store.FilePath), () => now, logger) { Spacing = TimeSpan.Zero })
        {
            await again.LookAsync([Mod(1001, "Servermod", newTime), hud], CancellationToken.None);
            await again.FlushAsync(TimeSpan.FromSeconds(1));
            check(hook.Embeds.Length == 1, "A restart of the bot does not tell it again");
        }

        // A note of an earlier update belongs to that one: this update has none. A note that can not be read leaves the part out.
        updates.Notes[1002] = new WorkshopChange(hourAgo.AddDays(-3), "Old news");
        await log.LookAsync([Mod(1001, "Servermod", newTime), Mod(1002, "Enhanced HUD", now.AddMinutes(-1))], CancellationToken.None);
        await log.FlushAsync(TimeSpan.FromSeconds(5));
        check(hook.Embeds.Length == 2 && hook.Embeds[1].Description.Contains("keine Patch-Notes", StringComparison.Ordinal) && !hook.Embeds[1].Description.Contains("Old news", StringComparison.Ordinal),
            "The note of an earlier update is not shown for this one: " + hook.Embeds[^1].Description);
        updates.Notes.Remove(1002);
        await log.LookAsync([Mod(1001, "Servermod", newTime), Mod(1002, "Enhanced HUD", now)], CancellationToken.None);
        await log.FlushAsync(TimeSpan.FromSeconds(5));
        check(hook.Embeds.Length == 3 && !hook.Embeds[2].Description.Contains("Patch-Notes", StringComparison.Ordinal) && hook.Embeds[2].Description.Contains("Alle Änderungsnotizen", StringComparison.Ordinal),
            "A note Steam could not give leaves the notes part out, the message still goes: " + hook.Embeds[^1].Description);
        updates.Hang = true;
        var started = DateTime.UtcNow;
        await log.LookAsync([Mod(1001, "Servermod", newTime), Mod(1002, "Enhanced HUD", now.AddSeconds(30))], CancellationToken.None);
        await log.FlushAsync(TimeSpan.FromSeconds(5));
        check(hook.Embeds.Length == 4 && DateTime.UtcNow - started < TimeSpan.FromSeconds(4), "A note that never comes is given up after a moment, the message goes without it");
        updates.Hang = false;

        // Old news and the language of the bot.
        var before = hook.Embeds.Length;
        await log.LookAsync([Mod(1001, "Servermod", newTime), Mod(1002, "Enhanced HUD", now.AddSeconds(30)), Mod(1003, "Late", now.AddDays(-9))], CancellationToken.None);
        await log.LookAsync([Mod(1001, "Servermod", newTime), Mod(1002, "Enhanced HUD", now.AddSeconds(30)), Mod(1003, "Late", now.AddDays(-5))], CancellationToken.None);
        await log.FlushAsync(TimeSpan.FromSeconds(5));
        check(hook.Embeds.Length == before, "An update that is days old is no news");

        // Off: nothing is told, but the versions are remembered, so switching it on tells nothing old.
        policy = PolicyOf(c => { c.ModUpdatesEnabled = false; });
        await log.LookAsync([Mod(1001, "Servermod", now.AddSeconds(60)), Mod(1002, "Enhanced HUD", now.AddSeconds(30))], CancellationToken.None);
        policy = PolicyOf(c => { c.ModUpdatesEnabled = true; c.Language = "en"; });
        await log.LookAsync([Mod(1001, "Servermod", now.AddSeconds(60)), Mod(1002, "Enhanced HUD", now.AddSeconds(30))], CancellationToken.None);
        await log.FlushAsync(TimeSpan.FromSeconds(5));
        check(hook.Embeds.Length == before && store.Load()![1001] == (ulong)Unix(now.AddSeconds(60)), "Switched off nothing is told, but switched on again it does not tell what happened meanwhile");
        await log.LookAsync([Mod(1001, "Servermod", now.AddSeconds(90)), Mod(1002, "Enhanced HUD", now.AddSeconds(30))], CancellationToken.None);
        await log.FlushAsync(TimeSpan.FromSeconds(5));
        check(hook.Embeds.Length == before + 1 && hook.Embeds[^1].Title == "🔄 Update for mod: Servermod" && hook.Sent[^1].Message.Username is null && hook.Sent[^1].Message.AvatarUrl is null,
            "...and then the next update is told, in the language of the bot, under the name of the webhook (none set)");

        // No webhook (or none readable): nothing, and the versions are still remembered.
        before = hook.Embeds.Length;
        url = null;
        await log.LookAsync([Mod(1001, "Servermod", now.AddSeconds(120)), Mod(1002, "Enhanced HUD", now.AddSeconds(30))], CancellationToken.None);
        url = WebhookUrl;
        await log.LookAsync([Mod(1001, "Servermod", now.AddSeconds(120)), Mod(1002, "Enhanced HUD", now.AddSeconds(30))], CancellationToken.None);
        await log.FlushAsync(TimeSpan.FromSeconds(5));
        check(hook.Embeds.Length == before, "Without a webhook nothing is told, and it is not told later either");

        // Many at once: the first ones, then one message with the names of the rest.
        before = hook.Embeds.Length;
        var batch = Enumerable.Range(0, 14).Select(i => Mod(2000 + (ulong)i, "Mod " + i, now.AddDays(-1))).ToList();
        await log.LookAsync(batch, CancellationToken.None);
        await log.LookAsync(batch.Select(x => x with { TimeUpdated = (ulong)Unix(now.AddMinutes(-5)) + x.Id - 2000 }).ToList(), CancellationToken.None);
        await log.FlushAsync(TimeSpan.FromSeconds(10));
        var many = hook.Embeds.Skip(before).ToArray();
        check(many.Length == BotModUpdateLog.MaxPerLook + 1 && many[^1].Title == "🔄 More mod updates" && many[^1].Description == "Mod 10, Mod 11, Mod 12, Mod 13",
            "Many updates at once: the first ten, then the names of the others: " + string.Join(" | ", many.Select(x => x.Title)) + " / " + many[^1].Description);

        // A webhook that refuses is one warning, no exception.
        hook.Failure = new InvalidOperationException("Unknown Webhook " + WebhookUrl);
        before = hook.Embeds.Length;
        // (the mods that were not in the last look were forgotten: this look remembers these two again)
        await log.LookAsync([Mod(1001, "Servermod", now.AddSeconds(400)), Mod(1002, "Enhanced HUD", now.AddSeconds(450))], CancellationToken.None);
        await log.LookAsync([Mod(1001, "Servermod", now.AddSeconds(500)), Mod(1002, "Enhanced HUD", now.AddSeconds(600))], CancellationToken.None);
        await log.FlushAsync(TimeSpan.FromSeconds(5));
        check(logger.Lines.Count(x => x.StartsWith("Warning: Discord bot: the mod update webhook refused", StringComparison.Ordinal)) == 1
              && !string.Join('\n', logger.Lines).Contains("abcdefghijklmnopqrstuvwxyz0123456789ABCE", StringComparison.Ordinal),
            "A refusing webhook is one warning without the address: " + string.Join('|', logger.Lines));
        hook.Failure = null;

        // Through the event of the launcher, on another thread.
        before = hook.Embeds.Length;
        updates.Raise([Mod(1001, "Servermod", now.AddSeconds(700)), Mod(1002, "Enhanced HUD", now.AddSeconds(600))]);
        await Task.Delay(50);
        await log.FlushAsync(TimeSpan.FromSeconds(5));
        check(hook.Embeds.Length == before + 1, "The check of the launcher reaches the log");
        // An unreadable file is a first look, not a crash.
        File.WriteAllText(store.FilePath!, "{ not json");
        before = hook.Embeds.Length;
        await log.LookAsync([Mod(1001, "Servermod", now.AddSeconds(800))], CancellationToken.None);
        check(hook.Embeds.Length == before && store.Load() is { Count: 1 }, "A broken file of versions is a first look");
        // Nothing to look at, or a cancelled look: nothing happens.
        await log.LookAsync([], CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await log.LookAsync([Mod(1001, "Servermod", now.AddSeconds(900))], cancelled.Token);
        check(hook.Embeds.Length == before, "No mods or a cancelled look tell nothing");

        log.Dispose();
        check(updates.Listeners == 0, "A log that is ended stops listening");
        var other = new BotModUpdateStore(Path.Combine(folder, "none", "x", "y"));
        check(other.Load() is null && other.Save(new Dictionary<ulong, ulong> { [1] = 2 }) && other.Load() is { Count: 1 } && new BotModUpdateStore(null).Load() is null && new BotModUpdateStore(null).Save(new Dictionary<ulong, ulong>()),
            "The versions are kept in a folder that is made when needed, and not at all without a place");
    }
}
