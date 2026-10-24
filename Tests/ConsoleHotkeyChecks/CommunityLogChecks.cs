using System.Text;
using TrebuchetLib.DiscordBot;

/// <summary>
/// The webhook (format, protected storage, sending), the audit log under its own name and picture, and the community log:
/// what the players read when a restart or stop is planned, begins, and ends.
/// </summary>
static class CommunityLogChecks
{
    private const ulong Operator = 200000000000000002;
    private const ulong Guild = 100000000000000001;
    private const ulong Channel = 400000000000000004;
    private const ulong AuditChannel = 500000000000000005;
    private const string WebhookUrl = "https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789ABCD";

    private sealed class ReversingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Reverse().Select(x => (byte)(x ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => data.Select(x => (byte)(x ^ 0x5A)).Reverse().ToArray();
    }

    private sealed class FakeWebhook : IBotWebhookClient
    {
        public List<(string Url, BotWebhookMessage Message)> Sent { get; } = [];
        public Exception? Failure { get; set; }

        public Task SendAsync(string webhookUrl, BotWebhookMessage message, CancellationToken token)
        {
            if (Failure is not null) return Task.FromException(Failure);
            lock (Sent) Sent.Add((webhookUrl, message));
            return Task.CompletedTask;
        }

        public BotWebhookMessage[] Messages
        {
            get { lock (Sent) return Sent.Select(x => x.Message).ToArray(); }
        }
    }

    private sealed class StatusActions : IBotActions
    {
        public int InstanceCount => Statuses.Count;
        public List<BotServerStatus> Statuses { get; set; } = [];
        public event EventHandler? StatusChanged { add { } remove { } }
        public Task<IReadOnlyList<BotServerStatus>> GetStatusesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<BotServerStatus>>(Statuses.ToList());
        public Task<BotActionResult> PostponeAsync(int instance, int minutes, string reason, CancellationToken token) => Done();
        public Task<BotActionResult> ResumeAsync(int instance, CancellationToken token) => Done();
        public Task<BotActionResult> RestartAsync(int instance, TimeSpan delay, string reason, CancellationToken token) => Done();
        public Task<BotActionResult> StopAsync(int instance, TimeSpan delay, string reason, CancellationToken token) => Done();
        public Task<BotActionResult> StartAsync(int instance, CancellationToken token) => Done();
        public Task<BotActionResult> UpdateAsync(CancellationToken token) => Done();
        public Task<BotActionResult> CheckUpdatesAsync(CancellationToken token) => Done();
        private static Task<BotActionResult> Done() => Task.FromResult(new BotActionResult(BotActionOutcome.Done));
    }

    private sealed class LogLines : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static DiscordBotConfig Config(Action<DiscordBotConfig>? change = null)
    {
        var config = new DiscordBotConfig { GuildId = Guild, SuperAdminUserId = Operator };
        config.AddRole(300000000000000003);
        config.SetAdminChannel(Channel);
        change?.Invoke(config);
        return config;
    }

    private static BotPolicy PolicyOf(DiscordBotConfig config) =>
        BotPolicy.Create(config).Policy ?? throw new Exception("settings of the check are invalid: " + string.Join(", ", BotPolicy.Create(config).Issues.Select(x => x.Kind)));

    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var folder = Path.Combine(Path.GetTempPath(), "tb-community-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // --- the format of a webhook URL
            Check(BotWebhook.TryNormalize(WebhookUrl, out var normal) && normal == WebhookUrl, "A webhook URL is taken as it is");
            Check(BotWebhook.TryNormalize("  \"" + WebhookUrl + "?wait=true\" ", out var pasted) && pasted == WebhookUrl, "Spaces, quotes and a query are dropped");
            Check(BotWebhook.TryNormalize(WebhookUrl.Replace("discord.com", "canary.discord.com"), out var canary) && canary == WebhookUrl
                  && BotWebhook.TryNormalize(WebhookUrl.Replace("discord.com", "discordapp.com"), out _), "The other hosts of Discord are the same webhook");
            foreach (var wrong in new[]
                     {
                         "", "   ", "https://discord.com/api/webhooks/123/abc", "http://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789ABCD",
                         "https://discord.com.evil.example/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789ABCD",
                         "https://evil.example/https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789ABCD",
                         "https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789ABCD/extra",
                         "https://discord.com/api/webhooks/abc/abcdefghijklmnopqrstuvwxyz0123456789ABCD", string.Join(".", "MTIzNDU2Nzg5MDEyMzQ1Njc4", "GabcDE", "abcdefghijklmnopqrstuvwxyz0123456789ABCD")
                     })
                Check(!BotWebhook.TryNormalize(wrong, out _), "Not a webhook URL: " + wrong);

            // --- protected storage: like the token
            var path = Path.Combine(folder, "w.audit-webhook");
            var store = new BotWebhookStore(path, new ReversingProtector());
            Check(!store.HasWebhook && store.Load() is null && store.CanSave, "No webhook at first");
            store.Save(WebhookUrl + "?wait=true");
            Check(store.HasWebhook && store.Load() == WebhookUrl, "The webhook is stored and read back clean");
            Check(File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes("abcdefghijklmnop")) < 0, "The file holds no plain URL");
            var leaked = BotLogRedactor.Redact($"Request to {WebhookUrl} failed, token abcdefghijklmnopqrstuvwxyz0123456789ABCD");
            Check(!leaked.Contains("abcdefghijklmnop") && !leaked.Contains("123456789012345678/"), "A webhook that was saved or read is removed from log texts: " + leaked);
            Check(Throws<ArgumentException>(() => store.Save("not a webhook")) && store.Load() == WebhookUrl, "An invalid URL changes nothing");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            Check(store.Load() is null && store.HasWebhook, "A file that can not be decrypted is no webhook");
            store.Delete();
            Check(!store.HasWebhook, "The webhook can be removed");
            Check(!new BotWebhookStore(Path.Combine(folder, "none"), null).CanSave == !OperatingSystem.IsWindows() || OperatingSystem.IsWindows(),
                "Without protection (not Windows) nothing is stored");
            var both = BotWebhooks.ForTokenPath(Path.Combine(folder, "discord-bot.enhanced.token"));
            Check(both.Audit != both.Community, "The two webhooks are two files next to the token");

            // --- the embed Discord gets
            var embed = DiscordNetWebhookClient.ToEmbed(new BotWebhookEmbed(new string('T', 400), new string('D', 5000), BotCommunityLog.Orange, new DateTime(2026, 10, 24, 5, 30, 0, DateTimeKind.Utc)));
            Check(embed.Title.Length <= 256 && embed.Description.Length <= 4000 && embed.Color?.RawValue == BotCommunityLog.Orange
                  && embed.Timestamp is { } stamp && stamp.UtcDateTime == new DateTime(2026, 10, 24, 5, 30, 0, DateTimeKind.Utc), "The embed has its color and time and stays inside the limits");

            // --- settings: name, picture, lead time
            var defaults = PolicyOf(Config());
            Check(!defaults.CommunityEnabled && defaults.CommunityLead == TimeSpan.FromMinutes(30) && defaults.AuditName.Length == 0 && defaults.AuditAvatarUrl.Length == 0,
                "The community log is off by default, 30 minutes ahead");
            var identity = PolicyOf(Config(c =>
            {
                c.AuditName = "  Trebuchet\nWatcher  ";
                c.AuditAvatarUrl = " https://example.org/a.png ";
                c.CommunityName = new string('N', 200);
                c.CommunityAvatarUrl = "https://example.org/b.png";
                c.CommunityEnabled = true;
                c.CommunityLeadMinutes = 60;
            }));
            Check(identity.AuditName == "Trebuchet Watcher" && identity.AuditAvatarUrl == "https://example.org/a.png" && identity.CommunityName.Length == 80
                  && identity.CommunityEnabled && identity.CommunityLead == TimeSpan.FromMinutes(60), "Names are one clean line of at most 80 characters, links are trimmed");
            foreach (var wrong in new[] { "http://example.org/a.png", "ftp://example.org/a.png", "javascript:alert(1)", "https://example.org/a b.png", "example.org/a.png", "https://", "https://example.org/" + new string('x', 600) })
            {
                var result = BotPolicy.Create(Config(c => c.AuditAvatarUrl = wrong));
                Check(!result.IsValid && result.Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidUrl && x.Detail == nameof(DiscordBotConfig.AuditAvatarUrl)),
                    "A picture link must be one https link: " + wrong);
            }
            Check(!BotPolicy.Create(Config(c => c.CommunityAvatarUrl = "http://x.example/a.png")).IsValid, "The same for the community log");
            Check(!BotPolicy.Create(Config(c => c.CommunityLeadMinutes = 0)).IsValid && !BotPolicy.Create(Config(c => c.CommunityLeadMinutes = 61)).IsValid
                  && BotPolicy.Create(Config(c => c.CommunityLeadMinutes = 1)).IsValid && BotPolicy.Create(Config(c => c.CommunityLeadMinutes = 60)).IsValid,
                "The lead time is 1 to 60 minutes");

