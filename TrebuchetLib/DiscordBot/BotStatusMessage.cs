namespace TrebuchetLib.DiscordBot;

public enum BotServerState { Stopped, Starting, Online, Stopping, Crashed }

/// <summary>What is going to happen to a running server. Decides the label of the postpone button.</summary>
public enum BotPendingAction { None, Restart, Stop, Update }

/// <summary>The state of a server instance as the bot shows it. No paths, addresses or passwords.</summary>
/// <param name="Restarting">The server is going down to be started again (a restart is on its way), not to stay down.</param>
public sealed record BotServerStatus(
    int Instance,
    string Name,
    BotServerState State,
    int Players,
    int MaxPlayers,
    DateTime? StartedUtc,
    BotPendingAction Pending,
    DateTime? PendingAtUtc,
    DateTime? PausedUntilUtc,
    bool Restarting = false);

/// <summary>The colors of Discord for buttons: green, red, blue and grey are all it offers.</summary>
public enum BotButtonStyle { Success, Danger, Primary, Secondary }

/// <param name="Disabled">Shown greyed out, it can not be clicked (the server is changing its state).</param>
public sealed record BotButton(string CustomId, string Label, BotButtonStyle Style, BotCommand Command, bool Disabled = false);

public sealed record BotField(string Name, string Value, bool Inline = true);

/// <summary>The embed of a server: color by state, a few fields and the buttons that fit the state.</summary>
/// <param name="UpdatedUtc">When the state was read. Discord shows it next to the footer: a stale time means the bot is not checking.</param>
public sealed record BotStatusMessage(
    string Title,
    uint Color,
    IReadOnlyList<BotField> Fields,
    IReadOnlyList<BotButton> Buttons,
    string Footer,
    DateTime? UpdatedUtc = null);

/// <summary>
/// Builds the status message of a server instance. This is plain data, Discord.Net turns it into the real embed. The
/// buttons always fit the state of the server, whatever the command switches say: the message is the same for everybody
/// and the super admin may do everything. A button is a convenience, the click is checked like every other request.
/// </summary>
public static class BotStatusMessageBuilder
{
    public const uint Green = 0x2ECC71;
    public const uint Yellow = 0xF1C40F;
    public const uint Red = 0xE74C3C;
    public const uint Blue = 0x3498DB;

    public static BotStatusMessage Build(BotServerStatus status, BotTexts texts, DateTime utcNow)
    {
        var fields = new List<BotField>
        {
            new(texts.StateField, StateText(status.State, texts))
        };
        if (status.State == BotServerState.Online)
            fields.Add(new BotField(texts.PlayersField, $"{Math.Max(0, status.Players)} / {Math.Max(0, status.MaxPlayers)}"));
        if (status.State is BotServerState.Online or BotServerState.Starting && status.StartedUtc is { } started)
            fields.Add(new BotField(texts.UptimeField, Relative(started)));
        if (status.Pending != BotPendingAction.None && status.State == BotServerState.Online)
        {
            var planned = PlannedText(status.Pending, texts);
            fields.Add(new BotField(texts.PlannedField,
                status.PendingAtUtc is { } at ? $"{planned} {Relative(at)}" : planned, false));
        }
        if (status.PausedUntilUtc is { } paused && paused > utcNow)
            fields.Add(new BotField(texts.PostponedField, Clock(paused), false));

        return new BotStatusMessage(
            $"{status.Name} · {texts.Instance} {status.Instance}",
            ColorOf(status, utcNow),
            fields,
            ButtonsOf(status, texts, utcNow),
            texts.UpdatedFooter,
            utcNow);
    }

    public static uint ColorOf(BotServerStatus status, DateTime utcNow)
    {
        switch (status.State)
        {
            case BotServerState.Stopped:
            case BotServerState.Crashed:
                return Red;
            case BotServerState.Starting:
            case BotServerState.Stopping:
                return Yellow;
            default:
                // Postponed wins over pending: the launcher still reports the restart as pending while it is put off.
                if (status.PausedUntilUtc is { } paused && paused > utcNow) return Blue;
                return status.Pending != BotPendingAction.None ? Yellow : Green;
        }
    }

    private static List<BotButton> ButtonsOf(BotServerStatus status, BotTexts texts, DateTime utcNow)
    {
        var buttons = new List<BotButton>();
        void Add(BotCommand command, string label, BotButtonStyle style, bool disabled = false) =>
            buttons.Add(new BotButton(BotCustomId.ForAction(command, status.Instance), label, style, command, disabled));

        switch (status.State)
        {
            case BotServerState.Stopped:
            case BotServerState.Crashed:
                Add(BotCommand.Start, texts.Start, BotButtonStyle.Success);
                break;
            case BotServerState.Starting:
            case BotServerState.Online:
                Add(BotCommand.RestartNow, texts.Restart, BotButtonStyle.Primary);
                Add(BotCommand.Stop, texts.Stop, BotButtonStyle.Danger);
                if (status.Pending != BotPendingAction.None)
                    Add(BotCommand.Postpone, PostponeLabel(status.Pending, texts), BotButtonStyle.Secondary);
                if (status.PausedUntilUtc is { } paused && paused > utcNow)
                    Add(BotCommand.Resume, texts.Resume, BotButtonStyle.Secondary);
                break;
            case BotServerState.Stopping:
                // Nothing to click until it is down: the button of the next step waits greyed out.
                Add(BotCommand.Start, texts.Start, BotButtonStyle.Success, disabled: true);
                break;
        }
        // Not about this server alone: the check asks Steam about the mods and the server build and installs nothing.
        Add(BotCommand.CheckUpdates, texts.CheckUpdates, BotButtonStyle.Secondary);
        return buttons;
    }

    private static string PostponeLabel(BotPendingAction pending, BotTexts texts) => pending switch
    {
        BotPendingAction.Stop => texts.PostponeStop,
        BotPendingAction.Update => texts.PostponeUpdate,
        _ => texts.PostponeRestart
    };

    private static string PlannedText(BotPendingAction pending, BotTexts texts) => pending switch
    {
        BotPendingAction.Stop => texts.PlannedStop,
        BotPendingAction.Update => texts.PlannedUpdate,
        _ => texts.PlannedRestart
    };

    internal static string StateText(BotServerState state, BotTexts texts) => state switch
    {
        BotServerState.Online => texts.StateOnline,
        BotServerState.Starting => texts.StateStarting,
        BotServerState.Stopping => texts.StateStopping,
        BotServerState.Crashed => texts.StateCrashed,
        _ => texts.StateStopped
    };

    // Discord shows these in the time zone and language of every reader.
    private static string Relative(DateTime utc) => $"<t:{new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds()}:R>";
    private static string Clock(DateTime utc) => $"<t:{new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds()}:t>";
}
