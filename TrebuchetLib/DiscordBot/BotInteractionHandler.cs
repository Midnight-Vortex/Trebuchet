using System.Globalization;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

public enum BotOptionType { Integer, String }

public sealed record BotSlashOption(string Name, string Description, BotOptionType Type, bool Required, long? Min = null, long? Max = null);

/// <summary>A slash command or sub command: <c>Path</c> is "status", "restart postpone", "bot channel set".</summary>
public sealed record BotSlashCommand(string Path, string Description, IReadOnlyList<BotSlashOption> Options);

/// <summary>
/// Everything that happens after Discord delivered a command, button click or menu choice, without any Discord type:
/// gate (<see cref="BotGuard"/>), questions, the action through <see cref="IBotActions"/>, audit, answer. The Discord
/// layer only converts to <see cref="BotInteraction"/> and renders the replies.
/// </summary>
/// <remarks>
/// Order for every request: read what was asked (no decision yet) → the gate (server, channel, user ID and role, command
/// switch, level, wait and limits) → validate the inputs → ask a question (confirmation, minutes) or act → audit → answer.
/// A user who is not allowed gets the same neutral answer whatever was asked, so nothing leaks before the gate.
/// </remarks>
public sealed class BotInteractionHandler
{
    private const int MaxEmbeds = 10;
    private static readonly TimeSpan SelectLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ConfirmLifetime = BotPendingActions.DefaultLifetime;

    private static readonly IReadOnlyDictionary<string, BotCommand> Paths = new Dictionary<string, BotCommand>(StringComparer.Ordinal)
    {
        ["status"] = BotCommand.Status,
        ["restart postpone"] = BotCommand.Postpone,
        ["restart resume"] = BotCommand.Resume,
        ["restart now"] = BotCommand.RestartNow,
        ["stop"] = BotCommand.Stop,
        ["start"] = BotCommand.Start,
        ["update"] = BotCommand.Update,
        ["updatecheck"] = BotCommand.CheckUpdates,
        ["bot channel set"] = BotCommand.SetChannel
    };

    private readonly IBotActions _actions;
    private readonly BotGuard _guard;
    private readonly BotPendingActions _pending;
    private readonly BotAuditLog _audit;
    private readonly IBotSettings _settings;
    private readonly Func<DateTime> _clock;
    private readonly ILogger? _logger;
    private readonly BotPostponeBudget _budget = new();

    public BotInteractionHandler(IBotActions actions, BotGuard guard, BotPendingActions pending, BotAuditLog audit,
        IBotSettings settings, Func<DateTime>? clock = null, ILogger? logger = null)
    {
        _actions = actions;
        _guard = guard;
        _pending = pending;
        _audit = audit;
        _settings = settings;
        _clock = clock ?? (() => DateTime.UtcNow);
        _logger = logger;
    }

    /// <summary>How long a command waits for the launcher before it answers "running". The action itself continues.</summary>
    public TimeSpan ActionTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Called after an action changed something, so the status messages can be refreshed at once.</summary>
    public Func<CancellationToken, Task>? AfterAction { get; set; }

    /// <summary>The slash commands the Discord layer registers for the server (names and options, no logic).</summary>
    public static IReadOnlyList<BotSlashCommand> SlashCommands { get; } =
    [
        new("status", "Show the state of the servers", [Instance(false)]),
        new("restart postpone", "Postpone a pending restart, stop or update", [
            Instance(true),
            new BotSlashOption("minutes", "Minutes to postpone", BotOptionType.Integer, true, 1, DiscordBotConfig.HardMaxPauseMinutes),
            new BotSlashOption("reason", "Reason (only for the log)", BotOptionType.String, false)]),
        new("restart resume", "End a postponement: automatic restarts run again", [Instance(true)]),
        new("restart now", "Restart a server (players are warned first)", [
            Instance(true),
            new BotSlashOption("delay", "Warning time in minutes (default: from the settings)", BotOptionType.Integer, false, 0,
                BotInputRules.MaxDelayMinutes(BotCommand.RestartNow))]),
        new("stop", "Stop a server (players are warned first)", [
            Instance(true),
            new BotSlashOption("delay", "Warning time in minutes (default: from the settings)", BotOptionType.Integer, false, 0,
                BotInputRules.MaxDelayMinutes(BotCommand.Stop))]),
        new("start", "Start a stopped server", [Instance(true)]),
        new("update", "Check for updates and install them", []),
        new("updatecheck", "Check for mod and server updates (installs nothing)", []),
        new("bot channel set", "Make this channel the admin channel of the bot", [])
    ];