            // --- the audit log under its own name and picture
            var audit = new BotAuditLog(Path.Combine(folder, "audit"));
            var channel = new RecordingChannel();
            var hooks = new FakeWebhook();
            var policy = PolicyOf(Config(c =>
            {
                c.AuditChannelId = AuditChannel;
                c.AuditName = "Wächter";
                c.AuditAvatarUrl = "https://example.org/a.png";
            }));
            string? webhookUrl = null;
            using (var feed = new BotAuditChannelFeed(audit, channel, () => policy, _ => "Haupt", null, hooks, () => webhookUrl) { Spacing = TimeSpan.Zero, WebhookCacheTime = TimeSpan.Zero })
            {
                feed.Start();
                void Write(string detail) => audit.Write(new BotAuditEntry(DateTime.UtcNow, BotAuditResult.Executed, Operator, "x", Guild, Channel, BotCommand.Stop, "instance=0", detail));
                Write("Done");
                await feed.FlushAsync(TimeSpan.FromSeconds(5));
                Check(channel.Notes.Count == 1 && hooks.Messages.Length == 0, "Without a webhook the bot writes the line into the audit channel");
                webhookUrl = WebhookUrl;
                Write("Done");
                await feed.FlushAsync(TimeSpan.FromSeconds(5));
                Check(channel.Notes.Count == 1 && hooks.Messages.Length == 1 && hooks.Messages[0].Username == "Wächter" && hooks.Messages[0].AvatarUrl == "https://example.org/a.png"
                      && hooks.Messages[0].Text!.StartsWith("✅ Erledigt: Server stoppen: Server Haupt (Instanz 0)", StringComparison.Ordinal) && hooks.Messages[0].Embed is null
                      && hooks.Sent[0].Url == WebhookUrl, "With a webhook the line goes through it, under the name and with the picture of the settings");
                webhookUrl = null;
                Write("Done");
                await feed.FlushAsync(TimeSpan.FromSeconds(5));
                Check(channel.Notes.Count == 2 && hooks.Messages.Length == 1, "Removing the webhook brings back the audit channel");
                policy = PolicyOf(Config(c => { c.AuditName = string.Empty; c.AuditAvatarUrl = string.Empty; }));
                webhookUrl = WebhookUrl;
                Write("Done");
                await feed.FlushAsync(TimeSpan.FromSeconds(5));
                Check(hooks.Messages.Length == 2 && hooks.Messages[1].Username is null && hooks.Messages[1].AvatarUrl is null,
                    "Without a name and picture the ones of the webhook apply, and the webhook alone is enough (no channel ID)");
                hooks.Failure = new InvalidOperationException("Unknown Webhook " + WebhookUrl);
                Write("lost");
                await feed.FlushAsync(TimeSpan.FromSeconds(5));
                hooks.Failure = null;
                Write("Done");
                await feed.FlushAsync(TimeSpan.FromSeconds(5));
                Check(hooks.Messages.Length == 3, "A webhook that refuses a line costs that line only");
            }

