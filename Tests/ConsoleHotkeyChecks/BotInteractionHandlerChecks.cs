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
        public event EventHandler? StatusChanged;
        public void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);
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
        public Task<BotActionResult> CheckUpdatesAsync(CancellationToken token) => Answer("checkupdates");
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
                await rig.Do(rig.Slash("update", Operator)),
                await rig.Do(rig.Slash("start", SuperAdmin, roles: [OtherRole], numbers: Rig.N(0)))
            };
            Check(refusals.All(r => r.Replies.Count == 1 && r.Last.Text == BotDecision.PublicMessage && r.Last.Embeds is null && r.Last.Question is null),
                "Every refusal gets the same neutral answer, whatever the reason");
            Check(rig.Actions.Calls.Count == 0 && rig.Pending.Count == 0, "A refused request does nothing and asks nothing");
            Check(rig.Audit.Recent.Count(x => x.Result == BotAuditResult.Denied) == refusals.Count
                  && rig.Audit.Recent.Any(x => x.Detail == "MissingRole") && rig.Audit.Recent.Any(x => x.Detail == "WrongChannel")
                  && rig.Audit.Recent.Any(x => x.Detail == "CommandDisabled"), "The exact reasons are in the audit log");

            // the super admin always has every right: the switches (all critical ones are off here) only limit the others
            var superRig = new Rig(folder, Config());
            foreach (var command in new[] { "stop", "restart now", "start", "update" })
            {
                var asked = await superRig.Do(superRig.Slash(command, SuperAdmin, numbers: command == "update" ? null : Rig.N(0)));
                Check(asked.Replies.Count == 1 && asked.Last.Text != BotDecision.PublicMessage && asked.Last.Question is not null,
                    $"The super admin may use '{command}' without switching it on, after the confirmation");
            }
            Check(superRig.Actions.Calls.Count == 0, "Nothing runs before the confirmation, not even for the super admin");
            var operatorTries = await superRig.Do(superRig.Slash("stop", Operator, numbers: Rig.N(0)));
            Check(operatorTries.Last.Text == BotDecision.PublicMessage && operatorTries.Last.Question is null,
                "The switches still limit everybody else");

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

            // --- the restart and stop buttons ask for the warning time first (a menu, like the dialog in Trebuchet), then confirm
            var menus = new Rig(folder, AllOn());
            var before = menus.Actions.Executed;
            var delayMenu = (await menus.Do(menus.Button(BotCustomId.ForAction(BotCommand.RestartNow, 0)))).Last;
            var delays = delayMenu.Question as BotSelectQuestion;
            Check(delays is not null && menus.Actions.Executed == before && delayMenu.Text.Contains("Server 0")
                  && delays.Choices.Select(c => c.Value).SequenceEqual(["0", "1", "5", "10", "15", "30", "60", "120"])
                  && delays.Choices[0].Label == "Sofort" && delays.Choices[1].Label == "1 Min" && delays.Choices[^2].Label == "1 Std" && delays.Choices[^1].Label == "2 Std",
                "The restart button offers the warning times of Trebuchet first and does nothing yet");
            var picked = await menus.Do(menus.Select(BotCustomId.ForPostponeSelect(delays!.Nonce), "10"));
            var pickedConfirm = picked.Last.Question as BotConfirmQuestion;
            Check(pickedConfirm is not null && picked.Replies[0].Update && picked.Last.Text.Contains("Server 0") && picked.Last.Text.Contains("10 Min")
                  && menus.Actions.Executed == before, "The chosen warning time comes back in the confirmation, nothing runs yet");
            await menus.Do(menus.Click(pickedConfirm!, true));
            Check(menus.Actions.Calls.Last() == "restart 0 10 Discord", "Confirming restarts with the chosen warning time");

            var stopMenu = (await menus.Do(menus.Button(BotCustomId.ForAction(BotCommand.Stop, 1)))).Last.Question as BotSelectQuestion;
            Check(stopMenu is not null && stopMenu.Choices.Select(c => c.Value).SequenceEqual(["0", "1", "5", "10", "15", "30", "60"]),
                "A stop is offered up to one hour");
            var immediate = (await menus.Do(menus.Select(BotCustomId.ForPostponeSelect(stopMenu!.Nonce), "0"))).Last;
            Check(immediate.Text.Contains("sofort") && immediate.Question is BotConfirmQuestion, "'Right away' is a choice too");
            await menus.Do(menus.Click((BotConfirmQuestion)immediate.Question!, true));
            Check(menus.Actions.Calls.Last() == "stop 1 0 Discord", "Stopping right away passes no warning time");

            stopMenu = (await menus.Do(menus.Button(BotCustomId.ForAction(BotCommand.Stop, 0)))).Last.Question as BotSelectQuestion;
            before = menus.Actions.Executed;
            foreach (var wrong in new[] { "120", "7", "-1", "", "1e1", "0 ", "٣" })
            {
                var refused = await menus.Do(menus.Select(BotCustomId.ForPostponeSelect(stopMenu!.Nonce), wrong));
                Check(refused.Last.Text == BotMessages.German.InvalidInput && refused.Replies[0].Update && menus.Actions.Executed == before,
                    $"The warning time '{wrong}' is not one of the offered ones");
                stopMenu = (await menus.Do(menus.Button(BotCustomId.ForAction(BotCommand.Stop, 0)))).Last.Question as BotSelectQuestion;
            }
            Check((await menus.Do(menus.Select(BotCustomId.ForPostponeSelect(stopMenu!.Nonce), "5", Stranger))).Last.Text == BotDecision.PublicMessage,
                "A stranger can not use the menu of warning times");
            Check((await menus.Do(menus.Select(BotCustomId.ForPostponeSelect(stopMenu.Nonce), "5", SuperAdmin))).Last.Text == BotMessages.German.Expired,
                "Another user can not answer someone else's menu of warning times");
            stopMenu = (await menus.Do(menus.Button(BotCustomId.ForAction(BotCommand.Stop, 0)))).Last.Question as BotSelectQuestion;
            Check((await menus.Do(menus.Select(BotCustomId.ForPostponeSelect(stopMenu!.Nonce), "5"), advanceSeconds: 200)).Last.Text == BotMessages.German.Expired
                  && menus.Actions.Executed == before, "The menu of warning times runs out after two minutes");

            // The menu does not use up the limit of critical actions, the confirmation does.
            var limited = new Rig(folder, AllOn());
            for (var n = 0; n < 5; n++)
            {
                var offered = (await limited.Do(limited.Button(BotCustomId.ForAction(BotCommand.Stop, 0)))).Last.Question as BotSelectQuestion;
                Check(offered is not null, "The menu is offered again and again as long as nothing is confirmed");
                Check((await limited.Do(limited.Select(BotCustomId.ForPostponeSelect(offered!.Nonce), "5"))).Last.Question is BotConfirmQuestion,
                    "The menu answers with the confirmation while nothing is used up");
            }
            for (var n = 0; n < 3; n++)
            {
                var offered = (await limited.Do(limited.Button(BotCustomId.ForAction(BotCommand.Stop, 0)))).Last.Question as BotSelectQuestion;
                var question = (await limited.Do(limited.Select(BotCustomId.ForPostponeSelect(offered!.Nonce), "1"))).Last.Question as BotConfirmQuestion;
                await limited.Do(limited.Click(question!, true));
            }
            Check(limited.Actions.Executed == 3, "Three confirmed stops are carried out, each counted once (not for the menu and the confirmation)");
            Check((await limited.Do(limited.Button(BotCustomId.ForAction(BotCommand.Stop, 0)))).Last.Text == BotMessages.German.CriticalLimit,
                "The fourth is refused before the menu is shown");

            // A user who may not stop gets no menu at all.
            var noMenu = new Rig(folder, Config());
            Check((await noMenu.Do(noMenu.Button(BotCustomId.ForAction(BotCommand.Stop, 0)))).Last.Text == BotDecision.PublicMessage && noMenu.Pending.Count == 0,
                "Without the right to stop there is no menu of warning times");
            var superMenu = (await noMenu.Do(noMenu.Button(BotCustomId.ForAction(BotCommand.Stop, 0), SuperAdmin))).Last.Question as BotSelectQuestion;
            Check(superMenu is not null, "The super admin gets the menu without any switch");
            // A menu is only checked as the command it belongs to: the one of a stop does not become a restart.
            var forged = await noMenu.Do(noMenu.Select(BotCustomId.ForPostponeSelect(superMenu!.Nonce), "120", SuperAdmin));
            Check(forged.Last.Text == BotMessages.German.InvalidInput && noMenu.Actions.Executed == 0, "The menu of a stop does not accept the two hours of a restart");

            // --- the check for updates: scan only, no confirmation, no installation
            var checking = new Rig(folder, Config());
            var found = await checking.Do(checking.Slash("updatecheck"));
            Check(checking.Actions.Calls.Last() == "checkupdates" && found.Last.Question is null
                  && found.Last.Text == "Update-Prüfung fertig. Mod-Updates: 0. Server-Version: aktuell. Es wurde nichts installiert.",
                "The check for updates runs at once and says that nothing was installed");
            checking.Actions.Result = new BotActionResult(BotActionOutcome.Done, string.Empty, new BotUpdateInfo(2, true));
            Check((await checking.Do(checking.Button(BotCustomId.ForAction(BotCommand.CheckUpdates, 1)))).Last.Text
                  == "Update-Prüfung fertig. Mod-Updates: 2. Server-Version: neue Version verfügbar. Es wurde nichts installiert.",
                "The button of the status message does the same and reports the mods and the server build");
            checking.Actions.Result = new BotActionResult(BotActionOutcome.Busy);
            Check((await checking.Do(checking.Slash("updatecheck"))).Last.Text == BotMessages.German.Busy, "A check while Steam is busy says so");
            checking.Actions.Result = new BotActionResult(BotActionOutcome.Failed, "Steam did not answer");
            Check((await checking.Do(checking.Slash("updatecheck"))).Last.Text == BotMessages.German.Failed, "A check that failed says so");
            Check(checking.Actions.Done.All(x => x == "checkupdates") && checking.Actions.Done.Count == 4, "Only the check ran, never an update");
            Check((await checking.Do(checking.Slash("updatecheck", Stranger))).Last.Text == BotDecision.PublicMessage, "A stranger can not check for updates");
            var checkOff = new Rig(folder, Config(c => c.SetEnabled(BotCommand.CheckUpdates, false)));
            Check((await checkOff.Do(checkOff.Slash("updatecheck"))).Last.Text == BotDecision.PublicMessage && checkOff.Actions.Executed == 0,
                "The check can be switched off for the other users");
            Check((await checkOff.Do(checkOff.Slash("updatecheck", SuperAdmin))).Last.Text.StartsWith("Update-Prüfung fertig", StringComparison.Ordinal),
                "...but the super admin always can");

            // --- only the servers chosen in the settings, under the names chosen there
            var chosen = new Rig(folder, Config(c =>
            {
                foreach (var command in Enum.GetValues<BotCommand>()) c.SetEnabled(command, true);
                c.ManagedInstances = [1];
                c.InstanceNames[1] = "Haupt <@123> [Server]";
                c.InstanceNames[0] = "Nicht verwaltet";
            }));
            var chosenStatus = await chosen.Do(chosen.Slash("status"));
            Check(chosenStatus.Last.Embeds is { Count: 1 } shown && shown[0].Title == "Haupt 123 Server · Instanz 1",
                "The status shows only the managed server, with its name cleaned of mentions and links");
            Check((await chosen.Do(chosen.Slash("status", numbers: Rig.N(0)))).Last.Text == BotMessages.German.UnknownServer, "A server that is not managed does not exist for the bot");
            foreach (var path in new[] { "stop", "restart now", "start", "restart postpone", "restart resume" })
                Check((await chosen.Do(chosen.Slash(path, SuperAdmin, numbers: Rig.N(0, path == "restart postpone" ? 30 : null)))).Last.Text == BotMessages.German.UnknownServer,
                    $"'{path}' for a server that is not managed is refused, also for the super admin");
            Check((await chosen.Do(chosen.Button(BotCustomId.ForAction(BotCommand.Stop, 0)))).Last.Text == BotMessages.German.UnknownServer
                  && (await chosen.Do(chosen.Button(BotCustomId.ForAction(BotCommand.Start, 0), SuperAdmin))).Last.Text == BotMessages.German.UnknownServer,
                "The buttons of an old message of such a server do nothing");
            Check(chosen.Actions.Executed == 0, "Nothing reached the launcher");
            var named = (await chosen.Do(chosen.Slash("stop", numbers: Rig.N(1)))).Last;
            Check(named.Text.Contains("Server Haupt 123 Server stoppen"), "The questions use the chosen name");
            var namedDone = await chosen.Do(chosen.Click((BotConfirmQuestion)named.Question!, true));
            Check(chosen.Actions.Calls.Last() == "stop 1 5 Discord" && namedDone.Last.Text == "Stopp von Server Haupt 123 Server gestartet.", "...and so do the answers");
            var namedMenu = (await chosen.Do(chosen.Button(BotCustomId.ForAction(BotCommand.RestartNow, 1)))).Last;
            Check(namedMenu.Text.Contains("Haupt 123 Server"), "...and the menu of warning times");
            var unnamed = new Rig(folder, Config(c => c.ManagedInstances = [0, 1]));
            Check((await unnamed.Do(unnamed.Slash("status"))).Last.Embeds!.Select(e => e.Title).SequenceEqual(["Exiled Lands · Instanz 0", "Siptah · Instanz 1"]),
                "Without a name the save is shown, every chosen server is listed");
            var allOfThem = new Rig(folder, Config());
            Check((await allOfThem.Do(allOfThem.Slash("status"))).Last.Embeds!.Count == 2, "Not chosen yet means every server");
            var noneOfThem = new Rig(folder, Config(c => c.ManagedInstances = []));
            Check((await noneOfThem.Do(noneOfThem.Slash("status"))).Last.Text == BotMessages.German.NoServers, "An empty choice means no server");

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
        check(channel.Edits.Count == 1 && channel.Edits[0].Id == settings.Ids[0] && channel.Edits[0].Message.Buttons.Any(b => b.Label == "Neustart verschieben")
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

        // The buttons always fit the state, whatever the switches say: switching a command on or off writes nothing.
        check(channel.Edits[^1].Message.Buttons.Any(b => b.Command == BotCommand.Stop), "The buttons of the message show stop without any switch");
        var enabledStop = PolicyOf(Config(c => { c.SetEnabled(BotCommand.Stop, true); c.SetEnabled(BotCommand.RestartNow, true); }));
        settings.Policy = enabledStop;
        clock.Now = clock.Now.AddSeconds(10);
        editsBefore = channel.Edits.Count;
        await board.RefreshAsync(CancellationToken.None);
        check(channel.Edits.Count == editsBefore, "Switching a command on or off does not change the buttons");

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

        // --- the regular check: the message is edited at least every five minutes, a calm server is looked at every five
        // minutes and a changing one more often
        var beat = new Clock();
        var beatActions = new FakeActions
        {
            Statuses =
            [
                new BotServerStatus(0, "Exiled Lands", BotServerState.Online, 12, 40, beat.Now.AddHours(-4), BotPendingAction.None, null, null),
                new BotServerStatus(1, "Siptah", BotServerState.Online, 3, 40, beat.Now.AddHours(-2), BotPendingAction.None, null, null)
            ]
        };
        var beatSettings = new FakeSettings { Policy = PolicyOf(Config()), AdminChannelId = Channel };
        var beatChannel = new FakeChannel();
        var beatBoard = new BotStatusBoard(beatActions, beatChannel, beatSettings, () => beat.Now);
        var firstWait = await beatBoard.RefreshAsync(CancellationToken.None);
        check(beatChannel.Posts.Count == 2 && firstWait == TimeSpan.FromMinutes(5) && beatChannel.Posts[0].Message.UpdatedUtc == beat.Now,
            "A calm server is checked again in five minutes, the message carries the time of the check");
        beat.Now = beat.Now.AddMinutes(2);
        var secondWait = await beatBoard.RefreshAsync(CancellationToken.None);
        check(beatChannel.Edits.Count == 0 && secondWait == TimeSpan.FromMinutes(3), "Nothing is written before the five minutes are over, and the next check is due when they are");
        beat.Now = beat.Now.AddMinutes(3);
        await beatBoard.RefreshAsync(CancellationToken.None);
        check(beatChannel.Edits.Count == 2 && beatChannel.Edits.All(e => e.Message.UpdatedUtc == beat.Now),
            "After five minutes both messages are edited although nothing changed, so the time shows that the check runs");
        beatChannel.Existing.Remove((Channel, beatSettings.Ids[0]));
        beat.Now = beat.Now.AddMinutes(5);
        await beatBoard.RefreshAsync(CancellationToken.None);
        check(beatChannel.Posts.Count == 3 && beatChannel.Edits.Count == 3, "A message deleted by someone is noticed by the regular check and posted again");

        // A server that starts or stops is read more often, a change is written as soon as the pause between edits is over.
        beatActions.Statuses[0] = beatActions.Statuses[0] with { State = BotServerState.Stopping };
        beat.Now = beat.Now.AddSeconds(30);
        var stoppingWait = await beatBoard.RefreshAsync(CancellationToken.None);
        check(stoppingWait == TimeSpan.FromSeconds(15) && beatChannel.Edits[^1].Message.Buttons[0] is { Command: BotCommand.Start, Disabled: true },
            "While a server is stopping the next look is due in 15 seconds and the buttons wait greyed out");
        beatActions.Statuses[0] = beatActions.Statuses[0] with { State = BotServerState.Stopped, StartedUtc = null, Players = 0 };
        beat.Now = beat.Now.AddSeconds(2);
        var tooSoon = await beatBoard.RefreshAsync(CancellationToken.None);
        var editsNow = beatChannel.Edits.Count;
        check(tooSoon == TimeSpan.FromSeconds(3), "A change right after an edit is due as soon as the pause between edits is over");
        beat.Now = beat.Now.AddSeconds(3);
        await beatBoard.RefreshAsync(CancellationToken.None);
        check(beatChannel.Edits.Count == editsNow + 1 && beatChannel.Edits[^1].Message.Buttons[0] is { Command: BotCommand.Start, Disabled: false },
            "Then the stopped server shows its start button");
        beatChannel.FailEdits = true;
        beatActions.Statuses[1] = beatActions.Statuses[1] with { Players = 9 };
        beat.Now = beat.Now.AddMinutes(1);
        var failedWait = await beatBoard.RefreshAsync(CancellationToken.None);
        check(failedWait <= TimeSpan.FromMinutes(10) && failedWait >= TimeSpan.FromSeconds(1), "A failing edit is looked at again later, not never");
        beatActions.Statuses = [];
        check(await beatBoard.RefreshAsync(CancellationToken.None, force: true) == TimeSpan.FromSeconds(15), "No answer from the launcher is asked again soon");

        // --- only the managed servers, under their names; a server taken out gets a note instead of its buttons
        var picked = new Clock();
        var pickedActions = new FakeActions
        {
            Statuses =
            [
                new BotServerStatus(0, "Exiled Lands", BotServerState.Online, 12, 40, picked.Now.AddHours(-4), BotPendingAction.None, null, null),
                new BotServerStatus(1, "Siptah", BotServerState.Online, 3, 40, picked.Now.AddHours(-2), BotPendingAction.None, null, null)
            ]
        };
        var pickedSettings = new FakeSettings { Policy = PolicyOf(Config()), AdminChannelId = Channel };
        var pickedChannel = new FakeChannel();
        var pickedBoard = new BotStatusBoard(pickedActions, pickedChannel, pickedSettings, () => picked.Now);
        await pickedBoard.RefreshAsync(CancellationToken.None);
        var secondId = pickedSettings.Ids[1];
        pickedSettings.Policy = PolicyOf(Config(c =>
        {
            c.ManagedInstances = [0];
            c.InstanceNames[0] = "Haupt-Server";
        }));
        picked.Now = picked.Now.AddSeconds(10);
        await pickedBoard.RefreshAsync(CancellationToken.None, force: true);
        check(pickedChannel.Edits.Any(e => e.Id == pickedSettings.Ids[0] && e.Message.Title == "Haupt-Server · Instanz 0")
              && pickedChannel.Edits.Any(e => e.Id == secondId && e.Message.Buttons.Count == 0 && e.Message.Title == BotTexts.German.NotManagedTitle)
              && pickedSettings.Ids[1] == 0 && pickedChannel.Posts.Count == 2,
            "The managed server shows its name, the one that was taken out is replaced by a note without buttons and not posted again");
        var editsAfterRetire = pickedChannel.Edits.Count;
        picked.Now = picked.Now.AddSeconds(10);
        await pickedBoard.RefreshAsync(CancellationToken.None, force: true);
        check(pickedChannel.Edits.Count == editsAfterRetire + 1 && pickedChannel.Posts.Count == 2, "The note is written once");
        pickedSettings.Policy = PolicyOf(Config(c => c.ManagedInstances = [0, 1]));
        picked.Now = picked.Now.AddSeconds(10);
        await pickedBoard.RefreshAsync(CancellationToken.None);
        check(pickedChannel.Posts.Count == 3 && pickedSettings.Ids[1] != 0 && pickedSettings.Ids[1] != secondId,
            "A server that is chosen again gets a new message");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await board.RefreshAsync(cancelled.Token, force: true);
        check(true, "A cancelled refresh returns quietly");
        _ = folder;
        return true;
    }
}
