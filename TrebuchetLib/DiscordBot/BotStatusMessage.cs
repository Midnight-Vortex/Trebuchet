namespace TrebuchetLib.DiscordBot;

public enum BotServerState { Stopped, Starting, Online, Stopping, Crashed }

/// <summary>What is going to happen to a running server. Decides the label of the postpone button.</summary>
public enum BotPendingAction { None, Restart, Stop, Update }

/// <summary>The state of a server instance as the bot shows it. No paths, addresses or passwords.</summary>
public sealed record BotServerStatus(
    int Instance,
    string Name,
    BotServerState State,
    int Players,
    int MaxPlayers,
    DateTime? StartedUtc,
    BotPendingAction Pending,
    DateTime? PendingAtUtc,
    DateTime? PausedUntilUtc);

/// <summary>The colors of Discord for buttons: green, red, blue and grey are all it offers.</summary>
public enum BotButtonStyle { Success, Danger, Primary, Secondary }

public sealed record BotButton(string CustomId, string Label, BotButtonStyle Style, BotCommand Command);

public sealed record BotField(string Name, string Value, bool Inline = true);

/// <summary>The embed of a server: color by state, a few fields and the buttons that fit the state.</summary>
public sealed record BotStatusMessage(
    string Title,
    uint Color,
    IReadOnlyList<BotField> Fields,
    IReadOnlyList<BotButton> Buttons,
    string Footer);

/// <summary>
/// Builds the status message of a server instance. This is plain data, Discord.Net turns it into the real embed. A
/// button only shows when its command is switched on and it fits the state: it is a convenience, the click is checked
/// like every other request.
/// </summary>
public static class BotStatusMessageBuilder
{
    public const uint Green = 0x2ECC71;
    public const uint Yellow = 0xF1C40F;
    public const uint Red = 0xE74C3C;
    public const uint Blue = 0x3498DB;

    public static BotStatusMessage Build(BotServerStatus status, BotPolicy policy, BotTexts texts, DateTime utcNow)
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
            ButtonsOf(status, policy, texts, utcNow),
            texts.UpdatedFooter);
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
                if (status.Pending != BotPendingAction.None) return Yellow;
                return status.PausedUntilUtc is { } paused && paused > utcNow ? Blue : Green;
        }
    }

    private static List<BotButton> ButtonsOf(BotServerStatus status, BotPolicy policy, BotTexts texts, DateTime utcNow)
    {
        var buttons = new List<BotButton>();
        void Add(BotCommand command, string label, BotButtonStyle style)
        {
            if (policy.IsCommandEnabled(command))
                buttons.Add(new BotButton(BotCustomId.ForAction(command, status.Instance), label, style, command));
        }

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
            // Stopping: nothing to click until it is down.
        }
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

    private static string StateText(BotServerState state, BotTexts texts) => state switch
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
