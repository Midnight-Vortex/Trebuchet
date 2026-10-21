using TrebuchetLib;

/// <summary>
/// Server update restarts: a Steam lookup without an answer (build 0) restarted the servers after every check, and an
/// update that could not be installed restarted them again and again.
/// </summary>
static class ServerUpdateChecks
{
    public static int Run(DateTime t0)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        var serverStart = t0.AddDays(-2);
        var tracker = new ServerUpdateTracker();
        Check(!tracker.Report(0, 100, false, t0) && !tracker.NeedUpdate && !tracker.ShouldRestart(serverStart),
            "No answer from Steam is no server update");
        Check(tracker.Report(100, 100, false, t0) && !tracker.NeedUpdate, "Same build needs no update");

        var detected = t0.AddMinutes(5);
        Check(tracker.Report(200, 100, false, detected) && tracker.NeedUpdate && tracker.ShouldRestart(serverStart),
            "A new build restarts a server started before it was known");
        var restarted = detected.AddMinutes(20);
        Check(tracker.Report(200, 100, false, restarted.AddMinutes(5)) && tracker.SinceUtc == detected
              && !tracker.ShouldRestart(restarted), "A server started after the update is not restarted again when it was not installed");
        Check(!tracker.Report(0, 100, false, restarted.AddMinutes(10)) && tracker.NeedUpdate && tracker.SteamBuildId == 200,
            "No answer from Steam keeps the pending update");
        Check(tracker.ReportNotInstalled(0) && !tracker.ReportNotInstalled(0) && tracker.ReportNotInstalled(1),
            "The missing update is logged once per instance");

        var newer = restarted.AddHours(1);
        Check(tracker.Report(300, 100, false, newer) && tracker.SinceUtc == newer && tracker.ShouldRestart(restarted)
              && tracker.ReportNotInstalled(0), "A newer build restarts the servers once more");
        Check(tracker.Report(300, 300, false, newer.AddMinutes(30)) && !tracker.NeedUpdate && !tracker.ShouldRestart(serverStart),
            "An installed update needs no restart");

        var missing = new ServerUpdateTracker();
        Check(missing.Report(0, 0, true, t0) && missing.NeedUpdate && missing.ShouldRestart(serverStart)
              && !missing.ShouldRestart(t0.AddMinutes(1)), "Missing instances need one update restart, also without Steam");

        // A mod Steam sent no file for cannot be downloaded: it never counts as outdated.
        Check(!new PublishedMod(new SteamWorksWebAPI.PublishedFile { PublishedFileID = 1 }, UGCFileStatus.Default(1)).HasWorkshopFile
              && new PublishedMod(new SteamWorksWebAPI.PublishedFile { PublishedFileID = 1, HcontentFile = "123" }, UGCFileStatus.Default(1)).HasWorkshopFile,
            "Mods without a workshop file are recognized");

        return count;
    }
}
