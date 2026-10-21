namespace TrebuchetLib;

/// <summary>
/// Decides when running servers restart for a server update (24/7 operation). A Steam lookup without an answer
/// (connection lost, Steam maintenance) is no update, and a server started after the update became known already ran
/// the pre-launch update: restarting it again for the same update repeated after every check when the update could not
/// be installed. Such a server installs the update with its next restart.
/// </summary>
public sealed class ServerUpdateTracker
{
    private readonly HashSet<int> _notInstalledReported = [];

    public bool NeedUpdate { get; private set; }
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
            _notInstalledReported.Clear();
        }

        NeedUpdate = needUpdate;
        SteamBuildId = build;
        return true;
    }

    /// <summary>True when a server started at <paramref name="serverStartUtc"/> has to restart for the update.</summary>
    public bool ShouldRestart(DateTime serverStartUtc) => NeedUpdate && serverStartUtc < SinceUtc;

    /// <summary>True once per pending update and instance, to log that its start did not install the update.</summary>
    public bool ReportNotInstalled(int instance) => _notInstalledReported.Add(instance);
}
