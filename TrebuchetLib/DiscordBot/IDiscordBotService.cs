namespace TrebuchetLib.DiscordBot;

public enum DiscordBotState { Stopped, Starting, Connected, Stopping, Error }

public enum DiscordBotProblem
{
    None,
    NoToken,
    InvalidSettings,
    /// <summary>Another Trebuchet already runs a bot with this token.</summary>
    TokenInUse,
    LoginFailed,
    GuildNotFound,
    ConnectionLost,
    Unknown,
    /// <summary>Connected, but the slash commands could not be registered (e.g. the invite lacks <c>applications.commands</c>).</summary>
    CommandsNotRegistered
}

public enum BotCheckKind { Token, Settings, Guild, Roles, EveryoneRole, OtherGuilds, AdministratorPermission, Channel, Connection }

public enum BotCheckStatus { Ok, Warning, Error }

/// <param name="Detail">Short, no token. The panel shows its own text per <see cref="BotCheckKind"/> and may append this.</param>
public sealed record BotCheckItem(BotCheckKind Kind, BotCheckStatus Status, string Detail = "");

/// <summary>
/// The Discord bot as the panel and the headless host see it. Starting never throws: the outcome is in
/// <see cref="State"/> and <see cref="Problem"/>. Nothing it exposes contains the token.
/// </summary>
public interface IDiscordBotService : IAsyncDisposable
{
    DiscordBotState State { get; }
    DiscordBotProblem Problem { get; }
    /// <summary>The problems of the settings found by the last start or <see cref="ApplySettingsAsync"/>.</summary>
    IReadOnlyList<BotConfigIssue> Issues { get; }
    string? BotName { get; }
    string? GuildName { get; }
    /// <summary>Gateway latency in milliseconds, -1 when unknown.</summary>
    int LatencyMs { get; }
    DateTime? ConnectedSinceUtc { get; }

    /// <summary>Raised on any thread when the state, the problem or the connection details changed.</summary>
    event EventHandler? StateChanged;

    Task StartAsync(CancellationToken token);
    Task StopAsync();

    /// <summary>
    /// The panel saved the settings: rebuild the policy and apply it without a reconnect (users, roles, commands, limits).
    /// An invalid result keeps the bot running but allows nobody anything.
    /// </summary>
    Task ApplySettingsAsync();

    /// <summary>The "check security" list of the panel. Connects to Discord only when the bot runs.</summary>
    Task<IReadOnlyList<BotCheckItem>> CheckSecurityAsync(CancellationToken token);
}
