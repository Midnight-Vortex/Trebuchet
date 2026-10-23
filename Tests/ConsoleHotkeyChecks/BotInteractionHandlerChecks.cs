using System.Globalization;
using TrebuchetLib.DiscordBot;

/// <summary>
/// The whole flow after Discord delivered a command or a click: gate, questions, action, audit, answer, and the status
/// board. Everything runs against fakes, nothing talks to Discord or to a server.
/// </summary>
static class BotInteractionHandlerChecks
{
    private const ulong Guild = 100000000000000001UL;
    private const ulong SuperAdmin = 200000000000000001UL;
    private const ulong Operator = 200000000000000002UL;
    private const ulong Stranger = 200000000000000003UL;
    private const ulong Role = 300000000000000001UL;
    private const ulong OtherRole = 300000000000000002UL;
    private const ulong Channel = 400000000000000001UL;
    private const ulong OtherChannel = 400000000000000002UL;
    private const ulong AuditChannel = 500000000000000001UL;

    private sealed class FakeActions : IBotActions
    {
        public int InstanceCount { get; set; } = 2;
        public List<string> Calls { get; } = [];
        public List<BotServerStatus> Statuses { get; set; } = [];
        public BotActionResult Result { get; set; } = new(BotActionOutcome.Done);
        public Func<Task<BotActionResult>>? Hook { get; set; }

        private Task<BotActionResult> Answer(string call)
        {
            Calls.Add(call);
            return Hook?.Invoke() ?? Task.FromResult(Result);
        }

        public Task<IReadOnlyList<BotServerStatus>> GetStatusesAsync(CancellationToken token)
        {
            Calls.Add("statuses");
            return Task.FromResult<IReadOnlyList<BotServerStatus>>(Statuses.ToList());
        }

        public Task<BotActionResult> PostponeAsync(int instance, int minutes, string reason, CancellationToken token) => Answer($"postpone {instance} {minutes} {reason}");
        public Task<BotActionResult> ResumeAsync(int instance, CancellationToken token) => Answer($"resume {instance}");
        public Task<BotActionResult> RestartAsync(int instance, TimeSpan delay, string reason, CancellationToken token) => Answer($"restart {instance} {delay.TotalMinutes} {reason}");
        public Task<BotActionResult> StopAsync(int instance, TimeSpan delay, string reason, CancellationToken token) => Answer($"stop {instance} {delay.TotalMinutes} {reason}");
        public Task<BotActionResult> StartAsync(int instance, CancellationToken token) => Answer($"start {instance}");
        public Task<BotActionResult> UpdateAsync(CancellationToken token) => Answer("update");
        public int Executed => Calls.Count(x => x != "statuses");
        public List<string> Done => Calls.Where(x => x != "statuses").ToList();
    }

    private sealed class FakeResponder : IBotResponder
    {
        public List<(bool Update, BotReply Reply)> Replies { get; } = [];
        public bool Throws { get; set; }

        public Task ReplyAsync(BotReply reply)
        {
            if (Throws) throw new InvalidOperationException("discord is down");
            Replies.Add((false, reply));
            return Task.CompletedTask;
        }

        public Task UpdateAsync(BotReply reply)
        {
            if (Throws) throw new InvalidOperationException("discord is down");
            Replies.Add((true, reply));
            return Task.CompletedTask;
        }

        public BotReply Last => Replies[^1].Reply;
    }

    private sealed class FakeSettings : IBotSettings
    {
        public BotPolicy? Policy { get; set; }
        public ulong AdminChannelId { get; set; }
        public List<ulong> SetChannels { get; } = [];
        public Dictionary<int, ulong> Ids { get; } = [];

        public Task SetAdminChannelAsync(ulong channelId)
        {
            SetChannels.Add(channelId);
            AdminChannelId = channelId;
            return Task.CompletedTask;
        }

        public ulong GetStatusMessageId(int instance) => Ids.GetValueOrDefault(instance);
        public void SetStatusMessageId(int instance, ulong messageId) => Ids[instance] = messageId;
    }

    private sealed class FakeChannel : IBotChannelClient
    {
        private ulong _next = 900000000000000000UL;
        public List<(ulong Channel, ulong Id, BotStatusMessage Message)> Posts { get; } = [];
        public List<(ulong Channel, ulong Id, BotStatusMessage Message)> Edits { get; } = [];
        public List<(ulong Channel, string Text)> Notes { get; } = [];
        public HashSet<(ulong Channel, ulong Id)> Existing { get; } = [];
        public bool ThrowOnPost { get; set; }
        public bool RefusePosts { get; set; }
        public int PostAttempts { get; private set; }

        public Task<ulong?> PostStatusAsync(ulong channelId, BotStatusMessage message)
        {
            PostAttempts++;
            if (RefusePosts) return Task.FromResult<ulong?>(null);
            if (ThrowOnPost) throw new InvalidOperationException("post failed");
            var id = ++_next;
            Posts.Add((channelId, id, message));
            Existing.Add((channelId, id));
            return Task.FromResult<ulong?>(id);
        }

        public bool FailEdits { get; set; }

        public Task<BotEditResult> EditStatusAsync(ulong channelId, ulong messageId, BotStatusMessage message)
        {
            if (FailEdits) return Task.FromResult(BotEditResult.Failed);
            if (!Existing.Contains((channelId, messageId))) return Task.FromResult(BotEditResult.Gone);
            Edits.Add((channelId, messageId, message));
            return Task.FromResult(BotEditResult.Edited);
        }