    private static BotSlashOption Instance(bool required) =>
        new("instance", "Server instance number", BotOptionType.Integer, required, 0, 99);

    /// <summary>Never throws. Whatever goes wrong is answered with a neutral message and logged.</summary>
    public async Task HandleAsync(BotInteraction interaction, IBotResponder responder, CancellationToken token)
    {
        var messages = BotMessages.For(_settings.Policy?.Language);
        var update = interaction.Kind != BotInteractionKind.Slash && IsOwnQuestion(interaction.Name);
        try
        {
            await HandleCoreAsync(interaction, responder, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Handling a Discord interaction failed");
            await TryAnswerAsync(responder, new BotReply(messages.Failed), update).ConfigureAwait(false);
        }
    }

    private async Task HandleCoreAsync(BotInteraction i, IBotResponder responder, CancellationToken token)
    {
        var policy = _settings.Policy;
        var messages = BotMessages.For(policy?.Language);
        var now = _clock();

        // 1. Read what was asked. No decision is made on it yet.
        var ask = Read(i, now, policy);

        // 2. The gate: nothing below runs for anyone it does not let through.
        var request = new BotRequest(i.UserId, i.GuildId, i.ChannelId, i.RoleIds ?? [], i.IsHuman, ask.GuardCommand, i.InteractionId);
        var gate = _guard.Evaluate(policy, request, ask.Instance, ask.FollowUp, ask.CountDenial, !ask.NotFinal);
        switch (gate.Outcome)
        {
            case BotGuardOutcome.Ignored:
                return;
            case BotGuardOutcome.Denied:
                Audit(i, BotAuditResult.Denied, ask, gate.Reason.ToString());
                if (gate.LockedOutNow)
                {
                    Audit(i, BotAuditResult.LockedOut, ask, "too many refused requests");
                }
                await AnswerAsync(responder, new BotReply(messages.NoPermission), ask.UpdateMessage).ConfigureAwait(false);
                return;
            case BotGuardOutcome.Throttled:
                Audit(i, BotAuditResult.Throttled, ask, gate.Throttle.ToString());
                await AnswerAsync(responder,
                    new BotReply(gate.Throttle == BotThrottle.CriticalLimit ? messages.CriticalLimit : messages.Wait), ask.UpdateMessage).ConfigureAwait(false);
                return;
        }

        // From here on the user is allowed to use ask.GuardCommand (the baseline Status for unknown requests).
        var allowedPolicy = policy!;
        Audit(i, BotAuditResult.Allowed, ask, string.Empty);

        if (ask.Unknown || ask.Expired)
        {
            // An expired question (often the second click of a double click) is answered as a new message: replacing the
            // private message would overwrite the result the first click showed.
            await AnswerAsync(responder, new BotReply(ask.Expired ? messages.Expired : messages.UnknownRequest), ask.UpdateMessage && !ask.Expired).ConfigureAwait(false);
            return;
        }

        // 3. Act on it.
        switch (ask.Kind)
        {
            case Step.Cancel:
                await AnswerAsync(responder, new BotReply(messages.Cancelled), true).ConfigureAwait(false);
                Audit(i, BotAuditResult.Cancelled, ask, string.Empty);
                return;
            case Step.Confirm:
                await ConfirmedAsync(i, ask, allowedPolicy, messages, responder, token).ConfigureAwait(false);
                return;
            case Step.PostponeChoice:
                await ChoicePickedAsync(i, ask, allowedPolicy, messages, responder, token).ConfigureAwait(false);
                return;
        }

        switch (ask.Command)
        {
            case BotCommand.Status:
                await StatusAsync(i, ask, allowedPolicy, messages, responder, token).ConfigureAwait(false);
                break;
            case BotCommand.Postpone:
                await PostponeAsync(i, ask, allowedPolicy, messages, responder, token).ConfigureAwait(false);
                break;
            case BotCommand.Resume:
                if (!TryInstance(ask, messages, out var resumeError))
                    await AnswerAsync(responder, resumeError!, false).ConfigureAwait(false);
                else
                    await RunAsync(i, ask, BotCommand.Resume, ask.Instance!.Value, 0, allowedPolicy, messages, responder, false, token).ConfigureAwait(false);
                break;
            case BotCommand.CheckUpdates:
                await RunAsync(i, ask, BotCommand.CheckUpdates, -1, 0, allowedPolicy, messages, responder, false, token).ConfigureAwait(false);
                break;
            case BotCommand.RestartNow or BotCommand.Stop when i.Kind == BotInteractionKind.Button:
                // The buttons of the status message ask for the warning time first, like the dialog in Trebuchet.
                await AskDelayAsync(i, ask, allowedPolicy, messages, responder, token).ConfigureAwait(false);
                break;
            default:
                await AskConfirmationAsync(i, ask, allowedPolicy, messages, responder, token).ConfigureAwait(false);
                break;
        }
    }

    // ---- reading the request

    private enum Step { Initial, Confirm, Cancel, PostponeChoice }

    private sealed record Ask(
        BotCommand Command, BotCommand GuardCommand, Step Kind, int? Instance, bool FollowUp, bool Unknown, bool Expired, bool UpdateMessage,
        BotPending? Pending, long? RawInstance, long? RawMinutes, long? RawDelay, string? Reason, string Parameters)
    {
        /// <summary>False for a cancel or an expired question: a refusal there does not count toward the lock out.</summary>
        public bool CountDenial { get; init; } = true;

        /// <summary>The menu in front of a confirmation: it does not use up the limit of critical actions, the confirmation does.</summary>
        public bool NotFinal { get; init; }
    }

    // The bot only knows the instances chosen in its settings: any other one is answered like one that does not exist.
    private static bool IsManaged(BotPolicy? policy, int instance) => policy is null || policy.IsManaged(instance);

    private Ask Read(BotInteraction i, DateTime now, BotPolicy? policy)
    {
        var numbers = i.Numbers;
        long? raw(string key) => numbers is not null && numbers.TryGetValue(key, out var v) ? v : null;
        string? text(string key) => i.Texts is not null && i.Texts.TryGetValue(key, out var v) ? v : null;
        var count = _actions.InstanceCount;

        if (i.Kind == BotInteractionKind.Slash)
        {
            var path = (i.Name ?? string.Empty).Trim().ToLowerInvariant();
            if (!Paths.TryGetValue(path, out var command))
                return Unknown(false);
            var rawInstance = raw("instance");
            int? instance = rawInstance is { } r && BotInputRules.TryParseInstance(r, count, out var parsed) && IsManaged(policy, parsed) ? parsed : null;
            var reason = BotInputRules.SanitizeReason(text("reason"));
            return new Ask(command, command, Step.Initial, instance, false, false, false, false, null, rawInstance, raw("minutes"), raw("delay"),
                reason, Describe(command, rawInstance, raw("minutes"), raw("delay"), reason));
        }

        if (!BotCustomId.TryParse(i.Name, count, out var kind, out var buttonCommand, out var buttonInstance, out var nonce))
        {
            // Maybe an old button of an instance that does not exist any more: same neutral handling.
            return Unknown(IsOwnQuestion(i.Name));
        }

        switch (kind)
        {
            case BotCustomIdKind.Action:
                return new Ask(buttonCommand, buttonCommand, Step.Initial, IsManaged(policy, buttonInstance) ? buttonInstance : null, false, false, false, false,
                    null, buttonInstance, null, null, string.Empty, Describe(buttonCommand, buttonInstance, null, null, string.Empty));
            default:
            {
                var step = kind switch
                {
                    BotCustomIdKind.Confirm => Step.Confirm,
                    BotCustomIdKind.Cancel => Step.Cancel,
                    _ => Step.PostponeChoice
                };
                if (!_pending.TryTake(nonce, i.UserId, now, out var pending))
                {
                    // Expired, used before, or not this user's: the user is still checked as a user of the bot.
                    return new Ask(BotCommand.Status, BotCommand.Status, step, null, true, false, true, true, null, null, null, null, null, $"code={kind}")
                    {
                        CountDenial = false
                    };
                }
                // A cancel is no action: it must not use up a limit of the command it cancels. Cancelling the channel
                // command is checked as that command: it works from outside the current admin channel too.
                var guardCommand = step == Step.Cancel
                    ? (pending.Command == BotCommand.SetChannel ? BotCommand.SetChannel : BotCommand.Status)
                    : pending.Command;
                int? instance = pending.Instance >= 0 && pending.Instance < count && IsManaged(policy, pending.Instance) ? pending.Instance : null;
                return new Ask(pending.Command, guardCommand, step, instance, true, false, false, true, pending, pending.Instance, pending.Minutes, null, null,
                    Describe(pending.Command, pending.Instance, pending.Minutes, null, string.Empty) + $" step={step}")
                {
                    CountDenial = step != Step.Cancel,
                    NotFinal = step == Step.PostponeChoice && pending.Command != BotCommand.Postpone
                };
            }
        }
    }

    private static Ask Unknown(bool update) =>
        new(BotCommand.Status, BotCommand.Status, Step.Initial, null, false, true, false, update, null, null, null, null, null, "unknown");

    private static bool IsOwnQuestion(string? customId) =>
        customId is not null && (customId.StartsWith("tb1:ok:", StringComparison.Ordinal) || customId.StartsWith("tb1:no:", StringComparison.Ordinal)
                                 || customId.StartsWith("tb1:pp:", StringComparison.Ordinal));

    private static string Describe(BotCommand command, long? instance, long? minutes, long? delay, string? reason)
    {
        var parts = new List<string>();
        if (instance is { } n) parts.Add($"instance={n.ToString(CultureInfo.InvariantCulture)}");
        if (minutes is { } m && m != 0) parts.Add($"minutes={m.ToString(CultureInfo.InvariantCulture)}");
        if (delay is { } d) parts.Add($"delay={d.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrEmpty(reason)) parts.Add($"reason={reason}");
        return string.Join(' ', parts);
    }

    private static bool TryInstance(Ask ask, BotMessages messages, out BotReply? error)
    {
        if (ask.Instance is not null)
        {
            error = null;
            return true;
        }
        error = new BotReply(ask.RawInstance is null ? messages.InvalidInput : messages.UnknownServer);
        return false;
    }

    // ---- commands

    private async Task StatusAsync(BotInteraction i, Ask ask, BotPolicy policy, BotMessages messages, IBotResponder responder, CancellationToken token)
    {
        if (ask.RawInstance is not null && ask.Instance is null)
        {
            await AnswerAsync(responder, new BotReply(messages.UnknownServer), false).ConfigureAwait(false);
            return;
        }
        var statuses = await _actions.GetStatusesAsync(token).ConfigureAwait(false);
        var texts = BotTexts.For(policy.Language);
        var now = _clock();
        var embeds = statuses
            .Where(x => policy.IsManaged(x.Instance) && (ask.Instance is null || x.Instance == ask.Instance))
            .Take(MaxEmbeds)
            .Select(x => BotStatusMessageBuilder.Build(x, texts, now))
            .ToList();
        await AnswerAsync(responder, new BotReply(embeds.Count == 0 ? messages.NoServers : messages.StatusTitle, embeds), false).ConfigureAwait(false);
        Audit(i, BotAuditResult.Executed, ask, $"{embeds.Count} servers");
    }

    private async Task PostponeAsync(BotInteraction i, Ask ask, BotPolicy policy, BotMessages messages, IBotResponder responder, CancellationToken token)
    {
        if (!TryInstance(ask, messages, out var error))
        {
            await AnswerAsync(responder, error!, false).ConfigureAwait(false);
            return;
        }
        var instance = ask.Instance!.Value;

        if (i.Kind == BotInteractionKind.Slash)
        {
            if (ask.RawMinutes is not { } raw || !BotInputRules.TryParseMinutes(raw, policy, out var minutes))
            {
                await AnswerAsync(responder, new BotReply(messages.InvalidInput), false).ConfigureAwait(false);
                return;
            }
            await RunAsync(i, ask, BotCommand.Postpone, instance, minutes, policy, messages, responder, false, token, ask.Reason).ConfigureAwait(false);
            return;
        }

        // The button of the status message: the user picks the minutes from the offered ones.
        var nonce = _pending.Create(i.UserId, BotCommand.Postpone, instance, _clock(), SelectLifetime, 0, i.ChannelId ?? 0);
        var choices = policy.PostponeChoices.Select(m => new BotChoice(messages.FormatMinutes(m), m.ToString(CultureInfo.InvariantCulture))).ToList();
        await AnswerAsync(responder,
            new BotReply(messages.PostponePlaceholder, Question: new BotSelectQuestion(nonce, messages.PostponePlaceholder, choices)), false).ConfigureAwait(false);
    }

    private async Task AskDelayAsync(BotInteraction i, Ask ask, BotPolicy policy, BotMessages messages, IBotResponder responder, CancellationToken token)
    {
        if (!TryInstance(ask, messages, out var error))
        {
            await AnswerAsync(responder, error!, false).ConfigureAwait(false);
            return;
        }
        var command = ask.Command;
        var instance = ask.Instance!.Value;
        var nonce = _pending.Create(i.UserId, command, instance, _clock(), SelectLifetime, 0, i.ChannelId ?? 0);
        var choices = BotInputRules.DelayChoices(command)
            .Select(m => new BotChoice(m == 0 ? messages.NowChoice : messages.FormatMinutes(m), m.ToString(CultureInfo.InvariantCulture)))
            .ToList();
        var text = string.Format(CultureInfo.InvariantCulture, command == BotCommand.RestartNow ? messages.AskRestartDelay : messages.AskStopDelay,
            await ServerLabelAsync(instance, token).ConfigureAwait(false));
        await AnswerAsync(responder, new BotReply(text, Question: new BotSelectQuestion(nonce, messages.DelayPlaceholder, choices)), false).ConfigureAwait(false);
    }

    // The answer of a menu: the minutes of a postponement (done at once) or the warning time of a restart or stop (the
    // confirmation follows).
    private async Task ChoicePickedAsync(BotInteraction i, Ask ask, BotPolicy policy, BotMessages messages, IBotResponder responder, CancellationToken token)
    {
        if (ask.Command is BotCommand.RestartNow or BotCommand.Stop)
        {
            if (ask.Instance is null || i.Kind != BotInteractionKind.Select
                || (ask.Pending is { ChannelId: not 0 } asked && i.ChannelId != asked.ChannelId)
                || i.Values is not { Count: 1 } picked || !BotInputRules.TryParseDelayChoice(picked[0], ask.Command, out var delay))
            {
                await AnswerAsync(responder, new BotReply(messages.InvalidInput), true).ConfigureAwait(false);
                return;
            }
            await AskConfirmationAsync(i, ask, policy, messages, responder, token, update: true, chosenDelay: delay).ConfigureAwait(false);
            return;
        }
        await PostponePickedAsync(i, ask, policy, messages, responder, token).ConfigureAwait(false);
    }

    private async Task PostponePickedAsync(BotInteraction i, Ask ask, BotPolicy policy, BotMessages messages, IBotResponder responder, CancellationToken token)
    {
        if (ask.Command != BotCommand.Postpone || ask.Instance is not { } instance
            || i.Kind != BotInteractionKind.Select
            || (ask.Pending is { ChannelId: not 0 } asked && i.ChannelId != asked.ChannelId)
            || i.Values is not { Count: 1 } values || !BotInputRules.TryParsePostponeChoice(values[0], policy, out var minutes))
        {
            await AnswerAsync(responder, new BotReply(messages.InvalidInput), true).ConfigureAwait(false);
            return;
        }
        await RunAsync(i, ask, BotCommand.Postpone, instance, minutes, policy, messages, responder, true, token).ConfigureAwait(false);
    }

    private async Task AskConfirmationAsync(BotInteraction i, Ask ask, BotPolicy policy, BotMessages messages, IBotResponder responder,
        CancellationToken token, bool update = false, int? chosenDelay = null)
    {
        var command = ask.Command;
        if (!BotCommands.NeedsConfirmation(command))
        {
            await AnswerAsync(responder, new BotReply(messages.UnknownRequest), update).ConfigureAwait(false);
            return;
        }

        var instance = ask.Instance ?? -1;
        var delayMinutes = 0;
        if (command is BotCommand.RestartNow or BotCommand.Stop or BotCommand.Start)
        {
            if (!TryInstance(ask, messages, out var error))
            {
                await AnswerAsync(responder, error!, update).ConfigureAwait(false);
                return;
            }
            if (command != BotCommand.Start)
            {
                delayMinutes = (int)Math.Round(policy.ActionDelay.TotalMinutes);
                if (chosenDelay is { } chosen)
                    delayMinutes = chosen;
                else if (ask.RawDelay is { } rawDelay)
                {
                    if (rawDelay < 0 || rawDelay > BotInputRules.MaxDelayMinutes(command))
                    {
                        await AnswerAsync(responder, new BotReply(messages.InvalidInput), update).ConfigureAwait(false);
                        return;
                    }
                    delayMinutes = (int)rawDelay;
                }
            }
        }
        if (command == BotCommand.SetChannel && (i.ChannelId is not { } channel || !BotId.IsValid(channel)))
        {
            await AnswerAsync(responder, new BotReply(messages.InvalidInput), update).ConfigureAwait(false);
            return;
        }

        var server = instance >= 0 ? await ServerLabelAsync(instance, token).ConfigureAwait(false) : string.Empty;
        var warning = delayMinutes == 0 ? messages.Now : messages.FormatMinutes(delayMinutes);
        var text = command switch
        {
            BotCommand.RestartNow => string.Format(CultureInfo.InvariantCulture, messages.AskRestart, server, warning),
            BotCommand.Stop => string.Format(CultureInfo.InvariantCulture, messages.AskStop, server, warning),
            BotCommand.Start => string.Format(CultureInfo.InvariantCulture, messages.AskStart, server),
            BotCommand.Update => messages.AskUpdate,
            _ => messages.AskChannel
        };
        var nonce = _pending.Create(i.UserId, command, instance, _clock(), ConfirmLifetime, delayMinutes, i.ChannelId ?? 0);
        await AnswerAsync(responder, new BotReply(text, Question: new BotConfirmQuestion(nonce, messages.Confirm, messages.Cancel)), update).ConfigureAwait(false);
    }

    private async Task ConfirmedAsync(BotInteraction i, Ask ask, BotPolicy policy, BotMessages messages, IBotResponder responder, CancellationToken token)
    {
        var pending = ask.Pending!;
        // The answer only counts from the channel the question was asked in.
        if (pending.ChannelId != 0 && i.ChannelId != pending.ChannelId)
        {
            Audit(i, BotAuditResult.Denied, ask, "WrongChannel");
            await AnswerAsync(responder, new BotReply(messages.NoPermission), true).ConfigureAwait(false);
            return;
        }

        switch (pending.Command)
        {
            case BotCommand.SetChannel:
                if (!BotId.IsValid(pending.ChannelId))
                {
                    await AnswerAsync(responder, new BotReply(messages.InvalidInput), true).ConfigureAwait(false);
                    return;
                }
                await _settings.SetAdminChannelAsync(pending.ChannelId).ConfigureAwait(false);
                Audit(i, BotAuditResult.Executed, ask, "admin channel set");
                await AnswerAsync(responder, new BotReply(messages.DoneChannel), true).ConfigureAwait(false);
                await RefreshAsync(token).ConfigureAwait(false);
                return;
            case BotCommand.Update:
                await RunAsync(i, ask, BotCommand.Update, -1, 0, policy, messages, responder, true, token).ConfigureAwait(false);
                return;
            case BotCommand.RestartNow:
            case BotCommand.Stop:
            case BotCommand.Start:
                if (ask.Instance is not { } instance)
                {
                    await AnswerAsync(responder, new BotReply(messages.UnknownServer), true).ConfigureAwait(false);
                    return;
                }
                await RunAsync(i, ask, pending.Command, instance, pending.Minutes, policy, messages, responder, true, token).ConfigureAwait(false);
                return;
            default:
                await AnswerAsync(responder, new BotReply(messages.UnknownRequest), true).ConfigureAwait(false);
                return;
        }
    }

    // ---- doing it

    private async Task RunAsync(BotInteraction i, Ask ask, BotCommand command, int instance, int minutes, BotPolicy policy, BotMessages messages,
        IBotResponder responder, bool update, CancellationToken token, string? reason = null)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? "Discord" : "Discord: " + reason;
        var requestedMinutes = minutes;
        BotPostponeBudget.Snapshot budgetBefore = default;
        if (command == BotCommand.Postpone)
        {
            budgetBefore = _budget.Save(instance);
            // Postponing can not go on for ever: a chain of postponements ends at the longest one of the settings.
            var granted = await ReservePostponeAsync(instance, minutes, policy, token).ConfigureAwait(false);
            if (granted == 0)
            {
                // Nothing is postponed: what the reservation touched goes back.
                _budget.Restore(instance, budgetBefore);
                Audit(i, BotAuditResult.Throttled, ask, "PostponeLimit");
                await AnswerAsync(responder, new BotReply(messages.PostponeLimit), update).ConfigureAwait(false);
                return;
            }
            minutes = granted;
        }
        var delay = TimeSpan.FromMinutes(minutes);

        BotActionResult result;
        try
        {
            var action = command switch
            {
                BotCommand.Postpone => _actions.PostponeAsync(instance, minutes, text, token),
                BotCommand.Resume => _actions.ResumeAsync(instance, token),
                BotCommand.RestartNow => _actions.RestartAsync(instance, delay, "Discord", token),
                BotCommand.Stop => _actions.StopAsync(instance, delay, "Discord", token),
                BotCommand.Start => _actions.StartAsync(instance, token),
                BotCommand.Update => _actions.UpdateAsync(token),
                BotCommand.CheckUpdates => _actions.CheckUpdatesAsync(token),
                _ => Task.FromResult(new BotActionResult(BotActionOutcome.Failed, "not an action"))
            };

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
            var finished = await Task.WhenAny(action, Task.Delay(ActionTimeout, timer.Token)).ConfigureAwait(false);
            timer.Cancel();
            if (finished != action)
            {
                // Takes long (e.g. the update): answer now, the action carries on and is logged when it ends.
                _ = action.ContinueWith(t => LogLate(i, ask, t), TaskScheduler.Default);
                Audit(i, BotAuditResult.Executed, ask, "running");
                await AnswerAsync(responder, new BotReply(messages.Running), update).ConfigureAwait(false);
                return;
            }
            result = await action.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Discord action {Command} failed", command);
            result = new BotActionResult(BotActionOutcome.Failed, "exception");
        }

        // Refused (busy, not running, failed): nothing was postponed, so none of the budget is used.
        if (command == BotCommand.Postpone && !result.Success) _budget.Restore(instance, budgetBefore);
        Audit(i, result.Success ? BotAuditResult.Executed : BotAuditResult.Failed, ask, $"{result.Outcome} {result.Detail}".Trim());
        var answer = ResultText(command, instance >= 0 ? await ServerLabelAsync(instance, token).ConfigureAwait(false) : string.Empty, minutes, result, messages);
        if (command == BotCommand.Postpone && result.Success && minutes < requestedMinutes)
            answer = string.Format(CultureInfo.InvariantCulture, messages.PostponedLimited, messages.FormatMinutes(minutes));
        await AnswerAsync(responder, new BotReply(answer), update).ConfigureAwait(false);
        if (result.Success) await RefreshAsync(token).ConfigureAwait(false);
    }

    private async Task<int> ReservePostponeAsync(int instance, int minutes, BotPolicy policy, CancellationToken token)
    {
        var statuses = await _actions.GetStatusesAsync(token).ConfigureAwait(false);
        var status = statuses.FirstOrDefault(x => x.Instance == instance);
        var now = _clock();
        // A pending stop is moved back from its end (the action adds the minutes to the time that is left), not from now:
        // the time left counts against the chain like the minutes do, or postponing a stop again and again adds up for ever.
        var left = status is { Pending: BotPendingAction.Stop, PendingAtUtc: { } end } && end > now
            ? (int)Math.Ceiling((end - now).TotalMinutes)
            : 0;
        // A server that is not running or not known is refused by the action, which gives the reservation back.
        var granted = _budget.Reserve(instance, status?.StartedUtc, minutes + left, policy.MaxPause, now);
        return Math.Max(0, granted - left);
    }

    private void LogLate(BotInteraction i, Ask ask, Task<BotActionResult> task)
    {
        try
        {
            // Reading the exception marks it as observed.
            var detail = task.IsCompletedSuccessfully ? $"finished {task.Result.Outcome} {task.Result.Detail}".Trim() : $"finished with an error ({task.Exception?.GetBaseException().GetType().Name})";
            Audit(i, task.IsCompletedSuccessfully && task.Result.Success ? BotAuditResult.Executed : BotAuditResult.Failed, ask, detail);
        }
        catch (Exception)
        {
            // The audit log never throws; a faulted task is observed above.
        }
    }

    private static string ResultText(BotCommand command, string server, int minutes, BotActionResult result, BotMessages messages)
    {
        return result.Outcome switch
        {
            BotActionOutcome.Done => command switch
            {
                BotCommand.Postpone => string.Format(CultureInfo.InvariantCulture, messages.Postponed, messages.FormatMinutes(minutes)),
                BotCommand.Resume => messages.Resumed,
                BotCommand.RestartNow => string.Format(CultureInfo.InvariantCulture, messages.DoneRestart, server),
                BotCommand.Stop => string.Format(CultureInfo.InvariantCulture, messages.DoneStop, server),
                BotCommand.Start => string.Format(CultureInfo.InvariantCulture, messages.DoneStart, server),
                BotCommand.Update => messages.DoneUpdate,
                BotCommand.CheckUpdates => string.Format(CultureInfo.InvariantCulture, messages.DoneCheckUpdates, result.Updates?.OutdatedMods ?? 0,
                    result.Updates?.ServerUpdate == true ? messages.ServerNewVersion : messages.ServerCurrent),
                _ => messages.Failed
            },
            BotActionOutcome.NotRunning => messages.NotRunning,
            BotActionOutcome.AlreadyRunning => messages.AlreadyRunning,
            BotActionOutcome.NothingPending => messages.NothingPending,
            BotActionOutcome.Busy => messages.Busy,
            _ => messages.Failed
        };
    }

    // ---- helpers

    /// <summary>
    /// How a server is named in the answers: the name of the server (the first field of its save), the name of the save
    /// when that kept its default, the number when the launcher does not answer.
    /// </summary>
    private async Task<string> ServerLabelAsync(int instance, CancellationToken token)
    {
        var number = instance.ToString(CultureInfo.InvariantCulture);
        try
        {
            var statuses = await _actions.GetStatusesAsync(token).ConfigureAwait(false);
            var name = statuses.FirstOrDefault(x => x.Instance == instance)?.Name;
            return string.IsNullOrWhiteSpace(name) || name == "-" ? number : name;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return number;
        }
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        var after = AfterAction;
        if (after is null) return;
        try
        {
            await after(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Refreshing the status messages after an action failed");
        }
    }

    private void Audit(BotInteraction i, BotAuditResult result, Ask ask, string detail)
    {
        try
        {
            _audit.Write(new BotAuditEntry(_clock(), result, i.UserId, i.UserName ?? string.Empty, i.GuildId ?? 0, i.ChannelId ?? 0,
                ask.Unknown ? null : ask.Command, ask.Parameters, detail));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Writing the Discord audit log failed");
        }
    }

    private static Task AnswerAsync(IBotResponder responder, BotReply reply, bool update) =>
        update ? responder.UpdateAsync(reply) : responder.ReplyAsync(reply);

    private static async Task TryAnswerAsync(IBotResponder responder, BotReply reply, bool update)
    {
        try
        {
            await AnswerAsync(responder, reply, update).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing more can be done for this interaction.
        }
    }
}
