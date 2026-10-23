using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using tot_lib.OsSpecific;
using TrebuchetLib;
using TrebuchetLib.DiscordBot;
using TrebuchetLib.OsSpecific;
using TrebuchetLib.Processes;
using TrebuchetLib.Sequences;
using TrebuchetLib.Services;

/// <summary>
/// The bridge from the Discord bot into the launcher: what the status shows (state, what is about to happen and when),
/// that every action goes through the launcher thread and work given up never runs, and what the actions do to a real
/// launcher with stand-in server processes (no Conan server needed).
/// </summary>
static class LauncherBotActionsChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        StatusChecks(Check);
        DailyRestartChecks(Check);
        await LauncherChecks(Check);
        return count;
    }

    private static void StatusChecks(Action<bool, string> check)
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var started = now.AddHours(-3);
        LauncherServerStatus Process(ProcessState state) => new()
        {
            Instance = 1, SaveName = "Exiles", Installed = true, State = state, Players = 7, MaxPlayers = 40, StartedUtc = started
        };
        BotServerStatus Show(LauncherServerStatus status) => LauncherBotActions.ToBotStatus(status, now);

        var online = Show(Process(ProcessState.ONLINE) with { PausedUntilUtc = now.AddMinutes(30) });
        check(online is { Instance: 1, Name: "Exiles", State: BotServerState.Online, Players: 7, MaxPlayers: 40, Pending: BotPendingAction.None }
              && online.StartedUtc == started && online.PausedUntilUtc == now.AddMinutes(30),
            "An online server shows its players, start and pause: " + online);
        check(Show(Process(ProcessState.ONLINE) with { PausedUntilUtc = now.AddSeconds(-1) }).PausedUntilUtc is null,
            "A pause that is over is not shown");
        check(Show(Process(ProcessState.RUNNING)) is { State: BotServerState.Starting, Players: 0, MaxPlayers: 0 } starting
              && starting.StartedUtc == started && Show(Process(ProcessState.NEW)).State == BotServerState.Starting,
            "A server process that does not answer yet is starting, without players");
        check(Show(Process(ProcessState.STOPPING) with { CountdownRunning = true, CountdownEndsUtc = now.AddMinutes(5) })
                is { State: BotServerState.Stopping, Players: 0, Pending: BotPendingAction.None },
            "A stopping server shows no players and nothing pending");
        check(Show(Process(ProcessState.CRASHED)).State == BotServerState.Crashed && Show(Process(ProcessState.FAILED)).State == BotServerState.Crashed
              && Show(Process(ProcessState.STOPPED)).State == BotServerState.Stopped, "Crashed and stopped server processes");

        var down = new LauncherServerStatus
        {
            Instance = 0, SaveName = "Exiles", StartedUtc = started, UpdatePending = true, NextScheduledRestartUtc = now.AddMinutes(5)
        };
        check(Show(down) is { State: BotServerState.Stopped, StartedUtc: null, Pending: BotPendingAction.None, PendingAtUtc: null },
            "Without a server process the server is stopped and nothing is pending");
        check(Show(down with { Starting = true, Crashed = true }).State == BotServerState.Starting,
            "A server on its way up (also after a crash) is starting");
        check(Show(down with { ShuttingDown = true, Crashed = true }).State == BotServerState.Stopping,
            "While backup and scripts run after the stop the server is still stopping");
        check(Show(down with { Crashed = true }).State == BotServerState.Crashed, "A crashed server that is not restarted stays crashed");

        // What is about to happen, and when.
        var end = now.AddMinutes(12);
        var countdown = Process(ProcessState.ONLINE) with
        {
            SequenceRunning = true, CountdownRunning = true, CountdownEndsUtc = end, NextScheduledRestartUtc = now.AddMinutes(30)
        };
        check(Show(countdown with { CountdownRestarts = true, RestartRequested = true }) is { Pending: BotPendingAction.Restart } restart
              && restart.PendingAtUtc == end, "A restart countdown is a pending restart at its end");
        check(Show(countdown with { CountdownRestarts = true, InUpdateRound = true }) is { Pending: BotPendingAction.Update } update
              && update.PendingAtUtc == end, "A restart countdown of an update round is a pending update");
        check(Show(countdown) is { Pending: BotPendingAction.Stop } stop && stop.PendingAtUtc == end
              && Show(Process(ProcessState.RUNNING) with { CountdownRunning = true, CountdownEndsUtc = end }).Pending == BotPendingAction.Stop,
            "A countdown without restart is a pending stop");
        check(Show(Process(ProcessState.ONLINE) with { RestartRequested = true, SequenceRunning = true })
                is { Pending: BotPendingAction.Restart, PendingAtUtc: null }, "A restart on its way without countdown is pending now");
        check(Show(Process(ProcessState.ONLINE) with { SequenceRunning = true, UpdatePending = true }).Pending == BotPendingAction.None,
            "During a sequence of the save nothing automatic is pending");
        var pauseEnd = now.AddMinutes(40);
        check(Show(Process(ProcessState.ONLINE) with { UpdatePending = true, RestartPostponed = true, PausedUntilUtc = pauseEnd })
                  is { Pending: BotPendingAction.Update } paused && paused.PendingAtUtc == pauseEnd
              && Show(Process(ProcessState.ONLINE) with { UpdatePending = true }) is { Pending: BotPendingAction.Update, PendingAtUtc: null },
            "A pending update restarts after the pause, without a pause with the next check");
        check(Show(Process(ProcessState.ONLINE) with { RestartPostponed = true, PausedUntilUtc = pauseEnd }) is { Pending: BotPendingAction.Restart } postponed
              && postponed.PendingAtUtc == pauseEnd, "A postponed restart runs when the pause is over");
        check(Show(Process(ProcessState.ONLINE) with { NextScheduledRestartUtc = now.AddMinutes(30) }) is { Pending: BotPendingAction.Restart } scheduled
              && scheduled.PendingAtUtc == now.AddMinutes(30)
              && Show(Process(ProcessState.ONLINE) with { NextScheduledRestartUtc = now + LauncherBotActions.ScheduledRestartLookAhead }).Pending == BotPendingAction.Restart
              && Show(Process(ProcessState.ONLINE) with { NextScheduledRestartUtc = now.AddMinutes(61) }).Pending == BotPendingAction.None,
            "Only a scheduled restart within the next hour is pending");

        check(Show(down with { SaveName = @"C:\Users\admin\Documents\Exiles" }).Name == "Exiles"
              && Show(down with { SaveName = "/home/admin/RP Server\n@everyone" }).Name == "RP Server @everyone"
              && Show(down with { SaveName = @"C:\" }).Name == "-", "The name never shows a path or a line break");
    }

    private static void DailyRestartChecks(Action<bool, string> check)
    {
        var day = new DateTime(2026, 10, 23);
        TimeSpan[] noon = [TimeSpan.FromHours(12)];
        DateTime? Next(IEnumerable<TimeSpan> times, DateTime now, DateTime? notBefore = null, bool todayFull = false)
            => Launcher.GetNextDailyRestart(times, now, notBefore ?? day, todayFull);

        check(Next(noon, day.AddHours(11.5)) == day.AddHours(12), "The next restart time of today");
        check(Next(noon, day.AddHours(12).AddMinutes(2)) == day.AddHours(12).AddMinutes(2)
              && Next(noon, day.AddHours(12).AddMinutes(5)) == day.AddHours(12).AddMinutes(5),
            "Within 5 minutes after the restart time the tick restarts at once");
        check(Next(noon, day.AddHours(12).AddMinutes(5).AddSeconds(1)) == day.AddDays(1).AddHours(12), "After the window: tomorrow");
        check(Next(noon, day.AddHours(11.5), day.AddHours(12).AddMinutes(3)) == day.AddHours(12).AddMinutes(3),
            "Uptime or pause ending within the window delays the restart");
        check(Next(noon, day.AddHours(11.5), day.AddHours(12).AddMinutes(10)) == day.AddDays(1).AddHours(12),
            "Uptime or pause ending after the window skips the restart time");
        check(Next(noon, day.AddHours(11.5), todayFull: true) == day.AddDays(1).AddHours(12), "Restarts of today used up: tomorrow");
        check(Next([TimeSpan.FromHours(18), TimeSpan.FromHours(6)], day.AddHours(7)) == day.AddHours(18), "The earliest of several times");
        var lateTime = new TimeSpan(23, 58, 0);
        check(Next([lateTime], day.AddHours(23).AddMinutes(59).AddSeconds(30)) is { } late && late.Date == day
              && Next([lateTime], day.AddDays(1).AddMinutes(1)) == day.AddDays(1) + lateTime,
            "The window ends at midnight, as the tick compares the time of day");
        check(Next([], day) is null && Next([TimeSpan.FromHours(25), TimeSpan.FromHours(-1)], day) is null,
            "No or invalid restart times: no restart");
    }

    private static async Task LauncherChecks(Action<bool, string> check)
    {
        var none = CancellationToken.None;
        var root = Path.Combine(Path.GetTempPath(), "TrebuchetBotActionsChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tag = "TrebuchetBotCheck-" + Guid.NewGuid().ToString("N")[..8];
        var config = new Config { ServerInstanceCount = 3, DataDirectory = root, AutoUpdateStatus = AutoUpdateStatus.Never };
        var setup = new AppSetup(config, GameEdition.Enhanced, false, false);
        var os = OsPlatformSpecificExtensions.GetOsPlatformSpecific();
        var files = new AppFiles(setup, os);
        var saveA = files.Server.Ref(tag + "-A");
        var saveB = files.Server.Ref(tag + "-B");
        var mods = files.Mods.Ref(tag);
        config.SetInstanceProfile(0, saveA.Uri.OriginalString);
        config.SetInstanceProfile(1, saveB.Uri.OriginalString);

        var scan = new ProcessScan();
        var launcherLog = new ListLogger<Launcher>();
        using var steam = new Steam(NullLogger<Steam>.Instance, os, setup, new TrebuchetLib.Services.Progress());
        var launcher = new Launcher(files, os, scan, setup, steam, new BackupManager(setup, files),
            new ConanProcessFactory(NullLogger<Rcon>.Instance, NullLogger<LogReader>.Instance, NullLoggerFactory.Instance),
            launcherLog);
        var ui = new UiThread();
        var log = new ListLogger<LauncherBotActions>();
        var actions = new LauncherBotActions(launcher, setup, files, ui, log);
        var standIns = new List<Process>();
        var savesCreated = false;

        async Task<BotServerStatus?> StatusOf(int instance) =>
            (await actions.GetStatusesAsync(none)).FirstOrDefault(x => x.Instance == instance);
        Task<T> OnLauncher<T>(Func<T> read) => ui.InvokeAsync(() => Task.FromResult(read()));
        async Task<BotServerStatus?> WaitFor(int instance, Func<BotServerStatus?, bool> condition)
        {
            BotServerStatus? status = null;
            for (var i = 0; i < 150; i++)
            {
                status = await StatusOf(instance);
                if (condition(status)) break;
                await Task.Delay(100);
            }
            return status;
        }

        try
        {
            check(actions.InstanceCount == 3, "The instance count comes from the settings");
            var statuses = await actions.GetStatusesAsync(none);
            check(statuses.Count == 2 && statuses[0] is { Instance: 0, State: BotServerState.Stopped, Pending: BotPendingAction.None }
                  && statuses[0].Name == saveA.Name && statuses[1].Name == saveB.Name,
                "One status per instance with a selected save, under the name of the save");

            // A stand-in server process for instance 0, found by the process scan like a server that ran before.
            var started = DateTime.UtcNow.AddHours(-1);
            standIns.Add(StartStandIn());
            scan.Servers.Add(new ProcessData
            {
                pid = standIns[0].Id, filename = setup.GetInstanceInternalBinary(0), args = string.Empty,
                processName = Constants.FileServerBin, start = started
            });
            await ui.InvokeAsync(async () => { await launcher.AttachRunningServers(); return true; });
            scan.Servers.Clear();
            var running = await StatusOf(0);
            check(running is { State: BotServerState.Starting, Pending: BotPendingAction.None } && running.StartedUtc == started,
                "A server process that does not answer queries yet is starting: " + running);

            check((await actions.ResumeAsync(0, none)).Outcome == BotActionOutcome.NothingPending, "Nothing to release without a pause");
            var postpone = await actions.PostponeAsync(0, 30, "Event {servername}\n@here", none);
            check(postpone.Success && await OnLauncher(() => launcher.GetAutomaticRestartsPause(0)) is { } until
                                   && Near(until, DateTime.UtcNow.AddMinutes(30)),
                "Postponing with nothing pending pauses the automatic restarts for the minutes: " + postpone);
            check((await StatusOf(0))?.PausedUntilUtc is { } shown && Near(shown, DateTime.UtcNow.AddMinutes(30)), "The status shows the pause");
            check(log.Contains("reason: Event servername @here") && !log.Contains("{servername}"), "The reason goes cleaned into the log");
            check((await actions.ResumeAsync(0, none)).Success && await OnLauncher(() => launcher.GetAutomaticRestartsPause(0)) is null,
                "Release ends the pause");

            check((await actions.PostponeAsync(1, 30, "", none)).Outcome == BotActionOutcome.NotRunning
                  && (await actions.RestartAsync(1, TimeSpan.Zero, "", none)).Outcome == BotActionOutcome.NotRunning
                  && (await actions.StopAsync(1, TimeSpan.FromMinutes(5), "", none)).Outcome == BotActionOutcome.NotRunning
                  && await OnLauncher(() => launcher.GetAutomaticRestartsPause(1)) is null,
                "A server that does not run is not postponed, restarted or stopped");
            check((await actions.StartAsync(0, none)).Outcome == BotActionOutcome.AlreadyRunning, "A running server is not started again");
            check(await actions.StartAsync(1, none) is { Outcome: BotActionOutcome.Failed } notInstalled && notInstalled.Detail.Contains("installed"),
                "An instance that is not installed is not started");

            var calls = ui.Invocations;
            BotActionResult[] invalid =
            [
                await actions.PostponeAsync(3, 30, "", none), await actions.PostponeAsync(-1, 30, "", none),
                await actions.PostponeAsync(0, 0, "", none), await actions.PostponeAsync(0, 24 * 60 + 1, "", none),
                await actions.ResumeAsync(3, none), await actions.RestartAsync(0, TimeSpan.FromMinutes(-1), "", none),
                await actions.StopAsync(0, TimeSpan.FromHours(25), "", none), await actions.StartAsync(int.MaxValue, none)
            ];
            check(invalid.All(x => x.Outcome == BotActionOutcome.Failed) && ui.Invocations == calls,
                "Instances, minutes and delays out of range are refused before they reach the launcher");
            using (var cancelled = new CancellationTokenSource())
            {
                await cancelled.CancelAsync();
                check((await actions.StopAsync(0, TimeSpan.Zero, "", cancelled.Token)).Outcome == BotActionOutcome.Failed
                      && ui.Invocations == calls && (await StatusOf(0))?.State == BotServerState.Starting,
                    "A cancelled request does nothing");
            }

            check(await actions.UpdateAsync(none) is { Outcome: BotActionOutcome.Failed } off && off.Detail.Contains("off"),
                "No update while updates are switched off in the settings");
            config.AutoUpdateStatus = AutoUpdateStatus.OnlyOnStart;
            check((await actions.UpdateAsync(none)).Success, "The update runs (nothing outdated, the running server keeps running)");
            config.AutoUpdateStatus = AutoUpdateStatus.Never;

            // Work the caller gave up on never runs: the caller was told it did not happen.
            var held = new HeldDispatcher();
            var heldActions = new LauncherBotActions(launcher, setup, files, held, log);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                check(await heldActions.PostponeAsync(0, 30, "", timeout.Token) is { Outcome: BotActionOutcome.Failed } given
                      && given.Detail.Contains("cancel"), "An action the launcher thread did not take in time is given up");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                check((await heldActions.GetStatusesAsync(timeout.Token)).Count == 0, "A status that could not be read is empty");
            await held.ReleaseOn(ui);
            check(held.Ran == 2 && await OnLauncher(() => launcher.GetAutomaticRestartsPause(0)) is null,
                "Work released after the caller gave up does nothing");

            var broken = new LauncherBotActions(launcher, setup, files, new BrokenDispatcher(), log);
            check((await broken.PostponeAsync(0, 30, "", none)).Outcome == BotActionOutcome.Failed
                  && (await broken.GetStatusesAsync(none)).Count == 0
                  && (await broken.StartAsync(1, none)).Outcome == BotActionOutcome.Failed
                  && (await broken.UpdateAsync(none)).Outcome == BotActionOutcome.Failed,
                "A broken launcher thread gives failures, never an exception");

            // Countdowns and starts need saves on disk. They are written to the save folder of Trebuchet, so only where
            // that is a temporary folder (tests with a temporary home) or on the build server.
            var saveFolder = files.Server.GetBaseFolder();
            if (Path.IsPathRooted(saveFolder) && (IsInside(saveFolder, Path.GetTempPath())
                || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)))
            {
                savesCreated = true;
                await SaveChecks();
            }

            var stopNow = await actions.StopAsync(0, TimeSpan.Zero, "Wartung", none);
            var stopping = await StatusOf(0);
            check(stopNow.Success && stopping?.State == BotServerState.Stopping, "A stop now stops the server: " + stopNow + " " + stopping);
            check((await actions.PostponeAsync(0, 30, "", none)).Outcome == BotActionOutcome.Busy
                  && (await actions.StartAsync(0, none)).Outcome == BotActionOutcome.Busy
                  && (await actions.RestartAsync(0, TimeSpan.Zero, "", none)).Outcome == BotActionOutcome.Busy,
                "A stopping server is busy for postpone, start and restart");
            check(await HasExited(standIns[0]), "The stand-in server process was stopped");
        }
        finally
        {
            foreach (var process in standIns)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* gone */ }
                process.Dispose();
            }
            try { await ui.InvokeAsync(() => { launcher.Dispose(); return Task.FromResult(true); }).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* best effort */ }
            if (savesCreated)
            {
                // Only what this check created, by its unique name.
                foreach (var save in new[] { saveA, saveB })
                    if (save.Name.StartsWith(tag, StringComparison.Ordinal) && files.Server.Exists(save))
                        files.Server.Delete(save);
                if (mods.Name.StartsWith(tag, StringComparison.Ordinal) && files.Mods.Exists(mods))
                    files.Mods.Delete(mods);
            }
            try { Directory.Delete(root, recursive: true); } catch { /* a log reader may still hold a file */ }
        }

        async Task SaveChecks()
        {
            await ui.InvokeAsync(() =>
            {
                var a = files.Server.Create(saveA);
                // The stand-in has no players: a countdown that ends early on an empty server would end at once.
                a.RestartEarlyWhenEmpty = false;
                a.AutoRestart = true;
                var at = DateTime.Now.AddMinutes(20).TimeOfDay;
                a.AutoRestartDailyTime = [new TimeSpan(at.Hours, at.Minutes, 0)];
                a.SaveFile();
                var b = files.Server.Create(saveB);
                b.RestartEarlyWhenEmpty = false;
                b.StartingSequence = new Sequence { Actions = [new SequenceActionWait { WaitTime = TimeSpan.FromSeconds(4) }] };
                b.SaveFile();
                files.Mods.Create(mods);
                return Task.FromResult(true);
            });
            config.SetInstanceModlist(0, mods.Uri.OriginalString);
            config.SetInstanceModlist(1, mods.Uri.OriginalString);

            // The stand-in started an hour ago: with the default minimum uptime of 2 hours today's restart is skipped.
            check((await StatusOf(0))?.Pending == BotPendingAction.None, "A restart time before the minimum uptime is not pending");
            await ui.InvokeAsync(() => Task.FromResult(files.Server.Get(saveA).AutoRestartMinUptime = TimeSpan.Zero));
            var scheduled = await StatusOf(0);
            check(scheduled is { Pending: BotPendingAction.Restart, PendingAtUtc: { } restartAt }
                  && restartAt > DateTime.UtcNow.AddMinutes(18.9) && restartAt <= DateTime.UtcNow.AddMinutes(20),
                "The restart time of the save within the hour is a pending restart: " + scheduled);
            await ui.InvokeAsync(() => Task.FromResult(files.Server.Get(saveA).UseRestartWarnings = true));
            var warned = await StatusOf(0);
            await ui.InvokeAsync(() => Task.FromResult(files.Server.Get(saveA).UseRestartWarnings = false));
            check(warned?.PendingAtUtc - scheduled?.PendingAtUtc == TimeSpan.FromMinutes(5),
                "The restart warnings of the save count to the time of the restart: " + warned);

            var stop = await actions.StopAsync(0, TimeSpan.FromMinutes(30), "Wartung {x}", none);
            var stopPending = await WaitFor(0, x => x?.Pending == BotPendingAction.Stop);
            check(stop.Success && await OnLauncher(() => launcher.IsCountdownRunning(0))
                               && stopPending is { State: BotServerState.Starting, PendingAtUtc: { } stopAt }
                               && Near(stopAt, DateTime.UtcNow.AddMinutes(30)),
                "A stop in 30 minutes is a countdown, the status shows the stop and when: " + stop + " " + stopPending);
            check(launcherLog.Contains("Stopping server 0 in 30 min, reason: Wartung x"), "The reason of the stop reaches the launcher");

            var moved = await actions.PostponeAsync(0, 15, "Event", none);
            var movedStatus = await WaitFor(0, x => x?.PendingAtUtc is { } t && t > DateTime.UtcNow.AddMinutes(40));
            check(moved.Success && movedStatus is { Pending: BotPendingAction.Stop, PendingAtUtc: { } movedAt }
                                && Near(movedAt, DateTime.UtcNow.AddMinutes(45))
                                && await OnLauncher(() => launcher.GetAutomaticRestartsPause(0)) is null,
                "Postponing a pending stop moves its end back by the minutes (30 + 15), nothing is paused: " + moved + " " + movedStatus);
            check(launcherLog.Contains("Stopping server 0 in 45 min, reason: Wartung x"), "The postponed stop keeps its reason and never ends sooner than before");

            var restart = await actions.RestartAsync(0, TimeSpan.FromMinutes(20), "Patch", none);
            var restartPending = await WaitFor(0, x => x?.Pending == BotPendingAction.Restart);
            check(restart.Success && restartPending?.PendingAtUtc is { } restartAt2 && Near(restartAt2, DateTime.UtcNow.AddMinutes(20))
                  && launcherLog.Contains("Restarting server 0 in 20 min, reason: Patch"),
                "A restart in 20 minutes replaces the stop countdown: " + restart + " " + restartPending);

            var postponed = await actions.PostponeAsync(0, 45, "Event", none);
            var afterPostpone = await WaitFor(0, x => x?.PausedUntilUtc is not null);
            check(postponed.Success && !await OnLauncher(() => launcher.IsCountdownRunning(0))
                                    && afterPostpone is { Pending: BotPendingAction.Restart, PausedUntilUtc: { } pausedUntil }
                                    && afterPostpone.PendingAtUtc == pausedUntil && Near(pausedUntil, DateTime.UtcNow.AddMinutes(45))
                                    && launcherLog.Contains("Restart of instance 0 postponed by 45 minutes"),
                "Postponing a restart countdown cancels it, the restart follows the pause: " + afterPostpone);
            check((await actions.ResumeAsync(0, none)).Success
                  && await StatusOf(0) is { Pending: BotPendingAction.Restart, PendingAtUtc: null, PausedUntilUtc: null },
                "After the release the postponed restart is due");

            // Instances 0 and 1 installed (the binaries only), instance 1 on the save of the running instance 0.
            foreach (var instance in new[] { 0, 1 })
            {
                var binary = setup.GetInstanceInternalBinary(instance);
                Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
                await File.WriteAllBytesAsync(binary, []);
            }
            config.SetInstanceProfile(1, saveA.Uri.OriginalString);
            check(await actions.StartAsync(1, none) is { Outcome: BotActionOutcome.Busy } inUse && inUse.Detail.Contains("save"),
                "A save that runs on another instance is not started twice");
            config.SetInstanceProfile(1, saveB.Uri.OriginalString);

            var start = await actions.StartAsync(1, none);
            var starting = await StatusOf(1);
            check(start.Success && starting?.State == BotServerState.Starting,
                "A start with a starting sequence is under way: " + start + " " + starting);
            config.AutoUpdateStatus = AutoUpdateStatus.OnlyOnStart;
            check((await actions.UpdateAsync(none)).Outcome == BotActionOutcome.Busy
                  && (await actions.StartAsync(1, none)).Outcome == BotActionOutcome.Busy
                  && (await actions.PostponeAsync(1, 30, "", none)).Outcome == BotActionOutcome.NotRunning,
                "While a server starts: no update, no second start, nothing to postpone");
            var failedStart = await WaitFor(1, x => x?.State == BotServerState.Stopped);
            check(failedStart?.State == BotServerState.Stopped, "The start that failed (no server files) ends the starting state");
            check((await actions.UpdateAsync(none)).Success, "Once nothing starts the update runs");
            config.AutoUpdateStatus = AutoUpdateStatus.Never;

            await ui.InvokeAsync(() => Task.FromResult(files.Server.Get(saveB).StartingSequence = new Sequence()));
            check(await actions.StartAsync(1, none) is { Outcome: BotActionOutcome.Failed } failed && (await StatusOf(1))?.State == BotServerState.Stopped,
                "A start the launcher could not do is a failure");
        }
    }

    private static bool Near(DateTime actual, DateTime expected) => (actual - expected).Duration() < TimeSpan.FromMinutes(1);

    private static bool IsInside(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static Process StartStandIn()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1 >nul")
            : new ProcessStartInfo("sleep", "120");
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        return Process.Start(start)!;
    }

    private static async Task<bool> HasExited(Process process)
    {
        for (var i = 0; i < 100 && !process.HasExited; i++)
            await Task.Delay(100);
        return process.HasExited;
    }

    /// <summary>The thread the launcher is used on, like the UI thread of the window: one thread, posted work.</summary>
    private sealed class UiThread : SynchronizationContext, IBotDispatcher
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private int _invocations;

        public UiThread()
        {
            var thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                    callback(state);
            }) { IsBackground = true, Name = "Launcher thread" };
            thread.Start();
        }

        public int Invocations => Volatile.Read(ref _invocations);

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
        public override SynchronizationContext CreateCopy() => this;

        public Task<T> InvokeAsync<T>(Func<Task<T>> work)
        {
            Interlocked.Increment(ref _invocations);
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { result.SetResult(await work()); }
                catch (Exception ex) { result.SetException(ex); }
            }, null);
            return result.Task;
        }
    }

    /// <summary>Holds the work until it is released, like a launcher thread that is busy for a long time.</summary>
    private sealed class HeldDispatcher : IBotDispatcher
    {
        private readonly List<Func<Task>> _queued = [];
        private int _ran;

        public int Ran => Volatile.Read(ref _ran);

        public Task<T> InvokeAsync<T>(Func<Task<T>> work)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_queued)
                _queued.Add(async () =>
                {
                    Interlocked.Increment(ref _ran);
                    try { result.SetResult(await work()); }
                    catch (Exception ex) { result.SetException(ex); }
                });
            return result.Task;
        }

        public async Task ReleaseOn(UiThread ui)
        {
            List<Func<Task>> queued;
            lock (_queued)
            {
                queued = _queued.ToList();
                _queued.Clear();
            }
            foreach (var work in queued)
                await ui.InvokeAsync(async () => { await work(); return true; });
        }
    }

    /// <summary>A launcher thread that is gone: throws or fails every call.</summary>
    private sealed class BrokenDispatcher : IBotDispatcher
    {
        private int _calls;

        public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Interlocked.Increment(ref _calls) % 2 == 0
            ? throw new InvalidOperationException("dispatcher shut down")
            : Task.FromException<T>(new InvalidOperationException("dispatcher shut down"));
    }

    /// <summary>The process scan of the launcher, with the stand-ins as server processes.</summary>
    private sealed class ProcessScan : ITrebuchetOsSpecific
    {
        public List<ProcessData> Servers { get; } = [];
        public Task<List<ProcessData>> GetProcessesWithName(string processName) =>
            Task.FromResult(processName == Constants.FileServerBin ? Servers.ToList() : []);
        public IEnumerable<ProcessData> GetChildProcesses(int parentId) => [];
        public ProcessData GetFirstChildProcesses(int parentId) => ProcessData.Empty;
        public ProcessData GetProcess(int processId) => ProcessData.Empty;
        public void SetEveryoneAccess(DirectoryInfo dir) { }
        public void FocusWindow(IntPtr hwnd) { }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = [];

        public bool Contains(string text)
        {
            lock (_lines) return _lines.Any(x => x.Contains(text, StringComparison.Ordinal));
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }
    }
}
