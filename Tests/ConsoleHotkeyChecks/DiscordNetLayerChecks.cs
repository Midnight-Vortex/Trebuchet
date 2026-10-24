using System.Net;
using Discord;
using Discord.Net;
using Microsoft.Extensions.Logging;
using TrebuchetLib.DiscordBot;

/// <summary>
/// Checks of the Discord.Net layer of the bot without a connection to Discord: the commands registered for the server,
/// embeds and buttons, how interactions become requests (roles and IDs only), where answers go, which channels may be
/// written to, the state of the connection, the security check, and that the token reaches no log.
/// </summary>
static class DiscordNetLayerChecks
{
    private const ulong Guild = 100000000000000011UL;
    private const ulong OtherGuild = 100000000000000012UL;
    private const ulong SuperAdmin = 200000000000000011UL;
    private const ulong Operator = 200000000000000012UL;
    private const ulong Role = 300000000000000011UL;
    private const ulong OtherRole = 300000000000000012UL;
    private const ulong Channel = 400000000000000011UL;
    private const ulong OtherChannel = 400000000000000012UL;
    // Made up. Put together at run time so secret scanners do not take the source for a real token.
    // A different token in every run: the lock of a token is a machine wide name (parallel runs must not meet).
    private static readonly string SampleToken = string.Join(".", "OTg3NjU0MzIxMDk4NzY1NDMy", "HxyzAB", "zyxwvutsrqponmlkjihgfedcba9876543210ZYXW" + Guid.NewGuid().ToString("N")[..8]);
    // Never registered with the redactor: only its form gives it away.
    private static readonly string UnseenToken = string.Join(".", "NTU1NTU1NTU1NTU1NTU1NTU1", "QrStUv", "0123456789abcdefghijklmnopqrstuvwxyzABCD" + Guid.NewGuid().ToString("N")[..8]);

    #region Fakes

