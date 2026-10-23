namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Everything that happens after Discord delivered a command, button click or menu choice, without any Discord type:
/// gate (<see cref="BotGuard"/>), questions, the action through <see cref="IBotActions"/>, audit, answer. The Discord
/// layer only converts to <see cref="BotInteraction"/> and renders the replies.
/// </summary>
public sealed class BotInteractionHandler
{
    public BotInteractionHandler(IBotActions actions, BotGuard guard, BotPendingActions pending, BotAuditLog audit,
        IBotSettings settings, IBotChannelClient? channel = null, Func<DateTime>? clock = null)
    {
        throw new NotImplementedException();
    }

    /// <summary>Never throws. Whatever goes wrong is answered with a neutral message and logged.</summary>
    public Task HandleAsync(BotInteraction interaction, IBotResponder responder, CancellationToken token)
        => throw new NotImplementedException();

    /// <summary>The slash commands the Discord layer registers for the server (names and options, no logic).</summary>
    public static IReadOnlyList<BotSlashCommand> SlashCommands => throw new NotImplementedException();
}

public enum BotOptionType { Integer, String }

public sealed record BotSlashOption(string Name, string Description, BotOptionType Type, bool Required, long? Min = null, long? Max = null);

/// <summary>A slash command or sub command: <c>Path</c> is "status", "restart postpone", "bot channel set".</summary>
public sealed record BotSlashCommand(string Path, string Description, IReadOnlyList<BotSlashOption> Options);

/// <summary>
/// Keeps the public status message of every server instance in the admin channel up to date: posts it, edits it when
/// it changed (at most every few seconds), posts a new one when it was deleted.
/// </summary>
public sealed class BotStatusBoard
{
    public BotStatusBoard(IBotActions actions, IBotChannelClient channel, IBotSettings settings, Func<DateTime>? clock = null)
    {
        throw new NotImplementedException();
    }

    /// <summary>Call every few seconds and after an action. Never throws.</summary>
    public Task RefreshAsync(CancellationToken token, bool force = false) => throw new NotImplementedException();

    /// <summary>Forget the posted messages (the admin channel changed).</summary>
    public void Reset() => throw new NotImplementedException();
}
