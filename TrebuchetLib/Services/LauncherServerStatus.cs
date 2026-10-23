namespace TrebuchetLib.Services;

/// <summary>
/// The state of a server instance as <see cref="Launcher.GetServerStatuses"/> sees it at one moment, for remote control.
/// Plain values only: no paths, addresses or passwords, nothing that reaches back into the launcher.
/// </summary>
public sealed record LauncherServerStatus
{
    public required int Instance { get; init; }
    /// <summary>Name of the save selected for the instance, empty when none is selected.</summary>
    public required string SaveName { get; init; }
    /// <summary>The server files of the instance are installed.</summary>
    public bool Installed { get; init; }
    /// <summary>State of the server process, null when none runs.</summary>
    public ProcessState? State { get; init; }
    public int Players { get; init; }
    public int MaxPlayers { get; init; }
    public DateTime? StartedUtc { get; init; }
    /// <summary>No server process yet, but one is on its way (starting sequence, launch, start held for an update).</summary>
    public bool Starting { get; init; }
    /// <summary>The server stopped, backup and scripts after the stop still run.</summary>
    public bool ShuttingDown { get; init; }
    /// <summary>The last server process crashed and no new one runs.</summary>
    public bool Crashed { get; init; }
    /// <summary>A sequence runs: a countdown, or a starting or stopping sequence of the save.</summary>
    public bool SequenceRunning { get; init; }
    /// <summary>A stop or restart countdown with warnings to the players runs.</summary>
    public bool CountdownRunning { get; init; }
    /// <summary>The running countdown ends with a restart (otherwise with a stop).</summary>
    public bool CountdownRestarts { get; init; }
    /// <summary>When the running countdown stops or restarts the server, unless it empties before.</summary>
    public DateTime? CountdownEndsUtc { get; init; }
    /// <summary>An automatic restart is on its way (countdown or the restart itself).</summary>
    public bool RestartRequested { get; init; }
    /// <summary>A restart was postponed by a pause: it runs when the pause is over.</summary>
    public bool RestartPostponed { get; init; }
    /// <summary>The server takes part in a common restart for an update.</summary>
    public bool InUpdateRound { get; init; }
    /// <summary>A server or mod update restarts the server with one of the next update checks.</summary>
    public bool UpdatePending { get; init; }
    /// <summary>When the restart schedule of the save restarts the server next (warnings included), within a day.</summary>
    public DateTime? NextScheduledRestartUtc { get; init; }
    /// <summary>The automatic restarts are paused until then.</summary>
    public DateTime? PausedUntilUtc { get; init; }
}