            // --- the community log
            await CommunityAsync(Check, folder);
            // --- the automatic restarts of the launcher (schedule, countdown, update round), from its own status
            await AutomaticAsync(Check);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        return count;
    }

    private sealed class RecordingChannel : IBotChannelClient
    {
        public List<(ulong Channel, string Text)> Notes { get; } = [];
        public Task<ulong?> PostStatusAsync(ulong channelId, BotStatusMessage message) => Task.FromResult<ulong?>(1);
        public Task<BotEditResult> EditStatusAsync(ulong channelId, ulong messageId, BotStatusMessage message) => Task.FromResult(BotEditResult.Edited);
        public Task PostNoteAsync(ulong channelId, string text)
        {
            lock (Notes) Notes.Add((channelId, text));
            return Task.CompletedTask;
        }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    private static BotServerStatus Server(int instance, string name, BotServerState state, BotPendingAction pending = BotPendingAction.None, DateTime? at = null, bool restarting = false) =>
        new(instance, name, state, 0, 40, null, pending, at, null, restarting);

    // The status of the launcher as it is while an automatic restart is planned, under way and done: through the same conversion
    // the bot uses, so what the community log sees is what the launcher really reports.
    private static async Task AutomaticAsync(Action<bool, string> check)
    {
        var now = new DateTime(2026, 10, 24, 5, 0, 0, DateTimeKind.Utc);
        var actions = new StatusActions();
        var hooks = new FakeWebhook();
        var policy = PolicyOf(Config(c => c.CommunityEnabled = true));
        using var log = new BotCommunityLog(actions, hooks, () => WebhookUrl, () => policy, () => now) { Spacing = TimeSpan.Zero, WebhookCacheTime = TimeSpan.Zero };
        log.Start();
        var started = now.AddHours(-5);
        TrebuchetLib.Services.LauncherServerStatus Launcher(TrebuchetLib.ProcessState? state, Func<TrebuchetLib.Services.LauncherServerStatus, TrebuchetLib.Services.LauncherServerStatus>? change = null)
        {
            var status = new TrebuchetLib.Services.LauncherServerStatus { Instance = 0, SaveName = "Exiles", ServerName = "Schatten und Licht", Installed = true, State = state, StartedUtc = started };
            return change is null ? status : change(status);
        }
        async Task Look(TrebuchetLib.Services.LauncherServerStatus status, double advanceSeconds)
        {
            now = now.AddSeconds(advanceSeconds);
            actions.Statuses = [LauncherBotActions.ToBotStatus(status, now)];
            await log.RefreshAsync(CancellationToken.None);
            await log.FlushAsync(TimeSpan.FromSeconds(5));
        }
        string[] Titles() => hooks.Messages.Select(x => x.Embed!.Title).ToArray();

        var restartAt = now.AddMinutes(45);
        await Look(Launcher(TrebuchetLib.ProcessState.ONLINE), 0);
        // The daily restart of the save: the launcher knows the time an hour ahead.
        await Look(Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { NextScheduledRestartUtc = restartAt }), 60);
        check(hooks.Messages.Length == 0, "A scheduled restart more than the lead time away is not announced yet");
        await Look(Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { NextScheduledRestartUtc = restartAt }), 14 * 60);
        check(Titles().SequenceEqual(["⏰ Der Server startet in 30 Minuten neu"]), "The scheduled restart of the save is announced 30 minutes ahead: " + string.Join(" | ", Titles()));
        // The launcher then starts its own countdown for it (warnings to the players): the same restart, not told again.
        await Look(Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
        {
            CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = restartAt.AddSeconds(3), RestartRequested = true, SequenceRunning = true
        }), 25 * 60);
        check(hooks.Messages.Length == 1, "The countdown of that very restart is not announced a second time");
        await Look(Launcher(TrebuchetLib.ProcessState.STOPPING, x => x with { ProcessRestarts = true, SequenceRunning = true }), 5 * 60);
        await Look(Launcher(null, x => x with { ShuttingDown = true }), 20);
        await Look(Launcher(null, x => x with { Starting = true }), 30);
        await Look(Launcher(TrebuchetLib.ProcessState.RUNNING), 20);
        await Look(Launcher(TrebuchetLib.ProcessState.ONLINE), 20);
        check(Titles().SequenceEqual(["⏰ Der Server startet in 30 Minuten neu", "🔄 Der Server startet jetzt neu", "🟢 Der Server ist wieder online"])
              && hooks.Messages[2].Embed!.Description.StartsWith("Der Neustart ist durch", StringComparison.Ordinal)
              && hooks.Messages[2].Embed!.Description.EndsWith("**Offline:** 1 Min. 30 Sek.", StringComparison.Ordinal),
            "An automatic restart goes through announced, restarting, back online with the offline time: " + string.Join(" | ", Titles()) + " / " + hooks.Messages[^1].Embed!.Description);

        // A restart for an update (the launcher has no time ahead, its countdown is the first it says).
        var before = hooks.Messages.Length;
        await Look(Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { UpdatePending = true }), 600);
        check(hooks.Messages.Length == before, "An update that waits for the next check has no time yet: nothing is said");
        var updateAt = now.AddMinutes(10);
        await Look(Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
        {
            InUpdateRound = true, CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = updateAt, RestartRequested = true
        }), 30);
        check(Titles()[^1] == "⏰ Der Server startet in 10 Minuten für ein Update neu", "The countdown of an update restart is announced as one: " + Titles()[^1]);
        await Look(Launcher(TrebuchetLib.ProcessState.STOPPING, x => x with { ProcessRestarts = true, InUpdateRound = true }), 600);
        check(Titles()[^1] == "🔄 Der Server startet jetzt neu", "...and its start is told");
        await Look(Launcher(TrebuchetLib.ProcessState.ONLINE), 120);
        check(hooks.Messages[^1].Embed!.Title == "🟢 Der Server ist wieder online" && hooks.Messages[^1].Embed!.Description.EndsWith("**Offline:** 2 Min. 0 Sek.", StringComparison.Ordinal),
            "...and that it is back");

        // A restart that was postponed by the bot or the window runs when the pause is over: told when that comes close.
        var pauseEnd = now.AddMinutes(90);
        await Look(Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { RestartPostponed = true, PausedUntilUtc = pauseEnd }), 10);
        var postponedBefore = hooks.Messages.Length;
        await Look(Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { RestartPostponed = true, PausedUntilUtc = pauseEnd }), 60 * 60 + 5 * 60);
        check(postponedBefore == hooks.Messages.Length - 1 && Titles()[^1] == "⏰ Der Server startet in 25 Minuten neu",
            "A postponed restart runs when the pause is over: it is announced when that comes within the lead time: " + Titles()[^1]);
    }

    private static async Task CommunityAsync(Action<bool, string> check, string folder)
    {
        var now = new DateTime(2026, 10, 24, 5, 0, 0, DateTimeKind.Utc);
        var actions = new StatusActions { Statuses = [Server(0, "Exiled Lands", BotServerState.Online)] };
        var hooks = new FakeWebhook();
        var logger = new LogLines();
        string? url = WebhookUrl;
        var policy = PolicyOf(Config(c =>
        {
            c.CommunityEnabled = true;
            c.CommunityName = "Schatten und Licht Server";
            c.CommunityAvatarUrl = "https://example.org/logo.png";
        }));
        using var log = new BotCommunityLog(actions, hooks, () => url, () => policy, () => now, logger) { Spacing = TimeSpan.Zero, WebhookCacheTime = TimeSpan.Zero };
        log.Start();
        async Task<TimeSpan> Look(double advanceSeconds = 0)
        {
            now = now.AddSeconds(advanceSeconds);
            var due = await log.RefreshAsync(CancellationToken.None);
            await log.FlushAsync(TimeSpan.FromSeconds(5));
            return due;
        }
        BotWebhookEmbed[] Posts() => hooks.Messages.Select(x => x.Embed!).ToArray();
        string Unix(DateTime time) => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString();

        // The first look says nothing; a server that is merely online says nothing either.
        check(await Look() == BotCommunityLog.CalmInterval && hooks.Messages.Length == 0, "The first look says nothing");

        // 33 minutes ahead: not yet, but the log asks to be called exactly when the lead time begins (a calm server is read
        // only every five minutes, that would be too late).
        var restartAt = now.AddMinutes(33);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online, BotPendingAction.Restart, restartAt)];
        var wait = await Look();
        check(hooks.Messages.Length == 0 && wait == TimeSpan.FromMinutes(3), "Outside the lead time nothing is said and the next look is due when it begins: " + wait);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online, BotPendingAction.Restart, now.AddMinutes(90))];
        check(await Look() == BotCommunityLog.CalmInterval && hooks.Messages.Length == 0, "Far ahead it is the usual look every five minutes");
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online, BotPendingAction.Restart, restartAt)];

        // 30 minutes ahead: told once, as in the sample.
        var due = await Look(180);
        var told = Posts();
        check(told.Length == 1 && told[0].Title == "⏰ Der Server startet in 30 Minuten neu" && told[0].Color == BotCommunityLog.Orange && told[0].TimestampUtc == now
              && told[0].Description == $"Bitte sucht euch rechtzeitig einen sicheren Ort und loggt euch aus.\nNeustart um <t:{Unix(restartAt)}:t> Uhr (<t:{Unix(restartAt)}:R>).",
            "A planned restart is announced 30 minutes ahead with the time as Discord shows it: " + (told.Length > 0 ? told[0].Title + " | " + told[0].Description : "nothing"));
        check(hooks.Messages[0].Username == "Schatten und Licht Server" && hooks.Messages[0].AvatarUrl == "https://example.org/logo.png" && hooks.Messages[0].Text is null
              && hooks.Sent[0].Url == WebhookUrl, "...under the name and with the picture of the community");
        check(due <= TimeSpan.FromMinutes(5), "While it is announced it is looked at at least every five minutes");
        await Look(120);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online, BotPendingAction.Restart, restartAt.AddSeconds(2))];
        await Look(60);
        check(hooks.Messages.Length == 1, "The same event is not announced twice (the countdown ends a few seconds off the schedule)");

        // Postponed by an hour: told again when the new time comes within the lead time.
        var postponed = restartAt.AddHours(1);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online, BotPendingAction.Restart, postponed)];
        await Look(60);
        check(hooks.Messages.Length == 1, "A postponement outside the lead time says nothing");
        now = postponed.AddMinutes(-25).AddSeconds(-1);
        await Look(1);
        check(hooks.Messages.Length == 2 && Posts()[1].Title == "⏰ Der Server startet in 25 Minuten neu", "...and is announced again when it comes close: " + Posts()[^1].Title);

        // Going down: a restart is told at once, and "online again" says how long it was away.
        now = postponed.AddSeconds(-30);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Stopping, restarting: true)];
        var busy = await Look();
        var down = Posts();
        check(down.Length == 3 && down[2].Title == "🔄 Der Server startet jetzt neu" && down[2].Description == "Bis gleich – sobald er wieder da ist, steht es hier."
              && down[2].Color == BotCommunityLog.Red && busy == BotCommunityLog.BusyInterval, "A restart going down is told, a restarting server is looked at every 15 seconds");
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Stopped)];
        await Look(20);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Stopping, restarting: true)];
        await Look(5);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Starting)];
        await Look(25);
        check(hooks.Messages.Length == 3, "A flicker while it is down is not told again");
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online)];
        await Look(30);
        var up = Posts();
        check(up.Length == 4 && up[3].Title == "🟢 Der Server ist wieder online" && up[3].Color == BotCommunityLog.Green
              && up[3].Description == "Der Neustart ist durch – ihr könnt euch wieder einloggen.\n**Offline:** 1 Min. 20 Sek.",
            "Back online with the time it was offline: " + up[^1].Description);
        await Look(60);
        check(hooks.Messages.Length == 4, "Online again is told once");

        // A stop, with a countdown first.
        var stopAt = now.AddMinutes(10);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online, BotPendingAction.Stop, stopAt)];
        await Look();
        var stopTold = Posts();
        check(stopTold.Length == 5 && stopTold[4].Title == "⏰ Der Server wird in 10 Minuten gestoppt"
              && stopTold[4].Description.EndsWith($"Stopp um <t:{Unix(stopAt)}:t> Uhr (<t:{Unix(stopAt)}:R>).", StringComparison.Ordinal),
            "A planned stop is announced with its time: " + stopTold[^1].Title);
        now = stopAt;
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Stopping)];
        await Look();
        check(Posts()[5].Title == "⏹️ Der Server wird jetzt gestoppt" && Posts()[5].Color == BotCommunityLog.Red, "A stop going down is told as a stop");
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Stopped)];
        await Look(45);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online)];
        await Look(0);
        check(Posts()[6].Description == "Ihr könnt euch wieder einloggen.\n**Offline:** 45 Sek.", "After a stop: back online without the word restart: " + Posts()[^1].Description);

        // An update restart, and a restart that was only planned (the process was already gone when it was seen).
        var updateAt = now.AddMinutes(5);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online, BotPendingAction.Update, updateAt)];
        await Look(10);
        check(Posts()[7].Title == "⏰ Der Server startet in 5 Minuten für ein Update neu", "An update restart says so: " + Posts()[7].Title);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Stopped)];
        await Look(300);
        check(Posts()[8].Title == "🔄 Der Server startet jetzt neu", "A restart that was announced is a restart even when the server was seen already stopped");
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online)];
        await Look(60);
        check(Posts().Length == 10, "...and its end is told too");

        // A crash says nothing: the launcher did not plan it.
        var before = hooks.Messages.Length;
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Crashed)];
        await Look(10);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Starting)];
        await Look(30);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online)];
        await Look(60);
        check(hooks.Messages.Length == before, "A crash and the restart after it say nothing");

        // Several servers: the name tells which one. One server: the name of the log does.
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online), Server(1, "Siptah", BotServerState.Online)];
        await Look(10);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online), Server(1, "Siptah", BotServerState.Stopping, restarting: true)];
        await Look(10);
        check(Posts()[^1].Description == "**Siptah**\nBis gleich – sobald er wieder da ist, steht es hier.", "With several servers the name comes first: " + Posts()[^1].Description);

        // Off, or without a webhook: nothing, and nothing old when it is switched on.
        var offBefore = hooks.Messages.Length;
        policy = PolicyOf(Config(c => c.CommunityEnabled = false));
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online), Server(1, "Siptah", BotServerState.Online)];
        await Look(10);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Stopping, restarting: true), Server(1, "Siptah", BotServerState.Online)];
        await Look(10);
        policy = PolicyOf(Config(c => c.CommunityEnabled = true));
        url = null;
        await Look(10);
        check(hooks.Messages.Length == offBefore, "Switched off or without a webhook nothing is sent");
        url = WebhookUrl;
        await Look(10);
        check(hooks.Messages.Length == offBefore, "Switched on again it starts with a fresh look and does not tell what happened meanwhile");

        // A webhook that refuses: one warning, no exception, the next message goes out when it works again.
        hooks.Failure = new InvalidOperationException("Unknown Webhook " + WebhookUrl);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online), Server(1, "Siptah", BotServerState.Online)];
        await Look(10);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Stopping, restarting: true), Server(1, "Siptah", BotServerState.Stopping, restarting: true)];
        await Look(10);
        check(logger.Lines.Count(x => x.StartsWith("Warning: Discord bot: the community log webhook refused", StringComparison.Ordinal)) == 1
              && !string.Join('\n', logger.Lines).Contains("abcdefghijklmnopqrstuvwxyz0123456789ABCD"), "A refusing webhook is one warning that holds no secret");
        hooks.Failure = null;

        // The texts and the time of the offline phase.
        var de = BotCommunityTexts.German;
        var en = BotCommunityTexts.English;
        check(de.Duration(TimeSpan.FromSeconds(45)) == "45 Sek." && de.Duration(TimeSpan.FromSeconds(80)) == "1 Min. 20 Sek." && de.Duration(TimeSpan.FromSeconds(3900)) == "1 Std. 5 Min."
              && de.Duration(TimeSpan.FromHours(1)) == "1 Std." && de.Duration(TimeSpan.FromSeconds(-5)) == "0 Sek." && de.MinutesText(1) == "1 Minute" && de.MinutesText(30) == "30 Minuten",
            "Durations are told as in the sample");
        check(en.Duration(TimeSpan.FromSeconds(80)) == "1 min 20 s" && en.Duration(TimeSpan.FromSeconds(3900)) == "1 h 5 min" && en.MinutesText(30) == "30 minutes",
            "...and in English");
        policy = PolicyOf(Config(c => { c.CommunityEnabled = true; c.Language = "en"; }));
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online, BotPendingAction.Restart, now.AddMinutes(30)), Server(1, "Siptah", BotServerState.Online)];
        var englishBefore = hooks.Messages.Length;
        await Look(0);
        await Look(5);
        var english = hooks.Messages.Skip(englishBefore).Select(x => x.Embed!).FirstOrDefault(x => x.Title.StartsWith("⏰", StringComparison.Ordinal));
        check(english is not null && english.Title == "⏰ The server restarts in 30 minutes" && english.Description.StartsWith("**Exiled Lands**\nPlease find a safe place and log out in time.\nRestart at <t:", StringComparison.Ordinal),
            "The community log speaks the language of the bot: " + english?.Title);
    }
}