        public Task PostNoteAsync(ulong channelId, string text)
        {
            Notes.Add((channelId, text));
            return Task.CompletedTask;
        }
    }

    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    }

    private sealed class Rig
    {
        public FakeActions Actions { get; } = new()
        {
            Statuses =
            [
                new BotServerStatus(0, "Exiled Lands", BotServerState.Online, 12, 40, new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc), BotPendingAction.None, null, null),
                new BotServerStatus(1, "Siptah", BotServerState.Online, 3, 40, new DateTime(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc), BotPendingAction.Restart, null, null)
            ]
        };
        public FakeSettings Settings { get; } = new();
        public FakeChannel Client { get; } = new();
        public Clock Clock { get; } = new();
        public BotPendingActions Pending { get; } = new();
        public BotAuditLog Audit { get; }
        public BotInteractionHandler Handler { get; }
        public int AfterActions { get; private set; }
        private ulong _id;

        public Rig(string folder, DiscordBotConfig config, TimeSpan? actionTimeout = null)
        {
            Settings.Policy = PolicyOf(config);
            Settings.AdminChannelId = config.AllowedChannelIds.FirstOrDefault();
            Audit = new BotAuditLog(Path.Combine(folder, "audit-" + Guid.NewGuid().ToString("N")));
            Handler = new BotInteractionHandler(Actions, new BotGuard(new BotRateLimiter(), () => Clock.Now), Pending, Audit, Settings, Client, () => Clock.Now)
            {
                ActionTimeout = actionTimeout ?? TimeSpan.FromSeconds(5),
                AfterAction = _ =>
                {
                    AfterActions++;
                    return Task.CompletedTask;
                }
            };
        }

        public async Task<FakeResponder> Do(BotInteraction interaction, double advanceSeconds = 4)
        {
            Clock.Now = Clock.Now.AddSeconds(advanceSeconds);
            var responder = new FakeResponder();
            await Handler.HandleAsync(interaction, responder, CancellationToken.None);
            return responder;
        }

        public BotInteraction Slash(string path, ulong user = Operator, ulong[]? roles = null, ulong channel = Channel, ulong guild = Guild,
            bool human = true, Dictionary<string, long>? numbers = null, Dictionary<string, string>? texts = null) =>
            new(++_id, BotInteractionKind.Slash, user, "User" + user, human, guild, channel, roles ?? [Role], path, numbers, texts);

        public BotInteraction Button(string customId, ulong user = Operator, ulong[]? roles = null, ulong channel = Channel) =>
            new(++_id, BotInteractionKind.Button, user, "User" + user, true, Guild, channel, roles ?? [Role], customId);

        public BotInteraction Select(string customId, string value, ulong user = Operator, ulong[]? roles = null, ulong channel = Channel) =>
            new(++_id, BotInteractionKind.Select, user, "User" + user, true, Guild, channel, roles ?? [Role], customId, Values: [value]);

        public BotInteraction Click(BotQuestion question, bool confirm, ulong user = Operator, ulong channel = Channel) =>
            Button(confirm ? BotCustomId.ForConfirm(question.Nonce) : BotCustomId.ForCancel(question.Nonce), user, null, channel);

        public static Dictionary<string, long> N(long instance, long? minutes = null, long? delay = null)
        {
            var d = new Dictionary<string, long> { ["instance"] = instance };
            if (minutes is { } m) d["minutes"] = m;
            if (delay is { } dl) d["delay"] = dl;
            return d;
        }
    }

    private static DiscordBotConfig Config(Action<DiscordBotConfig>? change = null)
    {
        var config = new DiscordBotConfig { GuildId = Guild, SuperAdminUserId = SuperAdmin, AuditChannelId = AuditChannel };
        config.AddUser(Operator);
        config.AddRole(Role);
        config.SetAdminChannel(Channel);
        change?.Invoke(config);
        return config;
    }

    private static BotPolicy PolicyOf(DiscordBotConfig config)
    {
        var result = BotPolicy.Create(config);
        return result.Policy ?? throw new Exception("settings of the check are invalid: " + string.Join(", ", result.Issues.Select(x => x.Kind)));
    }

    private static DiscordBotConfig AllOn() => Config(c =>
    {
        foreach (var command in Enum.GetValues<BotCommand>()) c.SetEnabled(command, true);
    });

    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var folder = Path.Combine(Path.GetTempPath(), "TrebuchetConsoleChecks-handler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // --- refusals: the same neutral answer for every reason, nothing is done
            var rig = new Rig(folder, Config());
            var refusals = new List<FakeResponder>
            {
                await rig.Do(rig.Slash("status", Stranger)),
                await rig.Do(rig.Slash("status", Operator, roles: [OtherRole])),
                await rig.Do(rig.Slash("status", Operator, channel: OtherChannel)),
                await rig.Do(rig.Slash("status", Operator, guild: Guild + 1)),
                await rig.Do(rig.Slash("status", Stranger + 1, human: false)),
                await rig.Do(rig.Slash("stop", Operator, numbers: Rig.N(0))),
                await rig.Do(rig.Slash("update", SuperAdmin)),
                await rig.Do(rig.Slash("start", SuperAdmin, roles: [OtherRole], numbers: Rig.N(0)))
            };
            Check(refusals.All(r => r.Replies.Count == 1 && r.Last.Text == BotDecision.PublicMessage && r.Last.Embeds is null && r.Last.Question is null),
                "Every refusal gets the same neutral answer, whatever the reason");
            Check(rig.Actions.Calls.Count == 0 && rig.Pending.Count == 0, "A refused request does nothing and asks nothing");
            Check(rig.Audit.Recent.Count(x => x.Result == BotAuditResult.Denied) == refusals.Count
                  && rig.Audit.Recent.Any(x => x.Detail == "MissingRole") && rig.Audit.Recent.Any(x => x.Detail == "WrongChannel")
                  && rig.Audit.Recent.Any(x => x.Detail == "CommandDisabled"), "The exact reasons are in the audit log");

            // lock out after repeated refusals, then silence
            var locked = new Rig(folder, Config());
            for (var n = 0; n < 5; n++) await locked.Do(locked.Slash("status", Stranger));
            Check(locked.Audit.Recent.Any(x => x.Result == BotAuditResult.LockedOut) && locked.Client.Notes.Count == 1
                  && locked.Client.Notes[0].Channel == AuditChannel, "A lock out is logged and reported to the audit channel");
            var silent = await locked.Do(locked.Slash("status", Stranger));
            Check(silent.Replies.Count == 0, "A locked out user gets no answer");
            Check((await locked.Do(locked.Slash("status", Operator))).Replies.Count == 1, "Others are not affected");

            // no valid settings: nobody may do anything
            var noPolicy = new Rig(folder, Config());
            noPolicy.Settings.Policy = null;
            var none = await noPolicy.Do(noPolicy.Slash("status", SuperAdmin));
            Check(none.Last.Text == BotDecision.PublicMessage && noPolicy.Actions.Calls.Count == 0, "Without a valid policy nobody is allowed anything");

            // --- status
            rig = new Rig(folder, Config());
            var status = await rig.Do(rig.Slash("status"));
            Check(status.Replies.Count == 1 && !status.Replies[0].Update && status.Last.Embeds is { Count: 2 } && status.Last.Embeds[0].Title.Contains("Exiled Lands"),
                "Status shows every server as a private answer");
            Check((await rig.Do(rig.Slash("status", numbers: Rig.N(1)))).Last.Embeds is { Count: 1 } one && one[0].Title.Contains("Siptah"), "Status of one server");
            Check((await rig.Do(rig.Slash("status", numbers: Rig.N(7)))).Last.Text == BotMessages.German.UnknownServer, "An unknown server is said so");
            Check((await rig.Do(rig.Slash("nonsense"))).Last.Text == BotMessages.German.UnknownRequest
                  && (await rig.Do(rig.Slash("nonsense", Stranger))).Last.Text == BotDecision.PublicMessage, "Unknown commands: users get 'unknown', others the neutral answer");

            // --- postpone by command
            rig = new Rig(folder, Config());
            var postponed = await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 30),
                texts: new Dictionary<string, string> { ["reason"] = "Event {servername}\r\nnow" }));
            Check(rig.Actions.Done.SequenceEqual(["postpone 0 30 Discord: Event servername now"]), "Postpone calls the action with a cleaned reason");
            Check(postponed.Last.Text == "Verschoben um 30 Min." && rig.AfterActions == 1, "The answer tells how long, the status messages are refreshed");
            Check(rig.Audit.Recent.Any(x => x.Result == BotAuditResult.Executed && x.Command == BotCommand.Postpone && x.Parameters.Contains("minutes=30")), "Postponing is in the audit log");
            foreach (var minutes in new long[] { 0, 361, -5 })
            {
                var invalid = await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, minutes)));
                Check(invalid.Last.Text == BotMessages.German.InvalidInput, $"{minutes} minutes are refused");
            }
            Check((await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0)))).Last.Text == BotMessages.German.InvalidInput, "Minutes are required");
            Check((await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(9, 30)))).Last.Text == BotMessages.German.UnknownServer, "Postponing an unknown server is refused");
            Check(rig.Actions.Executed == 1, "Nothing was done for refused inputs");

            rig.Actions.Result = new BotActionResult(BotActionOutcome.NothingPending);
            Check((await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 30)))).Last.Text == BotMessages.German.NothingPending, "Nothing pending is said so");
            rig.Actions.Result = new BotActionResult(BotActionOutcome.Failed, "secret detail C:\\path");
            var failed = await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 30)));
            Check(failed.Last.Text == BotMessages.German.Failed && !failed.Last.Text.Contains("secret"), "A failure shows no details to the user");
            rig.Actions.Hook = () => throw new InvalidOperationException("boom");
            var thrown = await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 30)));
            Check(thrown.Last.Text == BotMessages.German.Failed && rig.Audit.Recent.Any(x => x.Result == BotAuditResult.Failed), "An action that throws is a neutral failure, logged");

            // --- postpone by button and menu
            rig = new Rig(folder, Config());
            var button = await rig.Do(rig.Button(BotCustomId.ForAction(BotCommand.Postpone, 1)));
            var select = button.Last.Question as BotSelectQuestion;
            Check(select is not null && !button.Replies[0].Update && rig.Pending.Count == 1
                  && select.Choices.Select(c => c.Value).SequenceEqual(["15", "30", "60", "120", "180", "360"])
                  && select.Choices.Select(c => c.Label).SequenceEqual(["15 Min", "30 Min", "1 Std", "2 Std", "3 Std", "6 Std"]),
                "The postpone button offers the minutes of the settings");
            var pickedBy = await rig.Do(rig.Select(BotCustomId.ForPostponeSelect(select!.Nonce), "60"));
            Check(rig.Actions.Done.SequenceEqual(["postpone 1 60 Discord"]) && pickedBy.Replies[0].Update && pickedBy.Last.Text == "Verschoben um 1 Std.",
                "Picking minutes postpones and replaces the question");
            Check((await rig.Do(rig.Select(BotCustomId.ForPostponeSelect(select.Nonce), "60"))).Last.Text == BotMessages.German.Expired && rig.Actions.Executed == 1,
                "A menu works once");

            select = (await rig.Do(rig.Button(BotCustomId.ForAction(BotCommand.Postpone, 1)))).Last.Question as BotSelectQuestion;
            Check((await rig.Do(rig.Select(BotCustomId.ForPostponeSelect(select!.Nonce), "61"))).Last.Text == BotMessages.German.InvalidInput && rig.Actions.Executed == 1,
                "Only the offered minutes can be picked");
            select = (await rig.Do(rig.Button(BotCustomId.ForAction(BotCommand.Postpone, 1)))).Last.Question as BotSelectQuestion;
            Check((await rig.Do(rig.Select(BotCustomId.ForPostponeSelect(select!.Nonce), "30", Stranger))).Last.Text == BotDecision.PublicMessage && rig.Actions.Executed == 1,
                "A stranger can not use the menu");
            Check((await rig.Do(rig.Select(BotCustomId.ForPostponeSelect(select.Nonce), "30", SuperAdmin))).Last.Text == BotMessages.German.Expired && rig.Actions.Executed == 1,
                "Another allowed user can not answer someone else's menu");
            select = (await rig.Do(rig.Button(BotCustomId.ForAction(BotCommand.Postpone, 1)))).Last.Question as BotSelectQuestion;
            Check((await rig.Do(rig.Select(BotCustomId.ForPostponeSelect(select!.Nonce), "30"), advanceSeconds: 200)).Last.Text == BotMessages.German.Expired && rig.Actions.Executed == 1,
                "A menu runs out after two minutes");
            Check((await rig.Do(rig.Button(BotCustomId.ForAction(BotCommand.Postpone, 1), Stranger))).Last.Text == BotDecision.PublicMessage, "A stranger can not click the postpone button");
            Check((await rig.Do(rig.Button("tb1:a:postpone:5"))).Last.Text == BotMessages.German.UnknownRequest, "A button of a server that does not exist is unknown");
            Check((await rig.Do(rig.Button("tb1:a:postpone:5", Stranger))).Last.Text == BotDecision.PublicMessage, "...and still neutral for strangers");
            var resume = await rig.Do(rig.Button(BotCustomId.ForAction(BotCommand.Resume, 0)));
            Check(rig.Actions.Done.Last() == "resume 0" && resume.Last.Text == BotMessages.German.Resumed, "The release button works at once");

            // --- critical actions: off by default, then with a confirmation
            rig = new Rig(folder, Config());
            Check((await rig.Do(rig.Button(BotCustomId.ForAction(BotCommand.Stop, 0)))).Last.Text == BotDecision.PublicMessage && rig.Pending.Count == 0,
                "A stop button of a switched off command is refused");

            rig = new Rig(folder, AllOn());
            var ask = await rig.Do(rig.Slash("stop", numbers: Rig.N(0)));
            var confirm = ask.Last.Question as BotConfirmQuestion;
            Check(confirm is not null && rig.Actions.Executed == 0 && ask.Last.Text.Contains("Server 0") && ask.Last.Text.Contains("5 Min"),
                "Stop asks first and names the server and the warning time");
            var done = await rig.Do(rig.Click(confirm!, true));
            Check(rig.Actions.Calls.Last() == "stop 0 5 Discord" && done.Replies[0].Update && done.Last.Text == "Stopp von Server 0 gestartet.",
                "The confirmation stops with the default warning time");
            Check(rig.Client.Notes.Any(x => x.Channel == AuditChannel && x.Text.Contains("Stop server 0")), "Critical actions are reported to the audit channel");
            var secondClick = await rig.Do(rig.Click(confirm, true));
            Check(secondClick.Last.Text == BotMessages.German.Expired && !secondClick.Replies[0].Update && rig.Actions.Executed == 1,
                "A confirmation works once, the second click is answered as a new message (it does not overwrite the result)");

            confirm = (await rig.Do(rig.Slash("stop", numbers: Rig.N(0)))).Last.Question as BotConfirmQuestion;
            var cancelled = await rig.Do(rig.Click(confirm!, false));
            Check(cancelled.Last.Text == BotMessages.German.Cancelled && cancelled.Replies[0].Update && rig.Actions.Executed == 1, "Cancel does nothing");
            Check((await rig.Do(rig.Click(confirm!, true))).Last.Text == BotMessages.German.Expired && rig.Actions.Executed == 1, "A cancelled question can not be confirmed");

            confirm = (await rig.Do(rig.Slash("stop", numbers: Rig.N(0)))).Last.Question as BotConfirmQuestion;
            Check((await rig.Do(rig.Click(confirm!, true, Stranger))).Last.Text == BotDecision.PublicMessage && rig.Actions.Executed == 1, "A stranger can not confirm");
            Check((await rig.Do(rig.Click(confirm!, true, SuperAdmin))).Last.Text == BotMessages.German.Expired && rig.Actions.Executed == 1, "Another user can not confirm someone else's question");
            Check((await rig.Do(rig.Click(confirm!, true), advanceSeconds: 40)).Last.Text == BotMessages.German.Expired && rig.Actions.Executed == 1, "A confirmation runs out after 30 seconds");

            // delay option and validation
            var now = (await rig.Do(rig.Slash("stop", numbers: Rig.N(1, delay: 0)))).Last;
            Check(now.Text.Contains("sofort"), "Delay 0 means right away");
            await rig.Do(rig.Click((BotConfirmQuestion)now.Question!, true));
            Check(rig.Actions.Calls.Last() == "stop 1 0 Discord", "A stop without warning time");
            Check((await rig.Do(rig.Slash("stop", numbers: Rig.N(1, delay: 61)))).Last.Text == BotMessages.German.InvalidInput
                  && (await rig.Do(rig.Slash("stop", numbers: Rig.N(1, delay: -1)))).Last.Text == BotMessages.German.InvalidInput, "A warning time over an hour is refused");
            Check((await rig.Do(rig.Slash("stop", numbers: Rig.N(5)))).Last.Text == BotMessages.German.UnknownServer, "Stopping an unknown server is refused");

            var restart = (await rig.Do(rig.Slash("restart now", numbers: Rig.N(1, delay: 10)))).Last;
            await rig.Do(rig.Click((BotConfirmQuestion)restart.Question!, true));
            Check(rig.Actions.Calls.Last() == "restart 1 10 Discord", "Restart with its warning time");
            var button2 = (await rig.Do(rig.Button(BotCustomId.ForAction(BotCommand.RestartNow, 0)))).Last;
            Check(button2.Question is BotConfirmQuestion, "The restart button asks as well");

            // start and update are for the super admin only, even when switched on
            Check((await rig.Do(rig.Slash("start", Operator, numbers: Rig.N(1)))).Last.Text == BotDecision.PublicMessage
                  && (await rig.Do(rig.Slash("update", Operator))).Last.Text == BotDecision.PublicMessage, "Start and update stay with the super admin");
            var start = (await rig.Do(rig.Slash("start", SuperAdmin, numbers: Rig.N(1)))).Last;
            await rig.Do(rig.Click((BotConfirmQuestion)start.Question!, true, SuperAdmin));
            Check(rig.Actions.Calls.Last() == "start 1", "The super admin starts a server after confirming");
            var update = (await rig.Do(rig.Slash("update", SuperAdmin))).Last;
            await rig.Do(rig.Click((BotConfirmQuestion)update.Question!, true, SuperAdmin));
            Check(rig.Actions.Calls.Last() == "update", "The super admin starts the update after confirming");

            // limit of critical actions per server
            rig = new Rig(folder, AllOn());
            for (var n = 0; n < 3; n++)
            {
                var q = (await rig.Do(rig.Slash("stop", numbers: Rig.N(0)))).Last.Question as BotConfirmQuestion;
                await rig.Do(rig.Click(q!, true));
            }
            Check(rig.Actions.Executed == 3, "Three stops are carried out");
            Check((await rig.Do(rig.Slash("stop", numbers: Rig.N(0)))).Last.Text == BotMessages.German.CriticalLimit && rig.Pending.Count == 0,
                "The fourth in ten minutes is refused before it is even asked");
            Check((await rig.Do(rig.Slash("stop", numbers: Rig.N(1)))).Last.Question is BotConfirmQuestion, "The limit is per server");

            rig = new Rig(folder, AllOn());
            for (var n = 0; n < 5; n++)
            {
                var q = (await rig.Do(rig.Slash("stop", numbers: Rig.N(0)))).Last.Question as BotConfirmQuestion;
                await rig.Do(rig.Click(q!, false));
            }
            Check((await rig.Do(rig.Slash("stop", numbers: Rig.N(0)))).Last.Question is BotConfirmQuestion, "Cancelling does not use up the limit of critical actions");

            // --- the admin channel command
            rig = new Rig(folder, Config());
            Check((await rig.Do(rig.Slash("bot channel set", Operator, channel: OtherChannel))).Last.Text == BotDecision.PublicMessage, "Only the super admin sets the channel");
            var channelAsk = (await rig.Do(rig.Slash("bot channel set", SuperAdmin, channel: OtherChannel))).Last;
            Check(channelAsk.Question is BotConfirmQuestion, "The super admin is asked to confirm, also from outside the admin channel");
            Check((await rig.Do(rig.Click((BotConfirmQuestion)channelAsk.Question!, true, SuperAdmin, channel: Channel))).Last.Text == BotDecision.PublicMessage
                  && rig.Settings.SetChannels.Count == 0, "A confirmation from another channel than the question does nothing");
            channelAsk = (await rig.Do(rig.Slash("bot channel set", SuperAdmin, channel: OtherChannel))).Last;
            var channelDone = await rig.Do(rig.Click((BotConfirmQuestion)channelAsk.Question!, true, SuperAdmin, channel: OtherChannel));
            Check(rig.Settings.SetChannels.SequenceEqual([OtherChannel]) && channelDone.Last.Text == BotMessages.German.DoneChannel
                  && rig.Client.Notes.Any(x => x.Text.Contains("Admin channel changed")), "The channel of the command becomes the admin channel, reported to the audit channel");
            Check(rig.Audit.Recent.Any(x => x.Command == BotCommand.SetChannel && x.Result == BotAuditResult.Executed), "...and is in the audit log");

            // --- the longest postponement: asking again and again can not put a restart off for ever
            rig = new Rig(folder, Config());
            Check((await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 360)))).Last.Text == "Verschoben um 6 Std.", "A postponement of the longest allowed time is granted");
            var shorter = await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 360)));
            Check(shorter.Last.Text.StartsWith("Verschoben um 5 Std 59 Min", StringComparison.Ordinal) && rig.Actions.Done.Last().StartsWith("postpone 0 359", StringComparison.Ordinal),
                "Asking again only reaches as far as the start of the chain plus the longest time");
            await rig.Do(rig.Slash("restart resume", numbers: Rig.N(0)));
            var used = await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 30)), advanceSeconds: 359 * 60 + 30);
            Check(used.Last.Text == BotMessages.German.PostponeLimit && !rig.Actions.Done.Last().StartsWith("postpone 0 30", StringComparison.Ordinal),
                "Releasing and postponing again does not give a new budget");
            rig.Actions.Statuses[0] = rig.Actions.Statuses[0] with { StartedUtc = new DateTime(2026, 10, 24, 1, 0, 0, DateTimeKind.Utc) };
            Check((await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 360)))).Last.Text == "Verschoben um 6 Std.", "After the restart a new chain starts");
            Check((await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(1, 360)))).Last.Text == "Verschoben um 6 Std.", "Every server has its own budget");

            // A pending stop moves back from its end: the time that is left counts against the chain too, or postponing a
            // stop again and again would add up for ever.
            rig = new Rig(folder, Config());
            var stopEnd = rig.Clock.Now.AddMinutes(30);
            rig.Actions.Statuses[0] = rig.Actions.Statuses[0] with { Pending = BotPendingAction.Stop, PendingAtUtc = stopEnd };
            var stopMoved = await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 360)));
            Check(rig.Actions.Done.Last().StartsWith("postpone 0 330", StringComparison.Ordinal) && stopMoved.Last.Text.StartsWith("Verschoben um 5 Std 30 Min", StringComparison.Ordinal),
                "The time left of a pending stop is taken from the minutes that may be postponed");
            rig.Actions.Statuses[0] = rig.Actions.Statuses[0] with { PendingAtUtc = stopEnd.AddMinutes(330) };
            var stopAgain = await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 30)));
            Check(stopAgain.Last.Text == BotMessages.German.PostponeLimit && rig.Actions.Done.Last().StartsWith("postpone 0 330", StringComparison.Ordinal),
                "A stop that already reaches the end of the chain can not be moved back again");

            // A postponement the launcher refused (busy) used none of the budget.
            rig = new Rig(folder, Config());
            rig.Actions.Result = new BotActionResult(BotActionOutcome.Busy);
            Check((await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 360)))).Last.Text == BotMessages.German.Busy, "A busy launcher refuses the postponement");
            rig.Actions.Result = new BotActionResult(BotActionOutcome.Done);
            Check((await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 360)))).Last.Text == "Verschoben um 6 Std.",
                "A refused postponement does not use up the budget");

            rig = new Rig(folder, Config());
            await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 60)));
            Check((await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 360)), advanceSeconds: 91 * 60)).Last.Text == "Verschoben um 6 Std.",
                "A chain that was idle for half an hour starts again");

            var budget = new BotPostponeBudget();
            var t = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
            var started = (DateTime?)t.AddHours(-1);
            Check(budget.Reserve(0, started, 90, TimeSpan.FromMinutes(100), t) == 90
                  && budget.Reserve(0, started, 90, TimeSpan.FromMinutes(100), t.AddMinutes(1)) == 90
                  && budget.Reserve(0, started, 90, TimeSpan.FromMinutes(100), t.AddMinutes(50)) == 50
                  && budget.Reserve(0, started, 30, TimeSpan.FromMinutes(100), t.AddSeconds(99.5 * 60)) == 0, "The budget grants less and less until the chain is used up");
            Check(budget.Reserve(0, started, 0, TimeSpan.FromMinutes(100), t) == 0 && budget.Reserve(0, started, -3, TimeSpan.FromMinutes(100), t) == 0, "No minutes, nothing granted");

            // --- cancelling or an expired question is no refusal, even from outside the admin channel
            rig = new Rig(folder, Config());
            var cancelledAll = true;
            for (var n = 0; n < 7; n++)
            {
                var channelQuestion = (await rig.Do(rig.Slash("bot channel set", SuperAdmin, channel: OtherChannel))).Last.Question as BotConfirmQuestion;
                var cancel = await rig.Do(rig.Click(channelQuestion!, false, SuperAdmin, channel: OtherChannel));
                cancelledAll &= cancel.Last.Text == BotMessages.German.Cancelled;
            }
            Check(cancelledAll && !rig.Audit.Recent.Any(x => x.Result == BotAuditResult.LockedOut), "Cancelling the channel command from the new channel never locks the super admin out");
            Check((await rig.Do(rig.Slash("status", SuperAdmin))).Last.Embeds is { Count: 2 }, "...and the super admin still works");
            var stale = true;
            for (var n = 0; n < 8; n++)
            {
                var expired = await rig.Do(rig.Button(BotCustomId.ForConfirm("AAAAAAAAAAAAAAAAAAAAAA"), Stranger));
                stale &= expired.Replies.Count == 1 && expired.Last.Text == BotDecision.PublicMessage;
            }
            Check(stale && !rig.Audit.Recent.Any(x => x.Result == BotAuditResult.LockedOut && x.UserId == Stranger), "Old buttons are refused neutrally and do not lock anyone out");

            // --- notes carry no free text names
            Check(locked.Client.Notes[0].Text.Contains("<@200000000000000003>") && !locked.Client.Notes[0].Text.Contains("(User"),
                "The audit channel note names the user by mention and ID, never by the free text name");

            // --- the minutes menu only works in the channel it was shown in
            rig = new Rig(folder, Config(c => c.AllowedChannelIds = []));
            var menu = (await rig.Do(rig.Button(BotCustomId.ForAction(BotCommand.Postpone, 1), channel: Channel))).Last.Question as BotSelectQuestion;
            var elsewhere = await rig.Do(rig.Select(BotCustomId.ForPostponeSelect(menu!.Nonce), "30", channel: OtherChannel));
            Check(elsewhere.Last.Text == BotMessages.German.InvalidInput && rig.Actions.Executed == 0, "A menu answered from another channel does nothing");

            // --- English
            rig = new Rig(folder, Config(c => c.Language = "en"));
            Check((await rig.Do(rig.Slash("status", Stranger))).Last.Text == "No permission."
                  && (await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 90)))).Last.Text == "Postponed by 1 h 30 min.", "The language of the settings is used");

            // --- slow actions, errors, duplicates
            rig = new Rig(folder, Config(), actionTimeout: TimeSpan.FromMilliseconds(150));
            var gate = new TaskCompletionSource<BotActionResult>();
            rig.Actions.Hook = () => gate.Task;
            var slow = await rig.Do(rig.Slash("restart postpone", numbers: Rig.N(0, 30)));
            Check(slow.Last.Text == BotMessages.German.Running && rig.Audit.Recent.Any(x => x.Detail == "running"), "A slow action is answered with 'running'");
            gate.SetResult(new BotActionResult(BotActionOutcome.Done));
            for (var wait = 0; wait < 50 && !rig.Audit.Recent.Any(x => x.Detail.StartsWith("finished", StringComparison.Ordinal)); wait++) await Task.Delay(20);
            Check(rig.Audit.Recent.Any(x => x.Detail.StartsWith("finished Done", StringComparison.Ordinal)), "...and its end is logged later");

            rig = new Rig(folder, Config());
            var duplicate = rig.Slash("status");
            await rig.Do(duplicate);
            var again = await rig.Do(duplicate);
            Check(again.Replies.Count == 0, "The same interaction is handled once");

            var broken = new FakeResponder { Throws = true };
            var threwNothing = true;
            try { await rig.Handler.HandleAsync(rig.Slash("status"), broken, CancellationToken.None); } catch { threwNothing = false; }
            Check(threwNothing, "A responder that fails does not throw out of the handler");
            rig.Actions.InstanceCount = 0;
            rig.Actions.Statuses = [];
            Check((await rig.Do(rig.Slash("status"))).Last.Text == BotMessages.German.NoServers, "No servers set up is said so");

            // --- the status board
            Check(await BoardChecksAsync(Check), "board");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        return count;
    }

    private static async Task<bool> BoardChecksAsync(Action<bool, string> check)
    {
        var folder = Path.GetTempPath();
        var clock = new Clock();
        var actions = new FakeActions
        {
            Statuses =
            [
                new BotServerStatus(0, "Exiled Lands", BotServerState.Online, 12, 40, clock.Now.AddHours(-4), BotPendingAction.None, null, null),
                new BotServerStatus(1, "Siptah", BotServerState.Stopped, 0, 40, null, BotPendingAction.None, null, null)
            ]
        };
        var settings = new FakeSettings { Policy = PolicyOf(Config()), AdminChannelId = Channel };
        var channel = new FakeChannel();
        var board = new BotStatusBoard(actions, channel, settings, () => clock.Now);

        await board.RefreshAsync(CancellationToken.None);
        check(channel.Posts.Count == 2 && channel.Posts.All(p => p.Channel == Channel) && settings.Ids.Count == 2, "The board posts one status message per server in the admin channel and remembers them");
        check(channel.Posts[0].Message.Title.Contains("Exiled Lands") && channel.Posts[0].Message.Color == BotStatusMessageBuilder.Green, "The message shows the state");
        await board.RefreshAsync(CancellationToken.None);
        check(channel.Posts.Count == 2 && channel.Edits.Count == 0, "Nothing is written when nothing changed");

        actions.Statuses[0] = actions.Statuses[0] with { Pending = BotPendingAction.Restart, PendingAtUtc = clock.Now.AddMinutes(10) };
        clock.Now = clock.Now.AddSeconds(2);
        await board.RefreshAsync(CancellationToken.None);
        check(channel.Edits.Count == 0, "A change right after a write waits a few seconds");
        clock.Now = clock.Now.AddSeconds(10);
        await board.RefreshAsync(CancellationToken.None);
        check(channel.Edits.Count == 1 && channel.Edits[0].Id == settings.Ids[0] && channel.Edits[0].Message.Buttons.Single().Label == "Neustart verschieben"
              && channel.Edits[0].Message.Color == BotStatusMessageBuilder.Yellow, "The message is edited, with the postpone button");
        await board.RefreshAsync(CancellationToken.None, force: true);
        check(channel.Edits.Count == 3, "A forced refresh writes every message again");

        channel.Existing.Remove((Channel, settings.Ids[1]));
        var oldId = settings.Ids[1];
        actions.Statuses[1] = actions.Statuses[1] with { State = BotServerState.Online, StartedUtc = clock.Now };
        clock.Now = clock.Now.AddSeconds(10);
        await board.RefreshAsync(CancellationToken.None);
        check(channel.Posts.Count == 3 && settings.Ids[1] != oldId, "A deleted message is posted again");

        board.Reset();
        clock.Now = clock.Now.AddSeconds(10);
        var editsBefore = channel.Edits.Count;
        await board.RefreshAsync(CancellationToken.None);
        check(channel.Posts.Count == 3 && channel.Edits.Count == editsBefore + 2, "After a reset the existing messages are edited, not posted twice");

        var enabledStop = PolicyOf(Config(c => { c.SetEnabled(BotCommand.Stop, true); c.SetEnabled(BotCommand.RestartNow, true); }));
        settings.Policy = enabledStop;
        clock.Now = clock.Now.AddSeconds(10);
        editsBefore = channel.Edits.Count;
        await board.RefreshAsync(CancellationToken.None);
        check(channel.Edits.Count > editsBefore && channel.Edits[^1].Message.Buttons.Any(b => b.Command == BotCommand.Stop), "Switching a command on changes the buttons");

        var postsBefore = channel.Posts.Count;
        editsBefore = channel.Edits.Count;
        settings.AdminChannelId = OtherChannel;
        clock.Now = clock.Now.AddSeconds(10);
        await board.RefreshAsync(CancellationToken.None);
        check(channel.Posts.Count == postsBefore + 2 && channel.Posts[^1].Channel == OtherChannel && channel.Edits.Count == editsBefore, "Another admin channel gets its own messages");

        var quiet = new FakeChannel();
        var quietBoard = new BotStatusBoard(actions, quiet, new FakeSettings { Policy = PolicyOf(Config()), AdminChannelId = 0 }, () => clock.Now);
        await quietBoard.RefreshAsync(CancellationToken.None);
        var invalidBoard = new BotStatusBoard(actions, quiet, new FakeSettings { Policy = null, AdminChannelId = Channel }, () => clock.Now);
        await invalidBoard.RefreshAsync(CancellationToken.None);
        check(quiet.Posts.Count == 0, "No admin channel or no valid policy: nothing is posted");

        // An edit that fails for another reason than "gone" does not post a duplicate and is tried again.
        var flaky = new FakeChannel();
        var flakySettings = new FakeSettings { Policy = PolicyOf(Config()), AdminChannelId = Channel };
        var flakyBoard = new BotStatusBoard(actions, flaky, flakySettings, () => clock.Now);
        await flakyBoard.RefreshAsync(CancellationToken.None);
        flaky.FailEdits = true;
        actions.Statuses[0] = actions.Statuses[0] with { Players = 20 };
        actions.Statuses[1] = actions.Statuses[1] with { Players = 5 };
        clock.Now = clock.Now.AddSeconds(10);
        await flakyBoard.RefreshAsync(CancellationToken.None);
        check(flaky.Posts.Count == 2 && flaky.Edits.Count == 0, "A failing edit does not post a duplicate message");
        flaky.FailEdits = false;
        clock.Now = clock.Now.AddSeconds(10);
        await flakyBoard.RefreshAsync(CancellationToken.None);
        check(flaky.Posts.Count == 2, "...and is tried again later");

        var failing = new FakeChannel { ThrowOnPost = true };
        var failingBoard = new BotStatusBoard(actions, failing, new FakeSettings { Policy = PolicyOf(Config()), AdminChannelId = Channel }, () => clock.Now);
        await failingBoard.RefreshAsync(CancellationToken.None);
        failing.ThrowOnPost = false;
        await failingBoard.RefreshAsync(CancellationToken.None);
        check(failing.Posts.Count == 0, "After a failure the board waits before it tries again");
        clock.Now = clock.Now.AddSeconds(40);
        await failingBoard.RefreshAsync(CancellationToken.None);
        check(failing.Posts.Count == 2, "A failing client does not throw and is retried after a pause");

        // A post that keeps failing is tried less and less often: a request now and then, not every refresh.
        var denied = new FakeChannel { RefusePosts = true };
        var deniedBoard = new BotStatusBoard(actions, denied, new FakeSettings { Policy = PolicyOf(Config()), AdminChannelId = Channel }, () => clock.Now);
        for (var n = 0; n < 12; n++)
        {
            clock.Now = clock.Now.AddSeconds(10);
            await deniedBoard.RefreshAsync(CancellationToken.None);
        }
        check(denied.PostAttempts is >= 2 and <= 8, $"A refused post is not retried every refresh ({denied.PostAttempts} attempts for 12 refreshes of 2 servers)");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await board.RefreshAsync(cancelled.Token, force: true);
        check(true, "A cancelled refresh returns quietly");
        _ = folder;
        return true;
    }
}
