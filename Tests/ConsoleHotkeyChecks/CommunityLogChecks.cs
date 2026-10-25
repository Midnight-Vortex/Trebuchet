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
        public List<(string Url, BotWebhookMessage Message, ulong Guild)> Sent { get; } = [];
        public Exception? Failure { get; set; }
        public bool Hang { get; set; }
        public int Cancelled;

        public async Task SendAsync(string webhookUrl, BotWebhookMessage message, ulong expectedGuildId, CancellationToken token)
        {
            if (Hang)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref Cancelled);
                    throw;
                }
            }
            if (Failure is not null) throw Failure;
            lock (Sent) Sent.Add((webhookUrl, message, expectedGuildId));
        }

        public BotWebhookMessage[] Messages
        {
            get { lock (Sent) return Sent.Select(x => x.Message).ToArray(); }
        }
    }

    // Discord as the webhook client sees it: answers the question for the server (GET) and takes the messages (POST).
    private sealed class FakeDiscordHttp : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];
        public string GuildAnswer { get; set; } = $"{{\"id\":\"123456789012345678\",\"type\":1,\"guild_id\":\"{Guild}\",\"channel_id\":\"{Channel}\"}}";
        public Func<int, HttpResponseMessage>? PostAnswer { get; set; }
        public TimeSpan Delay { get; set; }
        public int Cancelled;
        public Exception? NetworkFailure { get; set; }
        public int Posts => Requests.Count(x => x.Method == HttpMethod.Post);
        public int Gets => Requests.Count(x => x.Method == HttpMethod.Get);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (NetworkFailure is not null)
            {
                lock (Requests) Requests.Add((request.Method, request.RequestUri!.ToString(), body));
                throw NetworkFailure;
            }
            if (request.Method == HttpMethod.Get)
            {
                lock (Requests) Requests.Add((request.Method, request.RequestUri!.ToString(), body));
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(GuildAnswer) };
            }
            if (Delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(Delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref Cancelled);
                    throw;
                }
            }
            lock (Requests) Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            return PostAnswer?.Invoke(Posts) ?? new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
    }

    private static HttpResponseMessage Answer(System.Net.HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json) };

    private class StatusActions : IBotActions
    {
        public int InstanceCount => Statuses.Count;
        public List<BotServerStatus> Statuses { get; set; } = [];
        public event EventHandler? StatusChanged { add { } remove { } }
        public virtual Task<IReadOnlyList<BotServerStatus>> GetStatusesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<BotServerStatus>>(Statuses.ToList());
        public Task<BotActionResult> PostponeAsync(int instance, int minutes, string reason, CancellationToken token) => Done();
        public Task<BotActionResult> ResumeAsync(int instance, CancellationToken token) => Done();
        public Task<BotActionResult> CancelAsync(int instance, CancellationToken token) => Done();
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

            // a thread (or a post of a forum channel) has its own webhook address with the thread in it
            const string ThreadId = "555555555555555555";
            Check(BotWebhook.TryNormalize(WebhookUrl + "?wait=true&thread_id=" + ThreadId + "#x", out var inThread, out var thread) && inThread == WebhookUrl + "?thread_id=" + ThreadId
                  && thread == ulong.Parse(ThreadId) && BotWebhook.WithoutThread(inThread) == WebhookUrl, "The thread of a webhook address is kept, the rest of the query is dropped");
            foreach (var noThread in new[] { "?thread_id=abc", "?thread_id=0", "?thread_id=", "?xthread_id=" + ThreadId, "?thread_id=" + new string('9', 30), "?wait=true" })
                Check(BotWebhook.TryNormalize(WebhookUrl + noThread, out var plain, out var none) && plain == WebhookUrl && none is null, "No usable thread: " + noThread);
            Check(BotWebhook.Contains("my webhook " + WebhookUrl + " here") && BotWebhook.Contains("https://discordapp.com/api/v9/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789ABCD")
                  && !BotWebhook.Contains("https://discord.com/channels/123/456") && !BotWebhook.Contains(null), "A webhook address is found anywhere in a text");

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
            store.Save(WebhookUrl + "?thread_id=" + ThreadId);
            Check(store.Load() == WebhookUrl + "?thread_id=" + ThreadId, "The thread stays with the stored webhook");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            Check(store.Load() is null && store.HasWebhook, "A file that can not be decrypted is no webhook");
            // ...and it is told once (not once per look), again after it worked in between
            var unreadable = new LogLines();
            var source = store.AsSource("community", unreadable);
            Check(source() is null && unreadable.Lines.Count == 0, "...not at the first look (the file may just be replaced)");
            Check(source() is null && source() is null && source() is null && unreadable.Lines.Count == 1
                  && unreadable.Lines[0].StartsWith("Warning: Discord bot: the community webhook is saved but can not be read here", StringComparison.Ordinal)
                  && !unreadable.Lines[0].Contains("abcdefghijklmnop"), "A saved webhook that can not be read is one warning, not silence: " + string.Join("|", unreadable.Lines));
            store.Save(WebhookUrl);
            Check(source() == WebhookUrl && unreadable.Lines.Count == 1, "...and nothing when it works");
            File.WriteAllBytes(path, [9, 9]);
            Check(source() is null && source() is null && unreadable.Lines.Count == 2, "...and again when it breaks again");
            store.Delete();
            Check(!store.HasWebhook && source() is null && unreadable.Lines.Count == 2, "The webhook can be removed, and none set is no warning");
            var unprotected = new BotWebhookStore(Path.Combine(folder, "none"), null);
            Check(unprotected.CanSave == OperatingSystem.IsWindows(), "Only where there is protection (Windows) a webhook can be stored");
            if (!OperatingSystem.IsWindows())
                Check(Throws<PlatformNotSupportedException>(() => unprotected.Save(WebhookUrl)) && !unprotected.HasWebhook, "Without protection nothing is stored");
            var both = BotWebhooks.ForTokenPath(Path.Combine(folder, "discord-bot.enhanced.token"));
            Check(both.Audit.FilePath != both.Community.FilePath && both.Audit.FilePath.EndsWith(".audit-webhook", StringComparison.Ordinal)
                  && both.Community.FilePath.EndsWith(".community-webhook", StringComparison.Ordinal)
                  && Path.GetDirectoryName(both.Audit.FilePath) == folder && Path.GetDirectoryName(both.Community.FilePath) == folder, "The two webhooks are two files next to the token");

            await HttpClientAsync(Check);

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

            // a secret pasted into the wrong field is not accepted (it would be saved in plain text and posted in public)
            var token = string.Join(".", "MTIzNDU2Nzg5MDEyMzQ1Njc4", "GabcDE", "abcdefghijklmnopqrstuvwxyz0123456789ABCD");
            var knownSecret = "zz-known-secret-for-the-name-check";
            BotLogRedactor.RegisterSecret(knownSecret);
            foreach (var secret in new[] { token, "Bot " + token, "Wächter " + token, WebhookUrl, "see " + WebhookUrl + "?thread_id=1", knownSecret })
            {
                var named = BotPolicy.Create(Config(c => { c.CommunityName = secret; c.AuditName = "ok"; }));
                Check(!named.IsValid && named.Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidName && x.Detail == nameof(DiscordBotConfig.CommunityName))
                      && !named.Issues.Any(x => x.Detail.Contains("abcdefghijklmnop")), "A secret is no name of the community log: " + secret[..Math.Min(12, secret.Length)]);
                Check(BotPolicy.Create(Config(c => c.AuditName = secret)).Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidName && x.Detail == nameof(DiscordBotConfig.AuditName)),
                    "...nor of the audit log");
            }
            foreach (var secret in new[] { WebhookUrl, WebhookUrl + "?thread_id=1", "https://example.org/a.png?key=" + token, "https://example.org/" + knownSecret + ".png", "https://user:pw@example.org/a.png" })
                Check(BotPolicy.Create(Config(c => c.CommunityAvatarUrl = secret)).Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidUrl && x.Detail == nameof(DiscordBotConfig.CommunityAvatarUrl))
                      && BotPolicy.Create(Config(c => c.AuditAvatarUrl = secret)).Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidUrl && x.Detail == nameof(DiscordBotConfig.AuditAvatarUrl)),
                    "A secret is no picture link: " + secret[..Math.Min(30, secret.Length)]);
            Check(BotPolicy.Create(Config(c => { c.CommunityAvatarUrl = "https://cdn.discordapp.com/avatars/123456789012345678/abcdef0123456789.png?size=1024"; c.CommunityName = "Schatten und Licht Server"; })).IsValid,
                "A normal picture link and name are fine");

            // Discord refuses some names for a webhook message: better told in the settings than dropped silently later
            foreach (var forbidden in new[] { "Discord Wächter", "my CLYDE", "DISCORD", "xdiscordx" })
                Check(BotPolicy.Create(Config(c => c.AuditName = forbidden)).Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidName && x.Detail == nameof(DiscordBotConfig.AuditName))
                      && BotPolicy.Create(Config(c => c.CommunityName = forbidden)).Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidName), "Discord does not take this name: " + forbidden);
            Check(BotPolicy.Create(Config(c => { c.AuditName = "Dis cord"; c.CommunityName = "Wächter ⚔️"; })).IsValid, "Other names are fine");
            // invisible characters are removed from the name before it is sent: the name that is sent is the one that is checked
            foreach (var hidden in new[] { "Dis\u200Bcord Log", "Cly\u00ADde", "DIS\u2060CORD", "Wächter " + token.Insert(20, "\u200B"), "\u200D" + knownSecret })
                Check(BotPolicy.Create(Config(c => c.CommunityName = hidden)).Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidName)
                      && BotPolicy.Create(Config(c => c.AuditName = hidden)).Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidName), "A name is not clean because of a hidden character: " + hidden.Length);

            // the cut at 80 characters never splits an emoji (a pair of surrogates)
            var cutAtEmoji = PolicyOf(Config(c => c.CommunityName = new string('a', 79) + "😀 tail")).CommunityName;
            Check(cutAtEmoji == new string('a', 79) && NoLoneSurrogate(cutAtEmoji), "An emoji over the border is left out whole, not split: " + cutAtEmoji.Length);
            var emojiFits = PolicyOf(Config(c => c.CommunityName = new string('a', 78) + "😀😀")).CommunityName;
            Check(emojiFits == new string('a', 78) + "😀" && NoLoneSurrogate(emojiFits), "...and one that fits is kept");
            var flag = PolicyOf(Config(c => c.CommunityName = new string('a', 78) + "🇩🇪 tail")).CommunityName;
            Check(flag == new string('a', 78), "A flag (two letters of symbols) over the border is left out whole: " + flag.Length);
            var thumb = PolicyOf(Config(c => c.CommunityName = new string('a', 78) + "👍🏽 tail")).CommunityName;
            Check(thumb == new string('a', 78), "...and an emoji with a skin tone, too: " + thumb.Length);
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
                      && hooks.Sent[0].Url == WebhookUrl && hooks.Sent[0].Guild == Guild, "With a webhook the line goes through it, under the name and with the picture of the settings, for the server of the bot");
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

                // A webhook that does not work (deleted, a name Discord refuses): the audit channel gets the line when one is set.
                policy = PolicyOf(Config(c => c.AuditChannelId = AuditChannel));
                var notesBefore = channel.Notes.Count;
                hooks.Failure = new InvalidOperationException("Unknown Webhook " + WebhookUrl);
                Write("Done");
                await feed.FlushAsync(TimeSpan.FromSeconds(5));
                hooks.Failure = null;
                Check(channel.Notes.Count == notesBefore + 1 && hooks.Messages.Length == 3, "A webhook that refuses a line: the audit channel gets it instead");
            }

            // A webhook that hangs: after its budget the call is cancelled and the line goes to the audit channel once, not twice.
            var hangingHooks = new FakeWebhook { Hang = true };
            var hangingChannel = new RecordingChannel();
            var hangingLog = new LogLines();
            using (var feed = new BotAuditChannelFeed(audit, hangingChannel, () => policy, _ => "Haupt", hangingLog, hangingHooks, () => WebhookUrl)
                   { Spacing = TimeSpan.Zero, WebhookCacheTime = TimeSpan.Zero, WebhookBudget = TimeSpan.FromMilliseconds(200) })
            {
                feed.Start();
                audit.Write(new BotAuditEntry(DateTime.UtcNow, BotAuditResult.Executed, Operator, "x", Guild, Channel, BotCommand.Stop, "instance=0", "Done"));
                await feed.FlushAsync(TimeSpan.FromSeconds(5));
            }
            Check(hangingChannel.Notes.Count == 1 && hangingHooks.Messages.Length == 0 && hangingHooks.Cancelled == 1
                  && hangingLog.Lines.Count(x => x.StartsWith("Warning: Discord bot: the audit webhook refused", StringComparison.Ordinal)) == 1, "A webhook that takes too long is cancelled, and the line is delivered once (to the channel)");

            // The address of a webhook never reaches the log, whatever the source of it is (not only a saved one).
            var foreignUrl = "https://discord.com/api/webhooks/987654321098765432/ZYXWVUTSRQPONMLKJIHGFEDCBA9876543210zyxw";
            var feedLog = new LogLines();
            var refusing = new FakeWebhook { Failure = new InvalidOperationException("Unknown Webhook " + foreignUrl) };
            using (var feed = new BotAuditChannelFeed(audit, channel, () => policy, _ => "Haupt", feedLog, refusing, () => foreignUrl) { Spacing = TimeSpan.Zero, WebhookCacheTime = TimeSpan.Zero })
            {
                feed.Start();
                audit.Write(new BotAuditEntry(DateTime.UtcNow, BotAuditResult.Executed, Operator, "x", Guild, Channel, BotCommand.Stop, "instance=0", "Done"));
                await feed.FlushAsync(TimeSpan.FromSeconds(5));
            }
            Check(feedLog.Lines.Count(x => x.StartsWith("Warning: Discord bot: the audit webhook refused", StringComparison.Ordinal)) == 1
                  && !string.Join('\n', feedLog.Lines).Contains("ZYXWVUTSRQPONMLKJIHGFEDCBA9876543210zyxw"), "The warning of the audit feed holds no part of the address");

            // --- the community log
            await CommunityAsync(Check, folder);
            // --- the automatic restarts of the launcher (schedule, countdown, update round), from its own status
            await AutomaticAsync(Check);
            await ManualStopAsync(Check);
            await RestartSeenLateAsync(Check);
            await PlanChangesAsync(Check);
            await PostponedAsync(Check);
            await AwayAsync(Check);
            await GateAsync(Check);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        return count;
    }

    // The real client against a fake Discord: what goes over the wire, the check of the server, the limits of Discord.
    private static async Task HttpClientAsync(Action<bool, string> check)
    {
        const string ThreadId = "555555555555555555";
        var stamp = new DateTime(2026, 10, 24, 5, 30, 0, DateTimeKind.Utc);
        var embed = new BotWebhookEmbed(new string('T', 400), new string('D', 5000), BotCommunityLog.Orange, stamp);
        var http = new FakeDiscordHttp();
        using var client = new HttpBotWebhookClient(null, http);

        await client.SendAsync(WebhookUrl + "?thread_id=" + ThreadId, new BotWebhookMessage("Schatten und Licht Server", "https://example.org/logo.png", null, embed), Guild, CancellationToken.None);
        var post = http.Requests.Single(x => x.Method == HttpMethod.Post);
        var json = System.Text.Json.Nodes.JsonNode.Parse(post.Body)!.AsObject();
        var sentEmbed = json["embeds"]!.AsArray().Single()!.AsObject();
        check(http.Gets == 1 && http.Requests[0].Url == HttpBotWebhookClient.Versioned(WebhookUrl) && http.Requests[0].Url.Contains("/api/v10/webhooks/", StringComparison.Ordinal),
            "The webhook is asked which server it belongs to (by the named version of the API), without the thread");
        check(post.Url == HttpBotWebhookClient.Versioned(WebhookUrl) + "?thread_id=" + ThreadId, "The message goes to the thread of the address");
        check(json["username"]!.GetValue<string>() == "Schatten und Licht Server" && json["avatar_url"]!.GetValue<string>() == "https://example.org/logo.png"
              && json["content"] is null && json["allowed_mentions"]!["parse"]!.AsArray().Count == 0,
            "The name and picture of the settings are sent, and no mention pings: " + post.Body[..Math.Min(200, post.Body.Length)]);
        check(sentEmbed["title"]!.GetValue<string>().Length == 256 && sentEmbed["description"]!.GetValue<string>().Length <= 4000
              && sentEmbed["color"]!.GetValue<int>() == (int)BotCommunityLog.Orange && sentEmbed["timestamp"]!.GetValue<string>() == "2026-10-24T05:30:00.000Z",
            "The embed has its color and time and stays inside the limits");

        await client.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, new string('x', 3000), null), Guild, CancellationToken.None);
        var second = System.Text.Json.Nodes.JsonNode.Parse(http.Requests[^1].Body)!.AsObject();
        check(http.Gets == 1 && http.Requests[^1].Url == HttpBotWebhookClient.Versioned(WebhookUrl) && second["username"] is null && second["avatar_url"] is null && second["embeds"] is null
              && second["content"]!.GetValue<string>().Length <= 2000, "A text goes as content with the name of the webhook, and the server is asked once, not per message");

        var before = http.Posts;
        var other = await Capture(() => client.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), Guild + 1, CancellationToken.None));
        var none = await Capture(() => client.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), 0, CancellationToken.None));
        check(other is BotWebhookException && none is BotWebhookException && http.Posts == before
              && !other.Message.Contains("abcdefghijklmnop"), "A webhook of another server is never written to: " + other?.Message);

        // Any server (the community log): the webhook is not asked which server it belongs to, and the message goes out.
        var anyHttp = new FakeDiscordHttp();
        using (var anyClient = new HttpBotWebhookClient(null, anyHttp))
        {
            await anyClient.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), BotWebhook.AnyServer, CancellationToken.None);
            check(anyHttp.Gets == 0 && anyHttp.Posts == 1, "A webhook that may belong to any server is written to without asking for its server");
        }

        // The webhook is gone (404): refused, and asked again next time.
        http.PostAnswer = _ => Answer(System.Net.HttpStatusCode.NotFound, "{\"message\":\"Unknown Webhook\",\"code\":10015}");
        var gone = await Capture(() => client.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), Guild, CancellationToken.None));
        check(gone is BotWebhookException { Message: var text } && text.Contains("HTTP 404") && text.Contains("10015") && text.Contains("Unknown Webhook")
              && !text.Contains("abcdefghijklmnop") && !text.Contains("123456789012345678"), "A refused message tells status and code, never the address: " + gone?.Message);
        var getsBefore = http.Gets;
        http.PostAnswer = null;
        await client.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), Guild, CancellationToken.None);
        check(http.Gets == getsBefore + 1, "After that the webhook is asked again (it may exist again)");

        // The limit of Discord: once more after the short time it names, not when it asks for long.
        http.PostAnswer = n => n % 2 == 1 ? Answer(System.Net.HttpStatusCode.TooManyRequests, "{\"message\":\"You are being rate limited.\",\"retry_after\":0.05,\"global\":false}")
            : new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        var postsBefore = http.Posts;
        await client.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "limited", null), Guild, CancellationToken.None);
        check(http.Posts == postsBefore + 2, "A message that hit the limit goes out again after the time Discord named");
        http.PostAnswer = _ => Answer(System.Net.HttpStatusCode.TooManyRequests, "{\"message\":\"You are being rate limited.\",\"retry_after\":30}");
        postsBefore = http.Posts;
        var slow = await Capture(() => client.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "limited", null), Guild, CancellationToken.None));
        check(slow is BotWebhookException && http.Posts == postsBefore + 1, "A long wait is not waited for: the message is dropped");
        http.PostAnswer = _ => Answer(System.Net.HttpStatusCode.TooManyRequests, "{\"retry_after\":0.01}");
        postsBefore = http.Posts;
        var twice = await Capture(() => client.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "limited", null), Guild, CancellationToken.None));
        check(twice is BotWebhookException && http.Posts == postsBefore + 2, "...and it is tried once more at most");
        http.PostAnswer = null;

        // A bad name: Discord says which field, the message is dropped.
        http.PostAnswer = _ => Answer(System.Net.HttpStatusCode.BadRequest, "{\"message\":\"Invalid Form Body\",\"code\":50035,\"errors\":{\"username\":{}}}");
        var refused = await Capture(() => client.SendAsync(WebhookUrl, new BotWebhookMessage("x", null, "x", null), Guild, CancellationToken.None));
        check(refused is BotWebhookException { Message: var why } && why.Contains("400") && why.Contains("50035"), "Discord refusing the form is reported with its code: " + refused?.Message);
        http.PostAnswer = null;

        // No connection: the text of the error may carry the address, it must not get out.
        http.NetworkFailure = new HttpRequestException("Could not connect to " + WebhookUrl);
        var down = await Capture(() => client.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), Guild, CancellationToken.None));
        http.NetworkFailure = null;
        check(down is BotWebhookException && !down.Message.Contains("abcdefghijklmnop"), "A lost connection is an error without the address: " + down?.Message);

        // One time budget for all of it: when it is up the call is cancelled, not left running to deliver later.
        var slowHttp = new FakeDiscordHttp { Delay = TimeSpan.FromSeconds(5) };
        using (var slowClient = new HttpBotWebhookClient(null, slowHttp))
        {
            var started = DateTime.UtcNow;
            var late = await Capture(() => slowClient.SendWithinAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), Guild, TimeSpan.FromMilliseconds(300), CancellationToken.None));
            await Task.Delay(300);
            check(late is BotWebhookException && DateTime.UtcNow - started < TimeSpan.FromSeconds(3) && slowHttp.Posts == 0 && slowHttp.Cancelled >= 1,
                "A call that is slower than its budget is cancelled and ends in an error, not delivered later: " + late?.GetType().Name);
            using var outer = new CancellationTokenSource(100);
            check(await Capture(() => slowClient.SendWithinAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), Guild, TimeSpan.FromSeconds(30), outer.Token)) is OperationCanceledException,
                "...but a cancel from outside stays a cancel");
        }
        var limitedHttp = new FakeDiscordHttp
        {
            PostAnswer = n => n == 1 ? Answer(System.Net.HttpStatusCode.TooManyRequests, "{\"retry_after\":9.5}") : new HttpResponseMessage(System.Net.HttpStatusCode.NoContent)
        };
        using (var limitedClient = new HttpBotWebhookClient(null, limitedHttp))
        {
            var limited = await Capture(() => limitedClient.SendWithinAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), Guild, TimeSpan.FromMilliseconds(400), CancellationToken.None));
            await Task.Delay(300);
            check(limited is BotWebhookException && limitedHttp.Posts == 1, "Waiting for the limit of Discord is part of the budget: " + limited?.GetType().Name + " " + limitedHttp.Posts);
        }

        // Wrong answers about the server, a wrong address, a cancelled call.
        using var broken = new HttpBotWebhookClient(null, new FakeDiscordHttp { GuildAnswer = "{\"id\":\"1\"}" });
        check(await Capture(() => broken.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), Guild, CancellationToken.None)) is BotWebhookException,
            "A webhook that does not say its server is not written to");
        using var garbage = new HttpBotWebhookClient(null, new FakeDiscordHttp { GuildAnswer = "<html>" });
        check(await Capture(() => garbage.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), Guild, CancellationToken.None)) is BotWebhookException, "...nor one with a nonsense answer");
        check(await Capture(() => client.SendAsync("https://example.org/hook", new BotWebhookMessage(null, null, "x", null), Guild, CancellationToken.None)) is ArgumentException,
            "Only Discord webhook addresses are used");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        check(await Capture(() => client.SendAsync(WebhookUrl, new BotWebhookMessage(null, null, "x", null), Guild, cancel.Token)) is OperationCanceledException, "A cancelled call is cancelled, not an error");
    }

    private static bool NoLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
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

    // A launcher and a community log with a clock of their own. The status of the launcher is turned into what the bot sees by
    // the same conversion the bot uses, so what the log reads is what the launcher really reports. While time passes it looks
    // the way the bot does: when it asked for it, and at the latest every five minutes.
    private sealed class Scene : IDisposable
    {
        private static readonly TimeSpan MaxGap = BotCommunityLog.CalmInterval;
        public DateTime Now = new(2026, 10, 24, 5, 0, 0, DateTimeKind.Utc);
        public readonly StatusActions Actions = new();
        public readonly FakeWebhook Hooks = new();
        public readonly BotCommunityLog Log;
        public readonly DateTime Started;

        public Scene()
        {
            var policy = PolicyOf(Config(c => c.CommunityEnabled = true));
            Log = new BotCommunityLog(Actions, Hooks, () => WebhookUrl, () => policy, () => Now) { Spacing = TimeSpan.Zero, WebhookCacheTime = TimeSpan.Zero };
            Log.Start();
            Started = Now.AddHours(-5);
        }

        public TrebuchetLib.Services.LauncherServerStatus Launcher(TrebuchetLib.ProcessState? state, Func<TrebuchetLib.Services.LauncherServerStatus, TrebuchetLib.Services.LauncherServerStatus>? change = null)
        {
            var status = new TrebuchetLib.Services.LauncherServerStatus { Instance = 0, SaveName = "Exiles", ServerName = "Schatten und Licht", Installed = true, State = state, StartedUtc = Started };
            return change is null ? status : change(status);
        }

        private TrebuchetLib.Services.LauncherServerStatus? _last;

        // The new status is seen at the end of the time that passes; on the way the log looks at the one before.
        public async Task Look(TrebuchetLib.Services.LauncherServerStatus status, double advanceSeconds)
        {
            var target = Now.AddSeconds(advanceSeconds);
            var earlier = _last ?? status;
            while (target - Now > MaxGap)
            {
                Actions.Statuses = [LauncherBotActions.ToBotStatus(earlier, Now)];
                Now += await Log.RefreshAsync(CancellationToken.None);
            }
            Now = target;
            _last = status;
            Actions.Statuses = [LauncherBotActions.ToBotStatus(status, Now)];
            await Log.RefreshAsync(CancellationToken.None);
            await Log.FlushAsync(TimeSpan.FromSeconds(5));
        }

        public string[] Titles() => Hooks.Messages.Select(x => x.Embed!.Title).ToArray();

        public void Dispose() => Log.Dispose();
    }

    private static string Unix(DateTime time) => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString();

    private static async Task AutomaticAsync(Action<bool, string> check)
    {
        using var scene = new Scene();
        var hooks = scene.Hooks;
        var now = scene.Now;
        var Launcher = scene.Launcher;
        Task Look(TrebuchetLib.Services.LauncherServerStatus status, double advanceSeconds) => scene.Look(status, advanceSeconds);
        string[] Titles() => scene.Titles();

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
        var updateAt = scene.Now.AddMinutes(10);
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
    }

    // A stop by hand is a stop, whatever the launcher has planned around it.
    private static async Task ManualStopAsync(Action<bool, string> check)
    {
        // An hour before the daily restart (the launcher announces it that far ahead): the admin stops the server by hand.
        using (var scene = new Scene())
        {
            var restartAt = scene.Now.AddMinutes(55);
            var planned = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { NextScheduledRestartUtc = restartAt });
            await scene.Look(planned, 0);
            await scene.Look(planned, 60);
            check(LauncherBotActions.ToBotStatus(planned, scene.Now) is { Pending: BotPendingAction.Restart } && scene.Hooks.Messages.Length == 0, "The restart of the schedule is known an hour ahead, and not yet told");
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.STOPPING), 30);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.STOPPED), 20);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.STOPPED), 3 * 3600);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE), 20);
            var texts = scene.Hooks.Messages.Select(x => x.Embed!).ToArray();
            check(texts.Length == 2 && texts[0].Title == "⏹️ Der Server wird jetzt gestoppt" && texts[1].Title == "🟢 Der Server ist wieder online"
                  && texts[1].Description.StartsWith("Ihr könnt euch wieder einloggen.", StringComparison.Ordinal) && !texts[1].Description.Contains("Neustart"),
                "A stop by hand shortly before the schedule is told as a stop, and its end without the word restart: " + string.Join(" | ", texts.Select(x => x.Title + "/" + x.Description)));
        }

        // The server is part of an update round (not yet restarting) and is stopped by hand.
        using (var scene = new Scene())
        {
            var inRound = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { InUpdateRound = true });
            await scene.Look(inRound, 0);
            await scene.Look(inRound, 60);
            var stopping = scene.Launcher(TrebuchetLib.ProcessState.STOPPING, x => x with { InUpdateRound = true });
            check(!LauncherBotActions.ToBotStatus(stopping, scene.Now).Restarting, "Being in an update round does not make a stop a restart");
            await scene.Look(stopping, 30);
            check(scene.Titles().SequenceEqual(["⏹️ Der Server wird jetzt gestoppt"]), "A stop by hand in an update round is told as a stop: " + string.Join(" | ", scene.Titles()));
        }

        // The restart of the schedule itself, a few seconds before the time it was planned for: a restart.
        using (var scene = new Scene())
        {
            var restartAt = scene.Now.AddMinutes(45);
            var planned = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { NextScheduledRestartUtc = restartAt });
            await scene.Look(planned, 0);
            await scene.Look(planned, 44 * 60 + 58);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.STOPPING), 30);
            check(scene.Titles().SequenceEqual(["⏰ Der Server startet in 30 Minuten neu", "🔄 Der Server startet jetzt neu"]),
                "A restart that is seen only when the process is already going down is still a restart: " + string.Join(" | ", scene.Titles()));
        }
    }

    // A postponed restart begins once the pause is over, and the server goes down after the warnings: that is the time told.
    private static async Task PostponedAsync(Action<bool, string> check)
    {
        using var scene = new Scene();
        var pauseEnd = scene.Now.AddMinutes(90);
        var postponed = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
        {
            RestartPostponed = true, PausedUntilUtc = pauseEnd, RestartWarningLead = TimeSpan.FromMinutes(15)
        });
        var goesDown = pauseEnd.AddMinutes(15);
        check(LauncherBotActions.ToBotStatus(postponed, scene.Now) is { Pending: BotPendingAction.Restart, PendingAtUtc: { } at } && at == goesDown,
            "The time of a postponed restart is the end of the pause plus the warnings");
        await scene.Look(postponed, 0);
        await scene.Look(postponed, 74 * 60);
        check(scene.Hooks.Messages.Length == 0, "Nothing is told before the lead time");
        await scene.Look(postponed, 60);
        check(scene.Titles().SequenceEqual(["⏰ Der Server startet in 30 Minuten neu"])
              && scene.Hooks.Messages[0].Embed!.Description.Contains($"<t:{Unix(goesDown)}:t>", StringComparison.Ordinal),
            "It is announced with the time the server really goes down: " + string.Join(" | ", scene.Hooks.Messages.Select(x => x.Embed!.Description)));
        // The pause is over and the launcher counts down for it: the same restart, not told again with another time.
        await scene.Look(postponed, (pauseEnd - scene.Now).TotalSeconds + 1);
        await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
        {
            CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = goesDown.AddSeconds(3), RestartRequested = true, SequenceRunning = true
        }), 30);
        check(scene.Hooks.Messages.Length == 1, "The countdown after the pause is not announced again with a time 15 minutes later: " + string.Join(" | ", scene.Titles()));
    }

    // The bot was away (off, no connection to Discord): what happened meanwhile is old news, not "now".
    private static async Task AwayAsync(Action<bool, string> check)
    {
        using var scene = new Scene();
        await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE), 0);
        await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE), 300);
        scene.Now = scene.Now.AddHours(6);
        await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.STOPPED), 0);
        await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.STOPPED), 60);
        await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE), 60);
        // The stop is old news; the start the bot saw happen (it was stopped at its first look, a minute later it is online) is told.
        check(scene.Titles().SequenceEqual(["🟢 Der Server ist online"]), "A stop that happened while the bot was away is not told as happening now, a start it saw is: " + string.Join(" | ", scene.Titles()));
        // The same stop seen at the usual pace is told.
        await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.STOPPING), 240);
        check(scene.Titles().SequenceEqual(["🟢 Der Server ist online", "⏹️ Der Server wird jetzt gestoppt"]), "...while a stop seen at the usual pace is: " + string.Join(" | ", scene.Titles()));
        // The players were told it goes down, the bot was away while it came back: the end is told late, without a made-up offline time.
        scene.Now = scene.Now.AddHours(2);
        await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE), 0);
        var late = scene.Hooks.Messages.Select(x => x.Embed!).ToArray();
        check(late.Length == 3 && late[2].Title == "🟢 Der Server ist wieder online" && late[2].Description == "Ihr könnt euch wieder einloggen.",
            "A server that came back while the bot was away is told late, without the offline time: " + string.Join(" | ", late.Select(x => x.Title + "/" + x.Description)));
        await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE), 120);
        check(scene.Hooks.Messages.Length == 3, "...once");
    }

    // The players were told a time: when the plan is cancelled or postponed they are told too, they would wait for it otherwise.
    private static async Task PlanChangesAsync(Action<bool, string> check)
    {
        // A restart in 30 minutes, postponed by 30 minutes, the postponement lifted again (the real case, through the launcher's status).
        using (var scene = new Scene())
        {
            var end = scene.Now.AddMinutes(30);
            var counting = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = end, SequenceRunning = true
            });
            await scene.Look(counting, 0);
            check(scene.Titles().SequenceEqual(["⏰ Der Server startet in 30 Minuten neu"]), "The restart in 30 minutes is announced: " + string.Join(" | ", scene.Titles()));
            var postponed = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                RestartPostponed = true, PausedUntilUtc = end.AddMinutes(25), RestartWarningLead = TimeSpan.FromMinutes(5)
            });
            await scene.Look(postponed, 120);
            var told = scene.Hooks.Messages.Select(x => x.Embed!).ToArray();
            check(told.Length == 2 && told[1].Title == "⏸️ Der Neustart wurde verschoben" && told[1].Color == BotCommunityLog.Blue
                  && told[1].Description.Contains($"<t:{Unix(end.AddMinutes(30))}:t>", StringComparison.Ordinal) && told[1].Description.Contains("Kurz vorher", StringComparison.Ordinal),
                "A restart postponed beyond the lead time is told with the new time: " + string.Join(" | ", told.Select(x => x.Title + "/" + x.Description)));
            await scene.Look(postponed, 60);
            check(scene.Hooks.Messages.Length == 2, "...once");
            // Lifted at once: the old time is back, and the players are told it (a countdown with the old time), not left with the new one.
            await scene.Look(counting, 60);
            var back = scene.Hooks.Messages.Select(x => x.Embed!).ToArray();
            check(back.Length == 3 && back[2].Title == "⏰ Der Server startet in 26 Minuten neu" && back[2].Description.Contains($"<t:{Unix(end)}:t>", StringComparison.Ordinal),
                "When the postponement is lifted the old time is announced again: " + string.Join(" | ", back.Select(x => x.Title)));
        }

        // Cancelled and reported by the launcher: told at the very first look, no waiting to be sure.
        using (var scene = new Scene())
        {
            var end = scene.Now.AddMinutes(20);
            var counting = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = end, SequenceRunning = true
            });
            await scene.Look(counting, 0);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { CountdownCancelledUtc = scene.Now.AddSeconds(25) }), 30);
            var reported = scene.Hooks.Messages.Select(x => x.Embed!).ToArray();
            check(reported.Length == 2 && reported[1].Title == "✅ Der Neustart fällt aus" && reported[1].TimestampUtc == scene.Now,
                "A cancel the launcher reports is told at the first look: " + string.Join(" | ", reported.Select(x => x.Title)));
            // A cancel from before the announcement is not a cancel of this announcement.
            using var older = new Scene();
            var olderEnd = older.Now.AddMinutes(20);
            await older.Look(older.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = olderEnd, SequenceRunning = true, CountdownCancelledUtc = older.Now.AddHours(-3)
            }), 0);
            await older.Look(older.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { CountdownCancelledUtc = older.Now.AddHours(-3) }), 3);
            check(older.Hooks.Messages.Length == 1, "An old cancel marker says nothing about the countdown that was announced after it");
        }

        // Gone without a report: told when it stays gone for a few seconds (a changeover shows nothing planned for a moment), once.
        using (var scene = new Scene())
        {
            var end = scene.Now.AddMinutes(20);
            var counting = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = end, SequenceRunning = true
            });
            var calm = scene.Launcher(TrebuchetLib.ProcessState.ONLINE);
            await scene.Look(counting, 0);
            await scene.Look(calm, 30);
            check(scene.Hooks.Messages.Length == 1, "A countdown that is gone is not called cancelled at the first look");
            await scene.Look(counting, 3);
            await scene.Look(calm, 3);
            check(scene.Hooks.Messages.Length == 1, "...and a gap of a moment is no cancellation");
            var wait = await scene.Log.RefreshAsync(CancellationToken.None);
            check(wait <= BotCommunityLog.BusyInterval, "While it waits to be sure it looks again soon: " + wait);
            await scene.Look(calm, 5);
            check(scene.Hooks.Messages.Length == 1, "...and 5 seconds are not yet sure");
            await scene.Look(calm, 5);
            var cancelled = scene.Hooks.Messages.Select(x => x.Embed!).ToArray();
            check(cancelled.Length == 2 && cancelled[1].Title == "✅ Der Neustart fällt aus" && cancelled[1].Color == BotCommunityLog.Green
                  && cancelled[1].Description == "Der angekündigte Neustart findet nicht statt – der Server bleibt online.",
                "A countdown that stays gone is announced as cancelled: " + string.Join(" | ", cancelled.Select(x => x.Title)));
            await scene.Look(calm, 60);
            await scene.Look(calm, 60);
            check(scene.Hooks.Messages.Length == 2, "...once");
            // The same restart planned again for about the same time is announced again (the cancellation forgot the first announcement).
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = end, SequenceRunning = true
            }), 30);
            check(scene.Hooks.Messages.Length == 3 && scene.Hooks.Messages[2].Embed!.Title.StartsWith("⏰", StringComparison.Ordinal), "...and a new plan for the same time is announced again");
        }

        // A stop is called a stop; a stop that is planned instead of a restart is a new plan, not a postponement; going down is no cancellation.
        using (var scene = new Scene())
        {
            var stopAt = scene.Now.AddMinutes(10);
            var stopping = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { CountdownRunning = true, CountdownEndsUtc = stopAt, SequenceRunning = true });
            await scene.Look(stopping, 0);
            var calm = scene.Launcher(TrebuchetLib.ProcessState.ONLINE);
            await scene.Look(calm, 30);
            await scene.Look(calm, 30);
            check(scene.Titles().SequenceEqual(["⏰ Der Server wird in 10 Minuten gestoppt", "✅ Der Stopp fällt aus"])
                  && scene.Hooks.Messages[1].Embed!.Description == "Der angekündigte Stopp findet nicht statt – der Server bleibt online.",
                "A cancelled stop says stop: " + string.Join(" | ", scene.Titles()));
        }
        using (var scene = new Scene())
        {
            var restartAt = scene.Now.AddMinutes(20);
            var restart = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = restartAt, SequenceRunning = true
            });
            await scene.Look(restart, 0);
            // Changed into a stop for much later, further away than the lead time: no "postponed restart", the plan is a different one.
            var stop = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { CountdownRunning = true, CountdownEndsUtc = scene.Now.AddMinutes(90), SequenceRunning = true });
            await scene.Look(stop, 60);
            check(scene.Titles().SequenceEqual(["⏰ Der Server startet in 20 Minuten neu", "✅ Der Neustart fällt aus"]),
                "A stop planned instead of a restart (further away than the lead time) says the restart does not happen: " + string.Join(" | ", scene.Titles()));
            await scene.Look(stop, 60);
            check(scene.Hooks.Messages.Length == 2, "...once, and a stop is no postponement");
            // The announced restart goes down at its time: that is not a cancellation, whatever the launcher shows meanwhile.
        }
        using (var scene = new Scene())
        {
            var restartAt = scene.Now.AddMinutes(20);
            var restart = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = restartAt, SequenceRunning = true
            });
            await scene.Look(restart, 0);
            await scene.Look(restart, 19 * 60 + 30);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE), 20);
            await scene.Look(scene.Launcher(null, x => x with { ShuttingDown = true }), 20);
            check(!scene.Titles().Any(x => x.Contains("fällt aus", StringComparison.Ordinal)), "The restart that happens as announced is never called cancelled: " + string.Join(" | ", scene.Titles()));
        }

        // A restart "now" while one was announced, with a stopping sequence of the save of its own: the launcher shows nothing planned
        // while that sequence runs. That is the server going down, never a cancellation.
        using (var scene = new Scene())
        {
            var restartAt = scene.Now.AddMinutes(20);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = restartAt, SequenceRunning = true
            }), 0);
            var inSequence = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { SequenceRunning = true });
            for (var i = 0; i < 4; i++) await scene.Look(inSequence, 30);
            check(scene.Titles().SequenceEqual(["⏰ Der Server startet in 20 Minuten neu"]), "A sequence of the save that runs is no cancellation: " + string.Join(" | ", scene.Titles()));
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.STOPPING, x => x with { ProcessRestarts = true, SequenceRunning = true }), 20);
            check(scene.Titles().SequenceEqual(["⏰ Der Server startet in 20 Minuten neu", "🔄 Der Server startet jetzt neu"]), "...the restart is told when it begins: " + string.Join(" | ", scene.Titles()));
        }
        // A stop "now" while one was announced: the launcher says it is going down (its notice to the players can take a long time), that is no
        // cancellation however long it takes.
        using (var scene = new Scene())
        {
            var stopAt = scene.Now.AddMinutes(15);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { CountdownRunning = true, CountdownEndsUtc = stopAt, SequenceRunning = true }), 0);
            var goingDown = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { GoingDownNow = true });
            await scene.Look(goingDown, 5);
            await scene.Look(goingDown, 15);
            await scene.Look(goingDown, 20);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.STOPPING), 5);
            check(scene.Titles().SequenceEqual(["⏰ Der Server wird in 15 Minuten gestoppt", "⏹️ Der Server wird jetzt gestoppt"]),
                "A stop now that takes 40 seconds to show itself is no cancellation either: " + string.Join(" | ", scene.Titles()));
            check(LauncherBotActions.ToBotStatus(goingDown, scene.Now) is { Sequence: true, Pending: BotPendingAction.None },
                "The launcher's 'going down now' reaches the status of the bot");
        }
        // A cancel that lands while the launcher is still busy with the warning of the countdown (it counts as going down for a moment): the
        // cancel the launcher reports is told at once, it is not swallowed by the sequence.
        using (var scene = new Scene())
        {
            var end = scene.Now.AddMinutes(20);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = end, SequenceRunning = true
            }), 0);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { GoingDownNow = true, CountdownCancelledUtc = scene.Now.AddSeconds(5) }), 10);
            check(scene.Titles().SequenceEqual(["⏰ Der Server startet in 20 Minuten neu", "✅ Der Neustart fällt aus"]),
                "A reported cancel is told even while the launcher is still busy with a sequence: " + string.Join(" | ", scene.Titles()));
        }

        // A postponement within the lead time is told by the new countdown announcement alone.
        using (var scene = new Scene())
        {
            var first = scene.Now.AddMinutes(25);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = first, SequenceRunning = true
            }), 0);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with
            {
                CountdownRunning = true, CountdownRestarts = true, CountdownEndsUtc = first.AddMinutes(4), SequenceRunning = true
            }), 30);
            check(scene.Titles().SequenceEqual(["⏰ Der Server startet in 25 Minuten neu", "⏰ Der Server startet in 29 Minuten neu"]),
                "A postponement that stays within the lead time is one new announcement, not two messages: " + string.Join(" | ", scene.Titles()));
        }
    }

    // The restart of a server is a restart even when the log sees it late or only half of it.
    private static async Task RestartSeenLateAsync(Action<bool, string> check)
    {
        // The bot lost Discord shortly before the planned restart and is back 7 minutes after it, the process already starting.
        using (var scene = new Scene())
        {
            var restartAt = scene.Now.AddMinutes(35);
            var planned = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { NextScheduledRestartUtc = restartAt });
            await scene.Look(planned, 0);
            await scene.Look(planned, 5 * 60);
            await scene.Look(planned, (restartAt - scene.Now).TotalSeconds - 60);
            scene.Now = scene.Now.AddMinutes(7);
            await scene.Look(scene.Launcher(null, x => x with { Starting = true }), 0);
            check(scene.Titles().SequenceEqual(["⏰ Der Server startet in 30 Minuten neu", "🔄 Der Server startet jetzt neu"]),
                "A restart seen 7 minutes late is still a restart: " + string.Join(" | ", scene.Titles()));
            scene.Now = scene.Now.AddMinutes(20);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE), 0);
            check(scene.Hooks.Messages.Length == 3 && scene.Hooks.Messages[2].Embed!.Description == "Der Neustart ist durch – ihr könnt euch wieder einloggen.",
                "...and its end, told after another gap, says it is done without a time: " + scene.Hooks.Messages[^1].Embed!.Description);
        }

        // The pause is over, the launcher has not begun to count down yet (no time for a moment): the planned time is not forgotten.
        using (var scene = new Scene())
        {
            var pauseEnd = scene.Now.AddMinutes(40);
            var paused = scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { RestartPostponed = true, PausedUntilUtc = pauseEnd });
            await scene.Look(paused, 0);
            await scene.Look(paused, (pauseEnd - scene.Now).TotalSeconds - 120);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { RestartPostponed = true }), 150);
            await scene.Look(scene.Launcher(null, x => x with { ShuttingDown = true }), 20);
            check(scene.Titles().SequenceEqual(["⏰ Der Server startet in 30 Minuten neu", "🔄 Der Server startet jetzt neu"]),
                "A postponed restart that goes down right after the pause is a restart: " + string.Join(" | ", scene.Titles()));
        }

        // A restart is requested (no time to name), and only the end of it is seen: still a restart.
        using (var scene = new Scene())
        {
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE), 0);
            await scene.Look(scene.Launcher(TrebuchetLib.ProcessState.ONLINE, x => x with { RestartRequested = true }), 60);
            await scene.Look(scene.Launcher(null, x => x with { ShuttingDown = true }), 20);
            check(scene.Titles().SequenceEqual(["🔄 Der Server startet jetzt neu"]), "A restart that was requested and is seen going down is a restart: " + string.Join(" | ", scene.Titles()));
        }
    }

    // Two looks at once (an old loop that is ending, a new one) are one after the other, not interleaved.
    private sealed class SlowActions : StatusActions
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        public int Started;
        public int MostAtOnce;

        public void Release() => _release.TrySetResult();

        public override async Task<IReadOnlyList<BotServerStatus>> GetStatusesAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Started);
            var now = Interlocked.Increment(ref _running);
            lock (this) MostAtOnce = Math.Max(MostAtOnce, now);
            await _release.Task;
            Interlocked.Decrement(ref _running);
            return await base.GetStatusesAsync(token);
        }
    }

    private static async Task GateAsync(Action<bool, string> check)
    {
        var slow = new SlowActions { Statuses = [Server(0, "Exiled Lands", BotServerState.Online)] };
        var policy = PolicyOf(Config(c => c.CommunityEnabled = true));
        using var log = new BotCommunityLog(slow, new FakeWebhook(), () => WebhookUrl, () => policy, () => DateTime.UtcNow);
        var first = log.RefreshAsync(CancellationToken.None);
        var second = log.RefreshAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource(50);
        var third = Capture(() => log.RefreshAsync(cancel.Token));
        await Task.Delay(200);
        check(slow.Started == 1 && slow.MostAtOnce == 1 && await third is OperationCanceledException, "A second look waits for the first (and can be cancelled while it waits)");
        slow.Release();
        await Task.WhenAll(first, second);
        check(slow.Started == 2 && slow.MostAtOnce == 1, "...and then goes on, one at a time");
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
        // Time passes the way it does for the bot: the log is looked at when it asked for it, at the latest every five minutes.
        async Task Until(DateTime target)
        {
            while (now < target)
            {
                var wanted = await log.RefreshAsync(CancellationToken.None);
                await log.FlushAsync(TimeSpan.FromSeconds(5));
                now += wanted < target - now ? wanted : target - now;
            }
        }

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
              && hooks.Sent[0].Url == WebhookUrl && hooks.Sent[0].Guild == BotWebhook.AnyServer, "...under the name and with the picture of the community, for any server (the community often has a server of its own)");
        check(due == BotCommunityLog.CalmInterval, "While it is announced it is looked at every five minutes: " + due);
        await Look(120);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online, BotPendingAction.Restart, restartAt.AddSeconds(2))];
        await Look(60);
        check(hooks.Messages.Length == 1, "The same event is not announced twice (the countdown ends a few seconds off the schedule)");
        await Until(restartAt.AddSeconds(-88));
        var closeDue = await Look();
        check(hooks.Messages.Length == 1 && closeDue == TimeSpan.FromSeconds(95), "Shortly before the restart it looks again when the server goes down: " + closeDue);

        // Postponed by an hour: told again when the new time comes within the lead time.
        var postponed = restartAt.AddHours(1);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online, BotPendingAction.Restart, postponed)];
        await Look(60);
        check(hooks.Messages.Length == 1, "A postponement outside the lead time says nothing");
        await Until(postponed.AddMinutes(-31));
        check(hooks.Messages.Length == 1, "...and says nothing while the new time is more than the lead time away");
        await Until(postponed.AddMinutes(-29));
        check(hooks.Messages.Length == 2 && Posts()[1].Title == "⏰ Der Server startet in 30 Minuten neu" && Posts()[1].Description.Contains($"<t:{Unix(postponed)}:t>", StringComparison.Ordinal),
            "...and is announced again, with the new time, exactly when it comes within the lead time: " + Posts()[^1].Title);

        // Going down: a restart is told at once, and "online again" says how long it was away.
        await Until(postponed.AddSeconds(-30));
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
        await Until(stopAt);
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

        // A server that is started (nobody was told that it went down: it was off when it was first seen) says it is online when it
        // is, without an offline time and without "again".
        var beforeStart = hooks.Messages.Length;
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online), Server(1, "Siptah", BotServerState.Stopped)];
        await Look(10);
        check(hooks.Messages.Length == beforeStart, "A server that is off all along says nothing");
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online), Server(1, "Siptah", BotServerState.Starting)];
        await Look(20);
        check(hooks.Messages.Length == beforeStart, "...and none that is starting");
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online), Server(1, "Siptah", BotServerState.Online)];
        await Look(30);
        check(hooks.Messages.Length == beforeStart + 1 && Posts()[^1] is { Title: "🟢 Der Server ist online", Color: BotCommunityLog.Green } startedPost
              && startedPost.Description == "**Siptah**\nDer Server wurde gestartet – ihr könnt euch jetzt einloggen.",
            "A server that was started tells when it is online: " + Posts()[^1].Title + " | " + Posts()[^1].Description);
        await Look(60);
        check(hooks.Messages.Length == beforeStart + 1, "...once");
        // The same after a stop that was told: then it is the "back online" with the time, not this.
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online), Server(1, "Siptah", BotServerState.Stopped)];
        await Look(10);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online), Server(1, "Siptah", BotServerState.Online)];
        await Look(40);
        check(hooks.Messages.Length == beforeStart + 3 && Posts()[^1].Title == "🟢 Der Server ist wieder online" && Posts()[^1].Description.Contains("**Offline:** 40 Sek.", StringComparison.Ordinal),
            "A stop that was told is answered by 'back online' with the time: " + Posts()[^1].Title + " | " + Posts()[^1].Description);

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
        // (an address nobody has registered anywhere else: the log itself must keep it out of its warning)
        var foreignUrl = "https://discord.com/api/webhooks/987654321098765433/QPONMLKJIHGFEDCBA9876543210zyxwvutsrQ";
        url = foreignUrl;
        hooks.Failure = new InvalidOperationException("Unknown Webhook " + foreignUrl);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Online), Server(1, "Siptah", BotServerState.Online)];
        await Look(10);
        actions.Statuses = [Server(0, "Exiled Lands", BotServerState.Stopping, restarting: true), Server(1, "Siptah", BotServerState.Stopping, restarting: true)];
        await Look(10);
        check(logger.Lines.Count(x => x.StartsWith("Warning: Discord bot: the community log webhook refused", StringComparison.Ordinal)) == 1
              && !string.Join('\n', logger.Lines).Contains("QPONMLKJIHGFEDCBA9876543210zyxwvutsrQ"), "A refusing webhook is one warning that holds no secret: " + string.Join('|', logger.Lines));
        hooks.Failure = null;
        url = WebhookUrl;

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
