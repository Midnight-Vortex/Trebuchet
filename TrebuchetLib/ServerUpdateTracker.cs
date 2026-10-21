namespace TrebuchetLib;

/// <summary>
/// Decides when running servers restart for a server update (24/7 operation). A Steam lookup without an answer
/// (connection lost, Steam maintenance) is no update. A server started after a start that could install the update
/// (no other server running) is not restarted again for it: when the install failed, restarting repeated after every
/// check. Starts that could not install it (another server ran) lead to a new round, at most
/// <see cref="MaxRounds"/> per build, so an update is not given up because of one busy server.
/// </summary>
public sealed class ServerUpdateTracker
{
    private readonly HashSet<int> _notInstalledReported = [];

    public const int MaxRounds = 3;

    public bool NeedUpdate { get; private set; }
    /// <summary>Last pre-launch update that could install a server update (no other server ran or started).</summary>
    public DateTime LastInstallAttemptUtc { get; private set; }
    /// <summary>Restart rounds started for the pending update.</summary>
    public int RoundsStarted { get; private set; }
    public uint SteamBuildId { get; private set; }
    /// <summary>When the pending update became known.</summary>
    public DateTime SinceUtc { get; private set; }

    /// <summary>
    /// Takes the result of an update check. Missing instances always need an update, otherwise a Steam build of 0
    /// (no answer) keeps the previous state.
    /// </summary>
    /// <returns>False when the check gave no answer.</returns>
    public bool Report(uint steamBuildId, ulong installedBuildId, bool instancesMissing, DateTime utcNow)
    {
        if (steamBuildId == 0 && !instancesMissing) return false;

        var build = steamBuildId == 0 ? SteamBuildId : steamBuildId;
        var needUpdate = instancesMissing || build != installedBuildId;
        if (needUpdate && (!NeedUpdate || build != SteamBuildId))
        {
            SinceUtc = utcNow;
            RoundsStarted = 0;
            _notInstalledReported.Clear();
        }

        NeedUpdate = needUpdate;
        SteamBuildId = build;
        return true;
    }

    /// <summary>True when a server started at <paramref name="serverStartUtc"/> has to restart for the update.</summary>
    public bool ShouldRestart(DateTime serverStartUtc)
    {
        if (!NeedUpdate || RoundsStarted >= MaxRounds) return false;
        // No start could install it since it became known: every server restarts for it.
        if (LastInstallAttemptUtc < SinceUtc) return true;
        return serverStartUtc < LastInstallAttemptUtc;
    }

    /// <summary>A start ran the pre-launch server update with no other server running.</summary>
    public void ReportInstallAttempt(DateTime utcNow) => LastInstallAttemptUtc = utcNow;

    public void ReportRoundStarted() => RoundsStarted++;

    /// <summary>True once per pending update and instance, to log that its start did not install the update.</summary>
    public bool ReportNotInstalled(int instance) => _notInstalledReported.Add(instance);
}
