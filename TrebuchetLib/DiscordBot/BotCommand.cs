namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Everything the bot can do. There is deliberately nothing free-form: no RCon, no scripts, no files, no mods, no
/// settings, no permissions. A command that is not in this list cannot be triggered from Discord.
/// </summary>
public enum BotCommand
{
    /// <summary>Show the state of the servers.</summary>
    Status,
    /// <summary>Postpone the pending restart, stop or update by some minutes.</summary>
    Postpone,
    /// <summary>End a postponement: the automatic restarts run again.</summary>
    Resume,
    /// <summary>Cancel the restart or stop that is counting down: the players are told it does not happen.</summary>
    Cancel,
    /// <summary>Restart a running server now (with the warnings of its save).</summary>
    RestartNow,
    /// <summary>Stop a running server.</summary>
    Stop,
    /// <summary>Start a stopped server.</summary>
    Start,
    /// <summary>Check for updates and install them.</summary>
    Update,
    /// <summary>Ask Steam whether mods or the server need an update. Installs nothing.</summary>
    CheckUpdates,
    /// <summary>Make the channel of the command the only channel the bot listens to.</summary>
    SetChannel
}

/// <summary>Who may use a command. Each level includes the ones above it.</summary>
public enum BotLevel
{
    /// <summary>Every authorized user.</summary>
    Read = 0,
    /// <summary>Every authorized user.</summary>
    Manage = 1,
    /// <summary>Every authorized user whose command is switched on (the super admin always), with a confirmation.</summary>
    Critical = 2,
    /// <summary>Only the super admin, always, with a confirmation.</summary>
    SuperAdmin = 3
}

public static class BotCommands
{
    /// <summary>
    /// The level of a command is fixed in code. The settings can only switch a command on or off for the other users:
    /// the super admin may use every command.
    /// </summary>
    public static BotLevel LevelOf(BotCommand command) => command switch
    {
        BotCommand.Status => BotLevel.Read,
        BotCommand.Postpone or BotCommand.Resume or BotCommand.Cancel or BotCommand.CheckUpdates => BotLevel.Manage,
        BotCommand.RestartNow or BotCommand.Stop => BotLevel.Critical,
        BotCommand.Start or BotCommand.Update or BotCommand.SetChannel => BotLevel.SuperAdmin,
        _ => BotLevel.SuperAdmin
    };

    /// <summary>Critical and super admin commands run only after the confirmation button.</summary>
    public static bool NeedsConfirmation(BotCommand command) => LevelOf(command) >= BotLevel.Critical;

    /// <summary>Counts against the limit of critical actions.</summary>
    public static bool IsLimited(BotCommand command) => command is BotCommand.RestartNow or BotCommand.Stop or BotCommand.Start or BotCommand.Update;

    /// <summary>Commands that are on in a new settings file. Everything else is off until someone switches it on.</summary>
    public static IReadOnlyList<BotCommand> EnabledByDefault { get; } =
        [BotCommand.Status, BotCommand.Postpone, BotCommand.Resume, BotCommand.Cancel, BotCommand.CheckUpdates, BotCommand.SetChannel];
}
