namespace TrebuchetLib.DiscordBot;

public enum BotActionOutcome
{
    Done,
    /// <summary>The server is not running (stop, restart, postpone).</summary>
    NotRunning,
    /// <summary>The server already runs (start).</summary>
    AlreadyRunning,
    /// <summary>Nothing is pending that could be postponed or released.</summary>
    NothingPending,
    /// <summary>Something else is going on (update running, sequence running, instance locked).</summary>
    Busy,
    Failed
}

/// <param name="Detail">Short text for the log. Never a path, password or token.</param>
public sealed record BotActionResult(BotActionOutcome Outcome, string Detail = "")
{
    public bool Success => Outcome == BotActionOutcome.Done;
}

/// <summary>
/// The only way from the bot into the launcher: a fixed list of actions, nothing free-form. Implementations run every
/// call on the thread the launcher uses (its tables are not thread safe) and never throw: a failure is a result.
/// All numbers have been checked by <see cref="BotInputRules"/> before they get here.
/// </summary>
public interface IBotActions
{
    /// <summary>Number of server instances of the installation.</summary>
    int InstanceCount { get; }

    /// <summary>One entry per instance, an immutable copy. Nothing in it can reach back into the launcher.</summary>
    Task<IReadOnlyList<BotServerStatus>> GetStatusesAsync(CancellationToken token);

    /// <summary>
    /// Postpones what is pending for the instance (restart, stop or update) by the minutes: pauses the automatic restarts
    /// (<c>Launcher.PauseAutomaticRestarts</c>, which also tells the players) or re-plans a pending stop.
    /// </summary>
    Task<BotActionResult> PostponeAsync(int instance, int minutes, string reason, CancellationToken token);

    /// <summary>Ends a postponement (<c>Launcher.ResumeAutomaticRestarts</c>).</summary>
    Task<BotActionResult> ResumeAsync(int instance, CancellationToken token);

    /// <summary>Restart with the warnings of the save during <paramref name="delay"/> (zero: now).</summary>
    Task<BotActionResult> RestartAsync(int instance, TimeSpan delay, string reason, CancellationToken token);

    /// <summary>Stop with the warnings of the save during <paramref name="delay"/> (zero: now).</summary>
    Task<BotActionResult> StopAsync(int instance, TimeSpan delay, string reason, CancellationToken token);

    Task<BotActionResult> StartAsync(int instance, CancellationToken token);

    /// <summary>Check for server and mod updates and install them (<c>Launcher.PerformCatapultUpdates</c>).</summary>
    Task<BotActionResult> UpdateAsync(CancellationToken token);
}