    private sealed class ReversingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Reverse().Select(x => (byte)(x ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => data.Select(x => (byte)(x ^ 0x5A)).Reverse().ToArray();
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        public string All => string.Join("\n", Lines);

        public void Add(string line)
        {
            lock (_lines) _lines.Add(line);
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        // An exception handed to the logger would be written whole: capture it the same way.
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Add($"{logLevel}: {formatter(state, exception)}" + (exception is null ? string.Empty : " | " + exception));
    }

    private sealed class FakeActions : IBotActions
    {
        public int InstanceCount => 2;
        private EventHandler? _handlers;
        public int Subscribers { get; private set; }
        public event EventHandler? StatusChanged
        {
            add { _handlers += value; Subscribers++; }
            remove { _handlers -= value; Subscribers--; }
        }
        public void Raise() => _handlers?.Invoke(this, EventArgs.Empty);
        public Task<IReadOnlyList<BotServerStatus>> GetStatusesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<BotServerStatus>>([]);
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

    private sealed class FakeInteractionTransport : IBotInteractionTransport
    {
        public List<(string Kind, BotOutgoingMessage? Message)> Calls { get; } = [];
        public Exception? Failure { get; set; }

        public Task ModifyOriginalAsync(BotOutgoingMessage message) => Record("modify", message);
        public Task FollowupAsync(BotOutgoingMessage message) => Record("followup", message);
        public Task DeleteOriginalAsync() => Record("delete", null);
        public Task RespondModalAsync(BotReasonModal modal) => Record("modal:" + modal.Title, null);
        public Task RespondUpdateAsync(BotOutgoingMessage message) => Record("respond-update", message);
        public Task RespondNewAsync(BotOutgoingMessage message) => Record("respond-new", message);
        public Task DeferUpdateAsync() => Record("defer", null);

        private Task Record(string kind, BotOutgoingMessage? message)
        {
            Calls.Add((kind, message));
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class FakeChannelTransport : IBotChannelTransport
    {
        public Dictionary<ulong, ulong> Channels { get; } = [];
        public List<(string Kind, ulong Channel, ulong Message, BotOutgoingMessage Content)> Calls { get; } = [];
        public bool MessageExists { get; set; } = true;
        public Exception? Failure { get; set; }
        public bool LookupFails { get; set; }
        private ulong _next = 900000000000000000UL;

        public ulong? GuildOf(ulong channelId) => LookupFails
            ? throw new InvalidOperationException("lookup broken")
            : Channels.TryGetValue(channelId, out var guild) ? guild : null;

        public Task<ulong> SendAsync(ulong channelId, BotOutgoingMessage message)
        {
            Calls.Add(("send", channelId, 0, message));
            return Failure is null ? Task.FromResult(++_next) : Task.FromException<ulong>(Failure);
        }

        public Task<bool> ModifyAsync(ulong channelId, ulong messageId, BotOutgoingMessage message)
        {
            Calls.Add(("modify", channelId, messageId, message));
            return Failure is null ? Task.FromResult(MessageExists) : Task.FromException<bool>(Failure);
        }
    }

    #endregion

    private static DiscordBotConfig ValidConfig(string? path = null)
    {
        var config = path is null ? new DiscordBotConfig() : DiscordBotConfig.CreateConfig(path);
        config.GuildId = Guild;
        config.SuperAdminUserId = SuperAdmin;
        config.AddUser(Operator);
        config.AddRole(Role);
        return config;
    }

    private static BotPolicy PolicyOf(DiscordBotConfig config) =>
        BotPolicy.Create(config).Policy ?? throw new Exception("The settings of the check are not valid");

    private static bool NoMentions(BotOutgoingMessage message) =>
        message.AllowedMentions is { AllowedTypes: AllowedMentionTypes.None, MentionRepliedUser: false } mentions
        && mentions.UserIds.Count == 0 && mentions.RoleIds.Count == 0 && !ReferenceEquals(mentions, AllowedMentions.None);

    private static List<List<IMessageComponent>> RowsOf(MessageComponent components) =>
        components.Components.Select(row => ((ActionRowComponent)row).Components.ToList()).ToList();

    private static List<ButtonComponent> ButtonsOf(MessageComponent components) =>
        RowsOf(components).SelectMany(x => x).OfType<ButtonComponent>().ToList();

    private static BotRawUser Human(ulong id = Operator, params ulong[] roles) => new(id, "Op", false, false, roles);

    private static BotStatusMessage Status(params BotButton[] buttons) =>
        new("Exiled Lands · Instanz 0", BotStatusMessageBuilder.Green, [new BotField("Zustand", "🟢 Online")], buttons, "Aktualisiert");

    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var t0 = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var logs = new CapturingLoggerFactory();
        var logger = logs.CreateLogger("checks");

        // --- the gateway connection asks for nothing privileged
        var socketConfig = DiscordBotService.CreateClientConfig();
        Check(socketConfig.GatewayIntents == GatewayIntents.Guilds, "Only the Guilds intent");
        Check((socketConfig.GatewayIntents & (GatewayIntents.GuildMembers | GatewayIntents.GuildPresences | GatewayIntents.MessageContent)) == 0,
            "No privileged intent: members, presences, message content");
        Check(!socketConfig.AlwaysDownloadUsers && socketConfig.MessageCacheSize == 0, "No member download, no message cache");
        Check(!socketConfig.UseInteractionSnowflakeDate, "The three seconds of an interaction count from its arrival");
        Check(DiscordBotService.ConnectDeadline == TimeSpan.FromSeconds(60) && DiscordBotService.RefreshInterval == TimeSpan.FromSeconds(10),
            "60 s to connect, status every 10 s");

        Check(DiscordBotService.ClassifyLoginFailure(new HttpException(HttpStatusCode.Unauthorized, null)) == DiscordBotProblem.LoginFailed
              && DiscordBotService.ClassifyLoginFailure(new ArgumentException("bad form")) == DiscordBotProblem.LoginFailed, "A refused token is a failed login");
        Check(DiscordBotService.ClassifyLoginFailure(new HttpRequestException("no network")) == DiscordBotProblem.ConnectionLost
              && DiscordBotService.ClassifyLoginFailure(new TimeoutException()) == DiscordBotProblem.ConnectionLost
              && DiscordBotService.ClassifyLoginFailure(new HttpException(HttpStatusCode.BadGateway, null)) == DiscordBotProblem.ConnectionLost,
            "No network is a lost connection");
        Check(DiscordBotService.ClassifyLoginFailure(new InvalidOperationException()) == DiscordBotProblem.Unknown, "Anything else is unknown");

        // --- slash commands: one server, never in direct messages, paths become sub commands
        BotSlashOption Instance(bool required = true) => new("instance", "Server-Instanz", BotOptionType.Integer, required, 0, 9);
        BotSlashCommand[] commands =
        [
            new("status", "Zustand der Server", [Instance(false)]),
            new("restart postpone", "Neustart verschieben", [Instance(), new("minutes", "Minuten", BotOptionType.Integer, true, 1, 1440),
                new("reason", "Grund", BotOptionType.String, false)]),
            new("restart resume", "Neustarts wieder freigeben", [Instance()]),
            new("restart now", "Neustart jetzt", [new("delay", "Verzögerung", BotOptionType.Integer, false, 0, 60), Instance()]),
            new("stop", "Server stoppen", [Instance(), new("delay", "Verzögerung", BotOptionType.Integer, false, 0, 60)]),
            new("start", "Server starten", [Instance()]),
            new("update", "Updates installieren", []),
            new("bot channel set", "Diesen Kanal zum Admin-Kanal machen", [])
        ];
        var builders = DiscordNetCommandRegistrar.BuildCommands(commands);
        Check(builders.Select(x => x.Name).SequenceEqual(["status", "restart", "stop", "start", "update", "bot"]), "One top level command per first word");
#pragma warning disable CS0618 // The flag is obsolete in favour of the context types, both are set.
        Check(builders.All(x => x.ContextTypes.SetEquals([InteractionContextType.Guild]) && x.IntegrationTypes.SetEquals([ApplicationIntegrationType.GuildInstall])
                               && !x.IsDMEnabled && x.DefaultMemberPermissions is null && !x.IsNsfw),
            "Every command only on the server, not in direct messages, not user installed");
        var built = builders.Select(x => x.Build()).ToList();
        Check(built.All(x => x.ContextTypes.Value.SetEquals([InteractionContextType.Guild]) && x.IsDMEnabled.Value == false),
            "The built commands keep server only");
#pragma warning restore CS0618
        var flagged = DiscordNetCommandRegistrar.BuildProperties(commands, withContexts: true);
        var plain = DiscordNetCommandRegistrar.BuildProperties(commands, withContexts: false);
        Check(flagged.All(x => x.ContextTypes.IsSpecified && x.IntegrationTypes.IsSpecified && x.IsDMEnabled is { IsSpecified: true, Value: false }),
            "Registered with the server only flags first");
        Check(plain.Length == flagged.Length && plain.All(x => !x.ContextTypes.IsSpecified && !x.IntegrationTypes.IsSpecified && !x.IsDMEnabled.IsSpecified)
              && plain.Select(x => x.Name.Value).SequenceEqual(flagged.Select(x => x.Name.Value)), "The fallback is the same commands without the flags");
        Check(builders.All(x => x.Description.Length is > 0 and <= 100), "Every command has a description Discord accepts");

        var restart = builders.Single(x => x.Name == "restart");
        Check(restart.Options.Select(x => (x.Name, x.Type)).SequenceEqual([("postpone", ApplicationCommandOptionType.SubCommand),
            ("resume", ApplicationCommandOptionType.SubCommand), ("now", ApplicationCommandOptionType.SubCommand)]), "restart has three sub commands");
        var postpone = restart.Options.Single(x => x.Name == "postpone").Options;
        Check(postpone.Single(x => x.Name == "instance") is { Type: ApplicationCommandOptionType.Integer, IsRequired: true, MinValue: 0, MaxValue: 9 },
            "The instance is a whole number with its range");
        Check(postpone.Single(x => x.Name == "minutes") is { Type: ApplicationCommandOptionType.Integer, IsRequired: true, MinValue: 1, MaxValue: 1440 },
            "The minutes are a whole number with their range");
        Check(postpone.Single(x => x.Name == "reason") is { Type: ApplicationCommandOptionType.String, IsRequired: false, MaxLength: BotInputRules.MaxReasonLength },
            "The reason is a short optional text");
        var restartNow = built.Single(x => x.Name.Value == "restart").Options.Value.Single(x => x.Name == "now").Options;
        Check(restartNow.Select(x => x.Name).SequenceEqual(["instance", "delay"]), "Required options come before optional ones");
        var botCommand = builders.Single(x => x.Name == "bot");
        Check(botCommand.Options.Single() is { Name: "channel", Type: ApplicationCommandOptionType.SubCommandGroup } group
              && group.Options.Single() is { Name: "set", Type: ApplicationCommandOptionType.SubCommand } set && set.Options.Count == 0,
            "bot channel set is a group with a sub command");
        Check(builders.Single(x => x.Name == "status").Options.Single() is { Name: "instance", IsRequired: false }, "status takes an optional instance");
        Check((builders.Single(x => x.Name == "update").Options ?? []).Count == 0, "update takes nothing");

        foreach (var bad in new[]
                 {
                     new[] { new BotSlashCommand("Status", "x", []) },
                     [new BotSlashCommand("a b c d", "x", [])],
                     [new BotSlashCommand("restart", "x", []), new BotSlashCommand("restart now", "x", [])],
                     [new BotSlashCommand("restart now", "x", []), new BotSlashCommand("restart", "x", [])],
                     [new BotSlashCommand("stop", "x", []), new BotSlashCommand("stop", "y", [])],
                     [new BotSlashCommand("stop", "x", [new BotSlashOption("Instance", "x", BotOptionType.Integer, true)])],
                     [new BotSlashCommand("stop  now", "x", [])]
                 })
        {
            var refused = false;
            try { DiscordNetCommandRegistrar.BuildCommands(bad); } catch (ArgumentException) { refused = true; }
            Check(refused, $"'{string.Join(" | ", bad.Select(x => x.Path))}' is refused");
        }

        // The real list of the handler, once it exists.
        IReadOnlyList<BotSlashCommand>? real = null;
        try { real = BotInteractionHandler.SlashCommands; } catch (NotImplementedException) { }
        if (real is not null)
        {
            var realBuilders = DiscordNetCommandRegistrar.BuildCommands(real);
            Check(realBuilders.Count > 0 && realBuilders.All(x => x.Build() is not null), "The commands of the handler map to valid Discord commands");
            Check(realBuilders.Any(x => x.Name == "updatecheck" && x.Build().Options.IsSpecified == false || x.Name == "updatecheck" && x.Build().Options.Value.Count == 0)
                  && real.Any(x => x.Path == "update") && real.Single(x => x.Path == "restart now").Options.Single(o => o.Name == "delay").Max == 120
                  && real.Single(x => x.Path == "stop").Options.Single(o => o.Name == "delay").Max == 60,
                "The check for updates has its own command, the delay of a restart goes up to two hours and that of a stop to one");
        }

        // --- embeds and buttons
        foreach (var color in new[] { BotStatusMessageBuilder.Green, BotStatusMessageBuilder.Yellow, BotStatusMessageBuilder.Blue, BotStatusMessageBuilder.Red })
            Check(BotDiscordRenderer.ToEmbed(Status() with { Color = color }).Color!.Value.RawValue == color, $"Color {color:X6} is kept");
        Check(BotDiscordRenderer.StyleOf(BotButtonStyle.Success) == ButtonStyle.Success && BotDiscordRenderer.StyleOf(BotButtonStyle.Danger) == ButtonStyle.Danger
              && BotDiscordRenderer.StyleOf(BotButtonStyle.Primary) == ButtonStyle.Primary && BotDiscordRenderer.StyleOf(BotButtonStyle.Secondary) == ButtonStyle.Secondary,
            "Green, red, blue and grey buttons");
        Check(Enum.GetValues<BotButtonStyle>().All(x => BotDiscordRenderer.StyleOf(x) is not ButtonStyle.Link and not ButtonStyle.Premium), "Never a link button");

        var allOn = ValidConfig();
        foreach (var command in Enum.GetValues<BotCommand>()) allOn.SetEnabled(command, true);
        var allOnPolicy = PolicyOf(allOn);
        var server = new BotServerStatus(1, "Exiled Lands", BotServerState.Online, 12, 40, t0.AddHours(-3), BotPendingAction.Restart, t0.AddMinutes(10), t0.AddHours(1));
        var model = BotStatusMessageBuilder.Build(server, BotTexts.German, t0);
        var status = BotDiscordRenderer.RenderStatus(model);
        var embed = status.Embeds.Single();
        Check(embed.Title == model.Title && embed.Color!.Value.RawValue == model.Color && embed.Footer!.Value.Text == model.Footer,
            "Title, color and footer of the status message");
        Check(embed.Fields.Select(f => (f.Name, f.Value, f.Inline)).SequenceEqual(model.Fields.Select(f => (f.Name, f.Value, f.Inline))), "All fields as built");
        var statusButtons = ButtonsOf(status.Components);
        Check(statusButtons.Select(b => (b.CustomId, b.Label, b.Style)).SequenceEqual(
                model.Buttons.Select(b => (b.CustomId, b.Label, BotDiscordRenderer.StyleOf(b.Style)))), "The buttons of the status message, in order");
        Check(statusButtons.All(b => BotCustomId.TryParse(b.CustomId, 2, out var kind, out _, out var instance, out _) && kind == BotCustomIdKind.Action && instance == 1),
            "Every button reads back to its server");
        Check(status.Text.Length == 0 && NoMentions(status), "The status message has no text and no mentions");
        Check(embed.Timestamp == new DateTimeOffset(t0, TimeSpan.Zero), "The embed carries the time of the check");
        var down = BotStatusMessageBuilder.Build(server with { State = BotServerState.Stopping }, BotTexts.German, t0);
        var downButtons = ButtonsOf(BotDiscordRenderer.RenderStatus(down).Components);
        Check(downButtons.Count == 2 && downButtons[0].IsDisabled && downButtons[0].CustomId == BotCustomId.ForAction(BotCommand.Start, 1) && !downButtons[1].IsDisabled
              && statusButtons.All(b => !b.IsDisabled), "A button that waits for the server to go down is greyed out, the others can be clicked");
        Check(statusButtons.Any(b => b.CustomId == BotCustomId.ForAction(BotCommand.CheckUpdates, 1)) && statusButtons.Count <= 5,
            "The update check is a button of the status message, and the row still fits");

        var many = Status(Enumerable.Range(0, 12).Select(i => new BotButton(BotCustomId.ForAction(BotCommand.Postpone, i), "P" + i, BotButtonStyle.Secondary, BotCommand.Postpone)).ToArray());
        var manyRows = RowsOf(BotDiscordRenderer.RenderStatus(many).Components);
        Check(manyRows.Select(x => x.Count).SequenceEqual([5, 5, 2]), "At most five buttons per row");
        var tooMany = Status(Enumerable.Range(0, 40).Select(i => new BotButton(BotCustomId.ForAction(BotCommand.Stop, i), "S" + i, BotButtonStyle.Danger, BotCommand.Stop)).ToArray());
        var tooManyRows = RowsOf(BotDiscordRenderer.RenderStatus(tooMany).Components);
        Check(tooManyRows.Count == BotDiscordRenderer.MaxRows && tooManyRows.All(x => x.Count <= 5), "At most five rows");
        var forged = Status(new BotButton("javascript:alert(1)", "x", BotButtonStyle.Primary, BotCommand.Stop),
            new BotButton(BotCustomId.ForConfirm(new string('a', 22)), "x", BotButtonStyle.Primary, BotCommand.Stop),
            new BotButton(BotCustomId.ForAction(BotCommand.Stop, 0), "ok", BotButtonStyle.Danger, BotCommand.Stop));
        Check(ButtonsOf(BotDiscordRenderer.RenderStatus(forged).Components).Select(b => b.CustomId).SequenceEqual([BotCustomId.ForAction(BotCommand.Stop, 0)]),
            "Only action IDs of the bot become buttons of a status message");
        var oversized = Status() with { Title = new string('T', 400), Fields = [new BotField("", " "), new BotField(new string('n', 300), new string('v', 2000))] };
        var oversizedEmbed = BotDiscordRenderer.ToEmbed(oversized);
        Check(oversizedEmbed.Title.Length == 256 && oversizedEmbed.Fields[0].Name == "-" && oversizedEmbed.Fields[0].Value == "-"
              && oversizedEmbed.Fields[1].Name.Length == 256 && oversizedEmbed.Fields[1].Value.Length == 1024, "Texts are cut to the limits of Discord, empty ones filled");

        var pending = new BotPendingActions();
        var nonce = pending.Create(Operator, BotCommand.Stop, 0, t0);
        var confirm = BotDiscordRenderer.Render(new BotReply("Server 0 stoppen?", Question: new BotConfirmQuestion(nonce, "Bestätigen", "Abbrechen")));
        var confirmButtons = ButtonsOf(confirm.Components);
        Check(confirmButtons.Count == 2 && confirmButtons[0] is { Style: ButtonStyle.Danger, Label: "Bestätigen" } && confirmButtons[1] is { Style: ButtonStyle.Secondary, Label: "Abbrechen" },
            "Confirm is red, cancel grey");
        Check(BotCustomId.TryParse(confirmButtons[0].CustomId, 1, out var okKind, out _, out _, out var okNonce) && okKind == BotCustomIdKind.Confirm && okNonce == nonce
              && BotCustomId.TryParse(confirmButtons[1].CustomId, 1, out var noKind, out _, out _, out var noNonce) && noKind == BotCustomIdKind.Cancel && noNonce == nonce,
            "The question buttons carry the code of the question");
        Check(confirm.Text == "Server 0 stoppen?" && confirm.Embeds.Length == 0 && NoMentions(confirm), "Question text without mentions");

        var select = BotDiscordRenderer.Render(new BotReply("Wie lange?", Question: new BotSelectQuestion(nonce, "Minuten wählen",
            [new BotChoice("15 Min", "15"), new BotChoice("1 Std", "60"), new BotChoice("doppelt", "60"), new BotChoice("leer", "")])));
        var menu = RowsOf(select.Components).Single().OfType<SelectMenuComponent>().Single();
        Check(BotCustomId.TryParse(menu.CustomId, 1, out var menuKind, out _, out _, out var menuNonce) && menuKind == BotCustomIdKind.PostponeSelect && menuNonce == nonce,
            "The menu carries the code of the question");
        Check(menu.Options.Select(o => (o.Label, o.Value)).SequenceEqual([("15 Min", "15"), ("1 Std", "60")]) && menu.MinValues == 1 && menu.MaxValues == 1
              && menu.Placeholder == "Minuten wählen", "One pick among the offered minutes, no doubles, no empty values");
        Check(RowsOf(BotDiscordRenderer.Render(new BotReply("x", Question: new BotConfirmQuestion("bad nonce!", "a", "b"))).Components).Count == 0,
            "A question with a broken code gets no buttons");

        var second = BotStatusMessageBuilder.Build(server with { Instance = 0 }, BotTexts.German, t0);
        var both = BotDiscordRenderer.Render(new BotReply("Status", [second, model]));
        var bothRows = RowsOf(both.Components);
        Check(both.Embeds.Length == 2 && bothRows.Count == 2 && bothRows[0].OfType<ButtonComponent>().All(b => b.Label.StartsWith("#0 "))
              && bothRows[1].OfType<ButtonComponent>().All(b => b.Label.StartsWith("#1 ")), "Several servers: one row each, every button says its server");
        var crowded = BotDiscordRenderer.Render(new BotReply("x", [tooMany], new BotConfirmQuestion(nonce, "Ja", "Nein")));
        var crowdedRows = RowsOf(crowded.Components);
        Check(crowdedRows.Count == BotDiscordRenderer.MaxRows && crowdedRows[^1].OfType<ButtonComponent>().First().CustomId == BotCustomId.ForConfirm(nonce),
            "The question always keeps its row");
        Check(BotDiscordRenderer.Render(new BotReply(new string('x', 2500))).Text.Length == BotDiscordRenderer.MaxTextLength, "Text is cut to 2000 characters");
        Check(BotDiscordRenderer.Render(new BotReply("")).Text.Length > 0, "An empty answer still is a valid message");
        Check(BotDiscordRenderer.Render(new BotReply("x", Enumerable.Repeat(Status(), 12).ToList())).Embeds.Length == BotDiscordRenderer.MaxEmbeds, "At most ten embeds");
        var ping = BotDiscordRenderer.Render(new BotReply("@everyone @here <@&300000000000000011>"));
        Check(NoMentions(ping) && NoMentions(BotDiscordRenderer.RenderNote("@everyone")) && !ReferenceEquals(BotDiscordRenderer.NoMentions(), BotDiscordRenderer.NoMentions()),
            "Every message is sent without mentions, with its own setting");

        // --- interactions: only what Discord delivered, roles as in the interaction
        var sourceRoles = new[] { Guild, Role, Role, 0UL, OtherRole };
        var slash = BotInteractionMapper.FromSlash(11, Human(Operator, sourceRoles), Guild, Channel, "restart",
        [
            new BotRawOption("postpone", BotRawOptionKind.SubCommand, null,
            [
                new BotRawOption("instance", BotRawOptionKind.Integer, 1L, []),
                new BotRawOption("minutes", BotRawOptionKind.Integer, 30, []),
                new BotRawOption("reason", BotRawOptionKind.String, "Event", []),
                new BotRawOption("ignored", BotRawOptionKind.Integer, "5", []),
                new BotRawOption("other", BotRawOptionKind.Other, true, [])
            ])
        ]);
        sourceRoles[1] = 1;
        Check(slash is { InteractionId: 11, Kind: BotInteractionKind.Slash, UserId: Operator, IsHuman: true, GuildId: Guild, ChannelId: Channel, Name: "restart postpone" },
            "A sub command becomes its path");
        Check(slash.Numbers!.Count == 2 && slash.Numbers["instance"] == 1 && slash.Numbers["minutes"] == 30 && slash.Texts!["reason"] == "Event" && slash.Texts.Count == 1,
            "Numbers and texts by name, nothing else");
        Check(slash.RoleIds.Order().SequenceEqual(new[] { Role, OtherRole }.Order()), "Roles without @everyone, zeros and doubles, copied when delivered");
        var channelSet = BotInteractionMapper.FromSlash(12, Human(SuperAdmin, Role), Guild, Channel, "bot",
            [new BotRawOption("channel", BotRawOptionKind.SubCommandGroup, null, [new BotRawOption("set", BotRawOptionKind.SubCommand, null, [])])]);
        Check(channelSet.Name == "bot channel set" && channelSet.Numbers!.Count == 0, "A group and its sub command become the path");
        var deep = BotInteractionMapper.FromSlash(13, Human(), Guild, Channel, "a", [new BotRawOption("b", BotRawOptionKind.SubCommandGroup, null,
            [new BotRawOption("c", BotRawOptionKind.SubCommand, null, [new BotRawOption("d", BotRawOptionKind.SubCommand, null, [])])])]);
        Check(deep.Name == "a b c", "At most three words");
        Check(BotInteractionMapper.FromSlash(14, Human(Operator, Role) with { IsBot = true }, Guild, Channel, "status", null).IsHuman == false
              && BotInteractionMapper.FromSlash(15, Human(Operator, Role) with { IsWebhook = true }, Guild, Channel, "status", null).IsHuman == false,
            "Bots and webhooks are no humans");
        Check(BotInteractionMapper.FromSlash(16, Human(Operator, Role) with { RoleIds = null }, Guild, Channel, "status", null).RoleIds.Count == 0,
            "No member delivered: no roles");
        var named = BotInteractionMapper.FromSlash(17, Human(Operator, Role) with { DisplayName = "Eve\n2026-10-23 | ALLOWED" }, Guild, Channel, "status", null);
        Check(!named.UserName.Contains('\n') && named.UserId == Operator, "The name is one line for the log, the ID decides");
        var direct = BotInteractionMapper.FromSlash(18, Human(Operator, Role), null, Channel, "status", null);
        Check(direct.GuildId is null && BotAuthorizer.Authorize(PolicyOf(ValidConfig()), new BotRequest(direct.UserId, direct.GuildId, direct.ChannelId, direct.RoleIds,
            direct.IsHuman, BotCommand.Status)).Reason == BotDenyReason.NotInGuild, "A direct message stays a direct message and is refused");
        var everyoneOnly = BotInteractionMapper.FromSlash(19, Human(Operator, Guild), Guild, Channel, "status", null);
        Check(BotAuthorizer.Authorize(PolicyOf(ValidConfig()), new BotRequest(everyoneOnly.UserId, everyoneOnly.GuildId, everyoneOnly.ChannelId, everyoneOnly.RoleIds,
            everyoneOnly.IsHuman, BotCommand.Status)).Reason == BotDenyReason.MissingRole, "@everyone alone is no role");
        var memberWithRole = BotInteractionMapper.FromSlash(20, Human(Operator, Guild, Role), Guild, Channel, "status", null);
        Check(BotAuthorizer.Authorize(PolicyOf(ValidConfig()), new BotRequest(memberWithRole.UserId, memberWithRole.GuildId, memberWithRole.ChannelId,
            memberWithRole.RoleIds, memberWithRole.IsHuman, BotCommand.Status)).Allowed, "A listed member with the role passes");

        var click = BotInteractionMapper.FromComponent(21, BotInteractionKind.Button, Human(Operator, Role), Guild, Channel, BotCustomId.ForAction(BotCommand.Postpone, 1), ["x"]);
        Check(click is { Kind: BotInteractionKind.Button, Name: "tb1:a:postpone:1", Values: null, Numbers: null }, "A click is its button ID");
        var values = new List<string> { "30" };
        values.AddRange(Enumerable.Repeat("1", 40));
        var pick = BotInteractionMapper.FromComponent(22, BotInteractionKind.Select, Human(Operator, Role), Guild, Channel, BotCustomId.ForPostponeSelect(nonce), values);
        values[0] = "999";
        Check(pick.Name == BotCustomId.ForPostponeSelect(nonce) && pick.Values!.Count == 25 && pick.Values[0] == "30", "A pick: menu ID and a copy of the values");

        // --- which acknowledgment: never write into a public message
        var actionId = BotCustomId.ForAction(BotCommand.Postpone, 0);
        Check(BotInteractionMapper.ChooseAck(BotInteractionKind.Slash, null, true) == BotAckKind.NewPrivateMessage, "A command answers in a new private message");
        Check(BotInteractionMapper.ChooseAck(BotInteractionKind.Button, actionId, false) == BotAckKind.NewPrivateMessage
              && BotInteractionMapper.ChooseAck(BotInteractionKind.Button, actionId, true) == BotAckKind.NewPrivateMessage, "A status button answers in a new private message");
        Check(BotInteractionMapper.ChooseAck(BotInteractionKind.Button, BotCustomId.ForConfirm(nonce), true) == BotAckKind.UpdateQuestion
              && BotInteractionMapper.ChooseAck(BotInteractionKind.Button, BotCustomId.ForCancel(nonce), true) == BotAckKind.UpdateQuestion
              && BotInteractionMapper.ChooseAck(BotInteractionKind.Modal, BotCustomId.ForReasonModal(nonce), true) == BotAckKind.UpdateQuestion,
            "Our private question is replaced by the answer (also after the window for the reason)");
        Check(BotInteractionMapper.ChooseAck(BotInteractionKind.Select, BotCustomId.ForPostponeSelect(nonce), true) == BotAckKind.Lazy
              && BotInteractionMapper.ChooseAck(BotInteractionKind.Select, BotCustomId.ForPostponeSelect(nonce), false) == BotAckKind.NewPrivateMessage
              && BotInteractionMapper.ChooseAck(BotInteractionKind.Modal, BotCustomId.ForReasonModal(nonce), false) == BotAckKind.NewPrivateMessage
              && BotInteractionMapper.ChooseAck(BotInteractionKind.Modal, BotCustomId.ForConfirm(nonce), true) == BotAckKind.UpdateQuestion
              && BotInteractionMapper.ChooseAck(BotInteractionKind.Modal, "nonsense", true) == BotAckKind.NewPrivateMessage,
            "A menu choice is not acknowledged up front (the window for the reason has to be the first answer), and only on our private question");
        var modalInteraction = BotInteractionMapper.FromModal(33, Human(Operator, Role), Guild, Channel, BotCustomId.ForReasonModal(nonce),
            new Dictionary<string, string> { ["reason"] = new string('r', 900), ["a"] = "1", ["b"] = "2", ["c"] = "3", ["d"] = "4", ["e"] = "5" });
        Check(modalInteraction.Kind == BotInteractionKind.Modal && modalInteraction.Name == BotCustomId.ForReasonModal(nonce) && modalInteraction.Texts!.Count == 5
              && modalInteraction.Texts["reason"].Length == 500 && modalInteraction.RoleIds!.SequenceEqual([Role]),
            "A submitted window: its ID, at most five fields, each cut");
        Check(BotInteractionMapper.ChooseAck(BotInteractionKind.Button, BotCustomId.ForConfirm(nonce), false) == BotAckKind.NewPrivateMessage
              && BotInteractionMapper.ChooseAck(BotInteractionKind.Select, BotCustomId.ForPostponeSelect(nonce), false) == BotAckKind.NewPrivateMessage,
            "A question on a public message is never edited");
        Check(BotInteractionMapper.ChooseAck(BotInteractionKind.Button, "nonsense", true) == BotAckKind.NewPrivateMessage
              && BotInteractionMapper.ChooseAck(BotInteractionKind.Button, null, true) == BotAckKind.NewPrivateMessage, "Unknown IDs never edit anything");

        // --- where answers go
        Check(DiscordNetResponder.TargetOf(BotAckKind.NewPrivateMessage, false, false) == BotReplyTarget.Original
              && DiscordNetResponder.TargetOf(BotAckKind.NewPrivateMessage, true, false) == BotReplyTarget.Original
              && DiscordNetResponder.TargetOf(BotAckKind.NewPrivateMessage, false, true) == BotReplyTarget.Followup
              && DiscordNetResponder.TargetOf(BotAckKind.NewPrivateMessage, true, true) == BotReplyTarget.Followup
              && DiscordNetResponder.TargetOf(BotAckKind.UpdateQuestion, true, false) == BotReplyTarget.Original
              && DiscordNetResponder.TargetOf(BotAckKind.UpdateQuestion, true, true) == BotReplyTarget.Original
              && DiscordNetResponder.TargetOf(BotAckKind.UpdateQuestion, false, false) == BotReplyTarget.Followup,
            "Answer targets");
        var fresh = new FakeInteractionTransport();
        var freshResponder = new DiscordNetResponder(fresh, BotAckKind.NewPrivateMessage, logger);
        await freshResponder.ReplyAsync(new BotReply("eins", Question: new BotSelectQuestion(nonce, "Minuten", [new BotChoice("15", "15")])));
        await freshResponder.ReplyAsync(new BotReply("zwei"));
        await freshResponder.UpdateAsync(new BotReply("drei"));
        await freshResponder.FinishAsync();
        Check(fresh.Calls.Select(x => x.Kind).SequenceEqual(["modify", "followup", "followup"]), "The first answer fills the thinking message, later ones are new");
        Check(fresh.Calls.All(x => x.Message is not null && NoMentions(x.Message)), "No answer mentions anyone");
        var silent = new FakeInteractionTransport();
        await new DiscordNetResponder(silent, BotAckKind.NewPrivateMessage, logger).FinishAsync();
        Check(silent.Calls.Select(x => x.Kind).SequenceEqual(["delete"]), "No answer at all removes the thinking message");
        var update = new FakeInteractionTransport();
        var updateResponder = new DiscordNetResponder(update, BotAckKind.UpdateQuestion, logger);
        await updateResponder.UpdateAsync(new BotReply("Erledigt"));
        await updateResponder.ReplyAsync(new BotReply("Noch etwas"));
        await updateResponder.FinishAsync();
        Check(update.Calls.Select(x => x.Kind).SequenceEqual(["modify", "followup"]), "An update replaces the question, a reply is new, nothing is deleted");
        Check(update.Calls[0].Message!.Components.Components.Count == 0 && update.Calls[0].Message!.Text == "Erledigt", "The old buttons of the question go away");
        // A menu choice answered with the window for the reason: that is the first answer and the acknowledgment.
        var lazyWindow = new FakeInteractionTransport();
        var lazyWindowResponder = new DiscordNetResponder(lazyWindow, BotAckKind.Lazy, logger);
        var shown = await lazyWindowResponder.ShowModalAsync(new BotReasonModal(nonce, "Stopp (5 Min) – Grund", "Grund", "z. B.", 100));
        await lazyWindowResponder.FinishAsync();
        Check(shown && lazyWindow.Calls.Select(x => x.Kind).SequenceEqual(["modal:Stopp (5 Min) – Grund"]), "The window is the first answer, nothing else is sent");
        var lazyTwice = new DiscordNetResponder(new FakeInteractionTransport(), BotAckKind.Lazy, logger);
        await lazyTwice.AcknowledgeAsync();
        Check(!await lazyTwice.ShowModalAsync(new BotReasonModal(nonce, "t", "l", "p", 100)), "After the acknowledgment no window is possible");
        var lazyAnswer = new FakeInteractionTransport();
        var lazyAnswerResponder = new DiscordNetResponder(lazyAnswer, BotAckKind.Lazy, logger);
        await lazyAnswerResponder.UpdateAsync(new BotReply("Ungültig"));
        await lazyAnswerResponder.ReplyAsync(new BotReply("Noch etwas"));
        await lazyAnswerResponder.FinishAsync();
        Check(lazyAnswer.Calls.Select(x => x.Kind).SequenceEqual(["respond-update", "followup"]), "Without a window the first answer is the acknowledgment (it replaces the menu), later ones are new");
        var lazyReply = new FakeInteractionTransport();
        var lazyReplyResponder = new DiscordNetResponder(lazyReply, BotAckKind.Lazy, logger);
        await lazyReplyResponder.ReplyAsync(new BotReply("Neu"));
        await lazyReplyResponder.UpdateAsync(new BotReply("Ersetzt"));
        Check(lazyReply.Calls.Select(x => x.Kind).SequenceEqual(["respond-new", "modify"]) && lazyReply.Calls.All(x => x.Message is null || NoMentions(x.Message)),
            "A first reply is a new private message, no answer mentions anyone");
        var lazySilent = new FakeInteractionTransport();
        await new DiscordNetResponder(lazySilent, BotAckKind.Lazy, logger).FinishAsync();
        Check(lazySilent.Calls.Select(x => x.Kind).SequenceEqual(["defer"]), "A menu choice nobody answers is still acknowledged");
        var lazyWatchdog = new FakeInteractionTransport();
        var lazyWatchdogResponder = new DiscordNetResponder(lazyWatchdog, BotAckKind.Lazy, logger);
        await lazyWatchdogResponder.AcknowledgeAsync();
        await lazyWatchdogResponder.UpdateAsync(new BotReply("Spät"));
        await lazyWatchdogResponder.FinishAsync();
        Check(lazyWatchdog.Calls.Select(x => x.Kind).SequenceEqual(["defer", "modify"]), "Acknowledged because it took long: the answer then replaces the menu");
        var windowFails = new FakeInteractionTransport { Failure = new InvalidOperationException("Unknown interaction") };
        Check(!await new DiscordNetResponder(windowFails, BotAckKind.Lazy, logger).ShowModalAsync(new BotReasonModal(nonce, "t", "l", "p", 100)),
            "A window that Discord refuses is reported as not shown");
        var notAMenu = new DiscordNetResponder(new FakeInteractionTransport(), BotAckKind.NewPrivateMessage, logger);
        Check(!await notAMenu.ShowModalAsync(new BotReasonModal(nonce, "t", "l", "p", 100)), "Only a menu choice can open the window");
        var modalLimits = BotDiscordRenderer.RenderModal(new BotReasonModal(nonce, new string('T', 90), new string('L', 90), new string('P', 300), 5000));
        Check(modalLimits.Title.Length <= 45 && modalLimits.CustomId == BotCustomId.ForReasonModal(nonce) && modalLimits.CustomId.Length <= BotCustomId.MaxLength,
            "The window stays inside the limits of Discord");
        var broken = new FakeInteractionTransport { Failure = new InvalidOperationException("Unknown interaction, token " + SampleToken) };
        var brokenResponder = new DiscordNetResponder(broken, BotAckKind.NewPrivateMessage, logger);
        await brokenResponder.ReplyAsync(new BotReply("x"));
        await brokenResponder.FinishAsync();
        Check(broken.Calls.Count == 2 && !logs.All.Contains(SampleToken) && logs.All.Contains(BotLogRedactor.Replacement),
            "A failed answer throws nothing and logs no token");

        // --- channels: only the server of the bot
        var transport = new FakeChannelTransport();
        transport.Channels[Channel] = Guild;
        transport.Channels[OtherChannel] = OtherGuild;
        var guildOfBot = Guild;
        var channelClient = new DiscordNetChannelClient(transport, () => guildOfBot, logger);
        var posted = await channelClient.PostStatusAsync(Channel, model);
        var sent = transport.Calls.Single();
        Check(posted is not null && sent.Kind == "send" && sent.Channel == Channel && sent.Content.Embeds.Length == 1 && NoMentions(sent.Content)
              && RowsOf(sent.Content.Components).All(x => x.Count <= 5), "A status message goes to the given channel of the server");
        Check(await channelClient.PostStatusAsync(OtherChannel, model) is null && await channelClient.PostStatusAsync(OtherChannel + 7, model) is null
              && await channelClient.PostStatusAsync(0, model) is null && transport.Calls.Count == 1, "Never to a channel of another server or an unknown one");
        Check(await channelClient.EditStatusAsync(OtherChannel, 5, model) == BotEditResult.Failed && transport.Calls.Count == 1, "No edit on another server");
        var refusals = logs.All.Split('\n').Count(l => l.Contains("refused to write to channel", StringComparison.Ordinal) && l.Contains(OtherChannel.ToString(), StringComparison.Ordinal));
        for (var n = 0; n < 5; n++) await channelClient.EditStatusAsync(OtherChannel, 5, model);
        Check(logs.All.Split('\n').Count(l => l.Contains("refused to write to channel", StringComparison.Ordinal) && l.Contains(OtherChannel.ToString(), StringComparison.Ordinal)) == refusals && refusals >= 1,
            "A refused channel is logged once, not on every refresh");
        await channelClient.PostNoteAsync(OtherChannel, "note");
        Check(transport.Calls.Count == 1, "No note on another server");
        Check(await channelClient.EditStatusAsync(Channel, posted!.Value, model) == BotEditResult.Edited && transport.Calls[^1] is { Kind: "modify", Channel: Channel } edit
              && edit.Message == posted.Value && NoMentions(edit.Content), "A status message is edited where it is");
        transport.MessageExists = false;
        Check(await channelClient.EditStatusAsync(Channel, posted.Value, model) == BotEditResult.Gone, "A deleted status message is reported");
        var callsBefore = transport.Calls.Count;
        Check(await channelClient.EditStatusAsync(Channel, 0, model) == BotEditResult.Gone && transport.Calls.Count == callsBefore, "No message, no edit");
        await channelClient.PostNoteAsync(Channel, "@everyone " + new string('x', 3000));
        Check(transport.Calls[^1] is { Kind: "send" } note && note.Content.Text.Length <= 2000 && NoMentions(note.Content) && note.Content.Embeds.Length == 0,
            "A note is short and mentions nobody");
        transport.Failure = new HttpException(HttpStatusCode.InternalServerError, null, reason: "broken " + SampleToken);
        Check(await channelClient.PostStatusAsync(Channel, model) is null && await channelClient.EditStatusAsync(Channel, 5, model) == BotEditResult.Failed,
            "Errors become null and a failed result, no exception");
        await channelClient.PostNoteAsync(Channel, "x");
        Check(!logs.All.Contains(SampleToken), "Errors of channels log no token");
        transport.Failure = null;
        guildOfBot = 0;
        Check(await channelClient.PostStatusAsync(Channel, model) is null, "Not connected: nowhere");
        guildOfBot = Guild;
        transport.LookupFails = true;
        Check(await channelClient.PostStatusAsync(Channel, model) is null, "An error in the lookup refuses");
        Check(DiscordNetChannelClient.IsAllowedChannel(Guild, Guild) && !DiscordNetChannelClient.IsAllowedChannel(null, Guild)
              && !DiscordNetChannelClient.IsAllowedChannel(OtherGuild, Guild) && !DiscordNetChannelClient.IsAllowedChannel(0, 0), "Allowed channels");

        // --- the state of the connection
        (DiscordBotState, DiscordBotProblem) Next(DiscordBotState s, DiscordBotProblem p, BotConnectionEvent e, bool verified = true, bool valid = true)
            => BotConnectionStateMachine.Next(s, p, e, verified, valid);
        const DiscordBotProblem none = DiscordBotProblem.None;
        // Commands that could not be registered: connected, but the panel is told. Nothing else wipes it, a good ready does.
        var noCommands = DiscordBotProblem.CommandsNotRegistered;
        Check(Next(DiscordBotState.Starting, none, BotConnectionEvent.ReadyVerifiedCommandsFailed) == (DiscordBotState.Connected, noCommands)
              && Next(DiscordBotState.Starting, none, BotConnectionEvent.ReadyVerifiedCommandsFailed, valid: false) == (DiscordBotState.Connected, DiscordBotProblem.InvalidSettings),
            "A ready without commands is connected with a problem (invalid settings come first)");
        Check(Next(DiscordBotState.Connected, noCommands, BotConnectionEvent.SettingsApplied) == (DiscordBotState.Connected, noCommands)
              && Next(DiscordBotState.Connected, noCommands, BotConnectionEvent.GatewayConnected) == (DiscordBotState.Connected, noCommands),
            "Applying settings or a resume keeps the commands problem");
        Check(Next(DiscordBotState.Connected, noCommands, BotConnectionEvent.ReadyVerified) == (DiscordBotState.Connected, none)
              && Next(DiscordBotState.Connected, noCommands, BotConnectionEvent.SettingsApplied, valid: false) == (DiscordBotState.Connected, DiscordBotProblem.InvalidSettings),
            "Registered commands (or invalid settings) replace the commands problem");
        Check(Next(DiscordBotState.Stopped, none, BotConnectionEvent.Starting) == (DiscordBotState.Starting, none)
              && Next(DiscordBotState.Error, DiscordBotProblem.NoToken, BotConnectionEvent.Starting) == (DiscordBotState.Starting, none), "Starting clears the problem");
        foreach (var (e, problem) in new[]
                 {
                     (BotConnectionEvent.SettingsInvalid, DiscordBotProblem.InvalidSettings), (BotConnectionEvent.TokenMissing, DiscordBotProblem.NoToken),
                     (BotConnectionEvent.TokenBusy, DiscordBotProblem.TokenInUse), (BotConnectionEvent.LoginRejected, DiscordBotProblem.LoginFailed),
                     (BotConnectionEvent.LoginUnreachable, DiscordBotProblem.ConnectionLost), (BotConnectionEvent.GuildMissing, DiscordBotProblem.GuildNotFound),
                     (BotConnectionEvent.Failed, DiscordBotProblem.Unknown)
                 })
            Check(Next(DiscordBotState.Starting, none, e) == (DiscordBotState.Error, problem), $"{e} is the problem {problem}");
        Check(Next(DiscordBotState.Starting, none, BotConnectionEvent.ReadyVerified) == (DiscordBotState.Connected, none), "Ready with the server connects");
        Check(Next(DiscordBotState.Starting, none, BotConnectionEvent.ReadyVerified, valid: false) == (DiscordBotState.Connected, DiscordBotProblem.InvalidSettings),
            "Connected with invalid settings says so");
        Check(Next(DiscordBotState.Starting, none, BotConnectionEvent.GatewayConnected, verified: false) == (DiscordBotState.Starting, none),
            "Connected without the server seen is not connected yet");
        Check(Next(DiscordBotState.Starting, none, BotConnectionEvent.GatewayDisconnected) == (DiscordBotState.Starting, none),
            "A lost connection while starting is retried");
        Check(Next(DiscordBotState.Starting, none, BotConnectionEvent.ConnectDeadline) == (DiscordBotState.Error, DiscordBotProblem.ConnectionLost),
            "No ready in time is an error");
        Check(Next(DiscordBotState.Connected, none, BotConnectionEvent.GatewayDisconnected) == (DiscordBotState.Error, DiscordBotProblem.ConnectionLost),
            "A lost connection is shown");
        Check(Next(DiscordBotState.Error, DiscordBotProblem.ConnectionLost, BotConnectionEvent.GatewayConnected) == (DiscordBotState.Connected, none),
            "A resumed connection is connected again");
        Check(Next(DiscordBotState.Error, DiscordBotProblem.ConnectionLost, BotConnectionEvent.ReadyVerified) == (DiscordBotState.Connected, none),
            "A late ready connects");
        Check(Next(DiscordBotState.Error, DiscordBotProblem.GuildNotFound, BotConnectionEvent.GatewayConnected) == (DiscordBotState.Error, DiscordBotProblem.GuildNotFound)
              && Next(DiscordBotState.Error, DiscordBotProblem.GuildNotFound, BotConnectionEvent.ReadyVerified) == (DiscordBotState.Error, DiscordBotProblem.GuildNotFound),
            "A refused server stays refused");
        Check(Next(DiscordBotState.Connected, none, BotConnectionEvent.ConnectDeadline) == (DiscordBotState.Connected, none), "A late deadline changes nothing");
        Check(Next(DiscordBotState.Connected, none, BotConnectionEvent.SettingsApplied, valid: false) == (DiscordBotState.Connected, DiscordBotProblem.InvalidSettings)
              && Next(DiscordBotState.Connected, DiscordBotProblem.InvalidSettings, BotConnectionEvent.SettingsApplied) == (DiscordBotState.Connected, none),
            "Settings apply while connected");
        Check(Next(DiscordBotState.Stopped, none, BotConnectionEvent.SettingsApplied, valid: false) == (DiscordBotState.Stopped, none), "Settings do not start anything");
        Check(Next(DiscordBotState.Connected, none, BotConnectionEvent.Stopping) == (DiscordBotState.Stopping, none)
              && Next(DiscordBotState.Stopping, none, BotConnectionEvent.Stopped) == (DiscordBotState.Stopped, none), "Stopping and stopped");
        var gateway = new[] { BotConnectionEvent.GatewayConnected, BotConnectionEvent.GatewayDisconnected, BotConnectionEvent.ReadyVerified,
            BotConnectionEvent.ConnectDeadline, BotConnectionEvent.SettingsApplied };
        foreach (var state in new[] { DiscordBotState.Stopped, DiscordBotState.Stopping })
        foreach (var e in gateway)
        foreach (var verified in new[] { false, true })
            Check(Next(state, none, e, verified) == (state, none), $"{e} does not wake a {state} bot");
        foreach (var state in Enum.GetValues<DiscordBotState>())
        foreach (var problem in Enum.GetValues<DiscordBotProblem>())
        foreach (var e in Enum.GetValues<BotConnectionEvent>())
        {
            // An error without a problem never happens, so it is no start for a transition.
            if (state == DiscordBotState.Error && problem == none) continue;
            var (nextState, nextProblem) = Next(state, problem, e, verified: false);
            if (e != BotConnectionEvent.ReadyVerified && e != BotConnectionEvent.ReadyVerifiedCommandsFailed && e != BotConnectionEvent.SettingsApplied)
                Check(nextState != DiscordBotState.Connected || state == DiscordBotState.Connected, $"Only a verified server connects ({state} {problem} {e})");
            Check(nextState != DiscordBotState.Error || nextProblem != none, $"An error always has a problem ({state} {problem} {e})");
        }

        Check(BotConnectionStateMachine.PlanGuilds(Guild, [OtherGuild, Guild, OtherGuild + 1]) is { Found: true } plan
              && plan.Leave.Order().SequenceEqual([OtherGuild, OtherGuild + 1]), "The bot leaves every other server");
        Check(BotConnectionStateMachine.PlanGuilds(Guild, [OtherGuild]) is { Found: false, Leave.Count: 0 }, "Without its server it leaves nothing and stops");
        Check(BotConnectionStateMachine.PlanGuilds(0, [0, OtherGuild]) is { Found: false }, "Server 0 is never found");

        // --- the security check
        BotGuildSnapshot Live(Func<BotGuildSnapshot, BotGuildSnapshot>? change = null)
        {
            var snapshot = new BotGuildSnapshot(Guild, "Test Server", new HashSet<ulong> { Guild, Role, OtherRole }, [], false,
                new BotChannelAccess(true, true, true, true, true), 42);
            return change is null ? snapshot : change(snapshot);
        }
        BotSecurityInput Input(Func<BotSecurityInput, BotSecurityInput>? change = null)
        {
            var input = new BotSecurityInput(true, true, [], Guild, [Role], Channel, true, Live());
            return change is null ? input : change(input);
        }
        BotCheckItem Item(IReadOnlyList<BotCheckItem> items, BotCheckKind kind) => items.Single(x => x.Kind == kind);

        var healthy = BotSecurityChecks.Build(Input());
        Check(Enum.GetValues<BotCheckKind>().All(k => healthy.Any(x => x.Kind == k)) && healthy.All(x => x.Status == BotCheckStatus.Ok),
            "A good setup is green everywhere");
        Check(Item(healthy, BotCheckKind.Connection).Detail == "42 ms" && Item(healthy, BotCheckKind.Guild).Detail == "Test Server", "Latency and server name");
        var offline = BotSecurityChecks.Build(Input(x => x with { TokenStored = false, TokenReadable = false, Connected = false, Live = null,
            Issues = [new BotConfigIssue(BotConfigIssueKind.NoRoles), new BotConfigIssue(BotConfigIssueKind.InvalidId, "AllowedUserIds: 5 " + SampleToken)] }));
        Check(Item(offline, BotCheckKind.Token) is { Status: BotCheckStatus.Error, Detail: "not set" }, "No token is an error");
        Check(offline.Count(x => x.Kind == BotCheckKind.Settings) == 2 && offline.Where(x => x.Kind == BotCheckKind.Settings).All(x => x.Status == BotCheckStatus.Error),
            "One error per settings issue");
        Check(new[] { BotCheckKind.Guild, BotCheckKind.Roles, BotCheckKind.OtherGuilds, BotCheckKind.AdministratorPermission, BotCheckKind.Channel, BotCheckKind.Connection }
                .All(k => Item(offline, k).Status == BotCheckStatus.Warning), "Offline: what can not be checked is a warning");
        Check(offline.All(x => !x.Detail.Contains(SampleToken)) && healthy.Concat(offline).All(x => !x.Detail.Contains(SampleToken)), "No check holds the token");
        Check(Item(BotSecurityChecks.Build(Input(x => x with { TokenReadable = false })), BotCheckKind.Token) is { Status: BotCheckStatus.Error, Detail: "stored but not readable" },
            "A token that can not be read is an error");
        Check(Item(BotSecurityChecks.Build(Input(x => x with { RoleIds = [Role, 300000000000000099UL] })), BotCheckKind.Roles) is { Status: BotCheckStatus.Error } missingRole
              && missingRole.Detail.Contains("300000000000000099"), "A role that does not exist is an error: nobody would get in");
        Check(Item(BotSecurityChecks.Build(Input(x => x with { RoleIds = [] })), BotCheckKind.Roles).Status == BotCheckStatus.Error, "No role is an error");
        Check(Item(BotSecurityChecks.Build(Input(x => x with { RoleIds = [Role, Guild] })), BotCheckKind.EveryoneRole).Status == BotCheckStatus.Error
              && Item(BotSecurityChecks.Build(Input(x => x with { RoleIds = [Role, Guild], Connected = false, Live = null })), BotCheckKind.EveryoneRole).Status == BotCheckStatus.Error,
            "@everyone is an error, also offline");
        Check(Item(BotSecurityChecks.Build(Input(x => x with { Live = Live(l => l with { OtherGuildIds = [OtherGuild] }) })), BotCheckKind.OtherGuilds).Status == BotCheckStatus.Error,
            "Another server is an error");
        Check(Item(BotSecurityChecks.Build(Input(x => x with { Live = Live(l => l with { BotIsAdministrator = true }) })), BotCheckKind.AdministratorPermission).Status == BotCheckStatus.Warning
              && Item(BotSecurityChecks.Build(Input(x => x with { Live = Live(l => l with { BotIsAdministrator = null }) })), BotCheckKind.AdministratorPermission).Status == BotCheckStatus.Warning,
            "Administrator permission (or not knowing) is a warning");
        Check(Item(BotSecurityChecks.Build(Input(x => x with { AdminChannelId = 0 })), BotCheckKind.Channel) is { Status: BotCheckStatus.Warning, Detail: "not set" },
            "No admin channel is a warning");
        foreach (var access in new[] { new BotChannelAccess(false, false, false, false, false), new BotChannelAccess(true, false, true, true, true),
                     new BotChannelAccess(true, true, false, true, true), new BotChannelAccess(true, true, true, false, true), new BotChannelAccess(true, true, true, true, false) })
            Check(Item(BotSecurityChecks.Build(Input(x => x with { Live = Live(l => l with { AdminChannel = access }) })), BotCheckKind.Channel).Status == BotCheckStatus.Error,
                $"The bot has to see, write and embed in the admin channel ({access})");
        Check(Item(BotSecurityChecks.Build(Input(x => x with { Live = null })), BotCheckKind.Guild).Status == BotCheckStatus.Error, "Connected without the server is an error");

        // --- the token stays out of every log
        var logMessage = new LogMessage(LogSeverity.Error, "Gateway", "login with " + SampleToken,
            new AggregateException(new InvalidOperationException("token " + SampleToken, new Exception("inner " + UnseenToken)), new Exception("second\n" + SampleToken)));
        var line = DiscordNetLog.Format(logMessage);
        Check(!line.Contains(SampleToken) && !line.Contains(UnseenToken) && line.Contains(BotLogRedactor.Replacement) && line.Contains("inner") && line.Contains("second")
              && !line.Contains('\n'), "Messages of Discord.Net: one line, the whole chain, no token");
        // "Exception: " and 1947 characters: a cut at 2000 would keep 42 characters of the token, too few for its pattern.
        var edge = DiscordNetLog.Describe(new Exception(new string('x', 1947) + UnseenToken));
        Check(!Enumerable.Range(0, UnseenToken.Length - 20).Any(i => edge.Contains(UnseenToken.Substring(i, 20))), "Cutting a long text leaves no piece of a token");
        Check(DiscordNetLog.Describe(null) == string.Empty && DiscordNetLog.LevelOf(LogSeverity.Critical) == LogLevel.Critical
              && DiscordNetLog.LevelOf(LogSeverity.Warning) == LogLevel.Warning && DiscordNetLog.LevelOf(LogSeverity.Debug) == LogLevel.Trace, "Log levels");

        // --- the service, without Discord: fail closed, settings, no token anywhere
        var folder = Path.Combine(Path.GetTempPath(), "TrebuchetConsoleChecks-botnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var serviceLogs = new CapturingLoggerFactory();
            FakeActions? lastActions = null;
            DiscordBotService Service(DiscordBotConfig config, string? token) => new(config,
                new BotTokenStore(Path.Combine(folder, "bot.token"), new ReversingProtector(), _ => token),
                new BotAuditLog(Path.Combine(folder, "audit")), lastActions = new FakeActions(), new BotGuard(new BotRateLimiter()), new BotPendingActions(), serviceLogs);

            // The service listens for changes of the servers (to refresh the status messages at once) for as long as it lives.
            var listening = Service(ValidConfig(), null);
            var announcer = lastActions!;
            Check(announcer.Subscribers == 1, "The service listens for changes of the servers");
            announcer.Raise();
            Check(listening.State == DiscordBotState.Stopped, "A change before the bot runs is noted and does nothing");
            await listening.DisposeAsync();
            Check(announcer.Subscribers == 0, "A disposed service no longer listens");
            announcer.Raise();

            var invalid = ValidConfig();
            invalid.AllowedRoleIds.Clear();
            var invalidService = Service(invalid, SampleToken);
            var raised = 0;
            var outsideLock = true;
            invalidService.StateChanged += (_, _) =>
            {
                raised++;
                // Raised under the lock of the service, another thread could not read the state.
                var reader = new Thread(() => _ = invalidService.State);
                reader.Start();
                if (!reader.Join(2000)) outsideLock = false;
            };
            await invalidService.StartAsync(CancellationToken.None);
            Check(invalidService is { State: DiscordBotState.Error, Problem: DiscordBotProblem.InvalidSettings, Policy: null }
                  && invalidService.Issues.Any(x => x.Kind == BotConfigIssueKind.NoRoles), "Invalid settings do not start");
            Check(raised > 0 && outsideLock, "The state change is raised outside of any lock");
            await invalidService.StopAsync();
            await invalidService.StopAsync();
            Check(invalidService is { State: DiscordBotState.Stopped, Problem: DiscordBotProblem.None }, "Stop works twice");

            var noToken = Service(ValidConfig(), null);
            await noToken.StartAsync(CancellationToken.None);
            Check(noToken is { State: DiscordBotState.Error, Problem: DiscordBotProblem.NoToken } && noToken.Policy is not null && noToken.Issues.Count == 0,
                "No token does not start");

            var busy = Service(ValidConfig(), SampleToken);
            using (var held = BotTokenLock.TryAcquire(SampleToken))
            {
                Check(held is not null, "The check holds the token");
                await busy.StartAsync(CancellationToken.None);
                Check(busy is { State: DiscordBotState.Error, Problem: DiscordBotProblem.TokenInUse }, "A token in use does not start a second bot");
            }
            using (var again = BotTokenLock.TryAcquire(SampleToken))
                Check(again is not null, "The refused start holds no lock");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var never = Service(ValidConfig(), SampleToken);
                await never.StartAsync(cancelled.Token);
                Check(never.State == DiscordBotState.Stopped, "A cancelled start does nothing");
            }

            var serviceWithLog = Service(ValidConfig(), SampleToken);
            serviceWithLog.ForwardDiscordLog(logMessage);
            serviceWithLog.ForwardDiscordLog(new LogMessage(LogSeverity.Warning, "Rest", null!, new HttpException(HttpStatusCode.Unauthorized, null, reason: UnseenToken)));
            Check(serviceLogs.Lines.Count >= 2 && !serviceLogs.All.Contains(SampleToken) && !serviceLogs.All.Contains(UnseenToken),
                "No token in anything the service logged");

            // Settings for the handler and the board.
            var path = Path.Combine(folder, "discord-bot.test.json");
            var saved = ValidConfig(path);
            saved.AllowedChannelIds = [Channel, OtherChannel];
            saved.StatusMessageIds[0] = 123456789012345678UL;
            saved.SaveFile();
            var settings = Service(saved, null);
            Check(settings.Policy is null && settings.AdminChannelId == 0, "No policy before the settings are applied");
            await settings.ApplySettingsAsync();
            Check(settings.Policy is { } applied && applied.ChannelIds.SetEquals([Channel, OtherChannel]) && settings.AdminChannelId == Channel,
                "The policy and the admin channel come from the settings");
            Check(settings.State == DiscordBotState.Stopped, "Applying settings starts nothing");
            Check(settings.GetStatusMessageId(0) == 123456789012345678UL && settings.GetStatusMessageId(1) == 0, "Status messages from the settings");
            settings.SetStatusMessageId(1, 223456789012345678UL);
            Check(settings.GetStatusMessageId(1) == 223456789012345678UL && DiscordBotConfig.LoadConfig(path).StatusMessageIds[1] == 223456789012345678UL,
                "A status message is saved");
            settings.SetStatusMessageId(1, 0);
            Check(settings.GetStatusMessageId(1) == 0 && !DiscordBotConfig.LoadConfig(path).StatusMessageIds.ContainsKey(1), "A forgotten status message is removed");

            await settings.SetAdminChannelAsync(OtherChannel);
            var reloaded = DiscordBotConfig.LoadConfig(path);
            Check(reloaded.AllowedChannelIds.SequenceEqual([OtherChannel]) && reloaded.StatusMessageIds.Count == 0, "The admin channel is saved, old status messages dropped");
            Check(settings.AdminChannelId == OtherChannel && settings.Policy!.ChannelIds.SetEquals([OtherChannel]), "The new channel applies at once");
            var notId = false;
            try { await settings.SetAdminChannelAsync(5); } catch (ArgumentException) { notId = true; }
            Check(notId && settings.AdminChannelId == OtherChannel, "A channel that is no ID is refused");

            saved.AllowedRoleIds.Clear();
            await settings.ApplySettingsAsync();
            Check(settings.Policy is null && settings.Issues.Any(x => x.Kind == BotConfigIssueKind.NoRoles), "Invalid settings allow nobody anything at once");
            saved.AddRole(Role);
            await settings.ApplySettingsAsync();
            Check(settings.Policy is not null && settings.Issues.Count == 0, "Valid settings apply at once");

            var memoryOnly = ValidConfig();
            memoryOnly.AllowedChannelIds = [Channel];
            var unsaved = Service(memoryOnly, null);
            await unsaved.ApplySettingsAsync();
            var saveFailed = false;
            try { await unsaved.SetAdminChannelAsync(OtherChannel); } catch (IOException ex) { saveFailed = !ex.Message.Contains(folder); }
            Check(saveFailed && memoryOnly.AllowedChannelIds.SequenceEqual([Channel]) && unsaved.AdminChannelId == Channel,
                "A channel that can not be saved changes nothing");
            unsaved.SetStatusMessageId(0, 323456789012345678UL);
            Check(unsaved.GetStatusMessageId(0) == 323456789012345678UL, "A status message that can not be saved stays in memory");

            var checks = await Service(ValidConfig(), SampleToken).CheckSecurityAsync(CancellationToken.None);
            Check(Item(checks, BotCheckKind.Token).Status == BotCheckStatus.Ok && Item(checks, BotCheckKind.Settings).Status == BotCheckStatus.Ok
                  && Item(checks, BotCheckKind.Connection) is { Status: BotCheckStatus.Warning, Detail: "not connected" }, "The security check works offline");
            var noTokenChecks = await Service(ValidConfig(), null).CheckSecurityAsync(CancellationToken.None);
            Check(Item(noTokenChecks, BotCheckKind.Token).Status == BotCheckStatus.Error, "The security check sees a missing token");

            var disposed = Service(ValidConfig(), SampleToken);
            await disposed.DisposeAsync();
            await disposed.DisposeAsync();
            await disposed.StartAsync(CancellationToken.None);
            Check(disposed.State == DiscordBotState.Stopped, "A disposed bot does not start");
            Check(!serviceLogs.All.Contains(SampleToken), "No token in the log of the service");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        return count;
    }
}
