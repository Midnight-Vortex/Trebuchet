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
        PostponementChecks(Check);
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

        // The name of the server is the one of its save ("Server name", the first field); the name of the save while that
        // kept its default; never a path or something Discord would format.
        check(Show(Process(ProcessState.ONLINE) with { ServerName = "SuL <@123> [Dev]" }).Name == "SuL 123 Dev"
              && Show(Process(ProcessState.ONLINE) with { ServerName = "Haupt/Server" }).Name == "Haupt/Server"
              && Show(Process(ProcessState.ONLINE) with { ServerName = "" }).Name == "Exiles"
              && Show(Process(ProcessState.ONLINE) with { ServerName = "<>@" }).Name == "Exiles",
            "A server is shown under the name of its save profile, else the name of the save");

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
        // Nothing is reused here: the checks change settings of the saves that the launcher does not announce, and read the state at once.
        var actions = new LauncherBotActions(launcher, setup, files, ui, log) { StatusReuse = TimeSpan.Zero };
        // Every change the status messages show is announced, from whatever thread noticed it.
        var changes = 0;
        actions.StatusChanged += (_, _) => Interlocked.Increment(ref changes);
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
            var changesBefore = Volatile.Read(ref changes);
            var postpone = await actions.PostponeAsync(0, 30, "Event {servername}\n@here", none);
            check(postpone.Success && await OnLauncher(() => launcher.GetAutomaticRestartsPause(0)) is { } until
                                   && Near(until, DateTime.UtcNow.AddMinutes(30)),
                "Postponing with nothing pending pauses the automatic restarts for the minutes: " + postpone);
            check((await StatusOf(0))?.PausedUntilUtc is { } shown && Near(shown, DateTime.UtcNow.AddMinutes(30)), "The status shows the pause");
            check(log.Contains("reason: Event servername here") && !log.Contains("{servername}") && !log.Contains("@here"), "The reason goes cleaned into the log (no macro, no mention)");
            check(Volatile.Read(ref changes) > changesBefore, "A pause is announced to the status messages at once");
            changesBefore = Volatile.Read(ref changes);
            check((await actions.ResumeAsync(0, none)).Success && await OnLauncher(() => launcher.GetAutomaticRestartsPause(0)) is null,
                "Release ends the pause");
            check(Volatile.Read(ref changes) > changesBefore, "The end of a pause is announced as well");

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

            // The check for updates asks Steam and installs nothing (here no mod lists and no installed servers: no network).
            var scanned = await actions.CheckUpdatesAsync(none);
            check(scanned is { Outcome: BotActionOutcome.Done, Updates.OutdatedMods: 0 }, "The check for updates reports what it found: " + scanned);
            check(await OnLauncher(() => launcher.GetAutomaticRestartsPause(0)) is null && (await StatusOf(0))?.State == BotServerState.Starting,
                "The check changes nothing about the servers");

            // The state of the servers is read once and shared: a button press, the status message and the community log ask within a moment,
            // and every read is a trip to the launcher thread.
            using (var shared = new LauncherBotActions(launcher, setup, files, ui, log))
            {
                var readsBefore = ui.Invocations;
                var burst = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => shared.GetStatusesAsync(none)));
                check(ui.Invocations == readsBefore + 1 && burst.All(x => x.Count == burst[0].Count && x.Count > 0),
                    "Six asks at once are one trip to the launcher: " + (ui.Invocations - readsBefore));
                await shared.GetStatusesAsync(none);
                check(ui.Invocations == readsBefore + 1, "...and one right after reuses it");
                await ui.InvokeAsync(async () => { await launcher.PauseAutomaticRestarts(0, TimeSpan.FromMinutes(5)); return true; });
                var afterEvent = ui.Invocations;
                var pausedState = await shared.GetStatusesAsync(none);
                check(ui.Invocations == afterEvent + 1 && pausedState.FirstOrDefault(x => x.Instance == 0)?.PausedUntilUtc is not null,
                    "A change of the launcher makes the next ask read again (and see the change)");
                await shared.ResumeAsync(0, none);
                var afterAction = ui.Invocations;
                var resumedState = await shared.GetStatusesAsync(none);
                await shared.GetStatusesAsync(none);
                check(ui.Invocations == afterAction + 1 && resumedState.FirstOrDefault(x => x.Instance == 0)?.PausedUntilUtc is null,
                    "After an action the next read is fresh, and shared again");
                await Task.Delay(500);
                var staleReads = ui.Invocations;
                await shared.GetStatusesAsync(none);
                check(ui.Invocations == staleReads + 1, "A read that is older than the reuse time is not used");
                using var gone = new CancellationTokenSource();
                await gone.CancelAsync();
                var reads = ui.Invocations;
                check((await shared.GetStatusesAsync(gone.Token)).Count == 0 && ui.Invocations == reads && (await shared.GetStatusesAsync(none)).Count > 0,
                    "A caller that gave up gets nothing and does not spoil the read for the others");
            }

            // A caller that gives up while the read of the others is under way: it gets nothing, the others get their state.
            var heldRead = new HeldDispatcher();
            using (var waiting = new LauncherBotActions(launcher, setup, files, heldRead, log))
            {
                using var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                var gaveUp = waiting.GetStatusesAsync(impatient.Token);
                var patient = waiting.GetStatusesAsync(none);
                await Task.Delay(400);
                check(gaveUp.IsCompleted && (await gaveUp).Count == 0 && !patient.IsCompleted, "A caller that gives up while the read is held gets nothing and the other keeps waiting");
                await heldRead.ReleaseOn(ui);
                check((await patient).Count > 0 && heldRead.Ran == 1, "...and gets its state when the read is done (one read for both)");
            }

            // Work the caller gave up on never runs: the caller was told it did not happen.
            var held = new HeldDispatcher();
            var heldActions = new LauncherBotActions(launcher, setup, files, held, log) { StatusReuse = TimeSpan.Zero };
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                check(await heldActions.PostponeAsync(0, 30, "", timeout.Token) is { Outcome: BotActionOutcome.Failed } given
                      && given.Detail.Contains("cancel"), "An action the launcher thread did not take in time is given up");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                check((await heldActions.GetStatusesAsync(timeout.Token)).Count == 0, "A status that could not be read is empty");
            await held.ReleaseOn(ui);
            check(held.Ran == 2 && await OnLauncher(() => launcher.GetAutomaticRestartsPause(0)) is null,
                "Work released after the caller gave up does nothing");

            var broken = new LauncherBotActions(launcher, setup, files, new BrokenDispatcher(), log) { StatusReuse = TimeSpan.Zero };
            check((await broken.PostponeAsync(0, 30, "", none)).Outcome == BotActionOutcome.Failed
                  && (await broken.GetStatusesAsync(none)).Count == 0
                  && (await broken.StartAsync(1, none)).Outcome == BotActionOutcome.Failed
                  && (await broken.UpdateAsync(none)).Outcome == BotActionOutcome.Failed
                  && (await broken.CheckUpdatesAsync(none)).Outcome == BotActionOutcome.Failed,
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

            changesBefore = Volatile.Read(ref changes);
            var stopNow = await actions.StopAsync(0, TimeSpan.Zero, "Wartung", none);
            var stopping = await StatusOf(0);
            check(stopNow.Success && stopping?.State == BotServerState.Stopping, "A stop now stops the server: " + stopNow + " " + stopping);
            check((await actions.PostponeAsync(0, 30, "", none)).Outcome == BotActionOutcome.Busy
                  && (await actions.StartAsync(0, none)).Outcome == BotActionOutcome.Busy
                  && (await actions.RestartAsync(0, TimeSpan.Zero, "", none)).Outcome == BotActionOutcome.Busy,
                "A stopping server is busy for postpone, start and restart");
            check(await HasExited(standIns[0]), "The stand-in server process was stopped");
            for (var i = 0; i < 100 && Volatile.Read(ref changes) <= changesBefore; i++) await Task.Delay(50);
            check(Volatile.Read(ref changes) > changesBefore, "A server that is stopping is announced to the status messages");
            actions.Dispose();
            await Task.Delay(300);
            var afterDispose = Volatile.Read(ref changes);
            var launcherEvents = 0;
            launcher.ServerStatusChanged += (_, _) => Interlocked.Increment(ref launcherEvents);
            await ui.InvokeAsync(async () => { await launcher.PauseAutomaticRestarts(0, TimeSpan.FromMinutes(5)); return true; });
            check(launcherEvents > 0 && Volatile.Read(ref changes) == afterDispose, "A disposed bridge no longer listens to the launcher");
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

            // The "Server name" of the save is the name of the server: in the status, and in the log lines of the launcher.
            await ui.InvokeAsync(() => Task.FromResult(files.Server.Get(saveA).ServerName = "Test <Server>"));
            check((await StatusOf(0))?.Name == "Test Server" && await OnLauncher(() => launcher.GetInstanceLabel(0)) == "0 (Test <Server>)",
                "The name of the server comes from its save: " + await OnLauncher(() => launcher.GetInstanceLabel(0)));
            await ui.InvokeAsync(() => Task.FromResult(files.Server.Get(saveA).ServerName = ServerProfile.ServerNameDefault));
            check((await StatusOf(0))?.Name == saveA.Name && await OnLauncher(() => launcher.GetInstanceLabel(0)) == "0",
                "While the save keeps the default name the save is shown, and the log shows the number");

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

            var plannedAt = restartPending?.PendingAtUtc ?? DateTime.UtcNow.AddMinutes(20);
            var postponed = await actions.PostponeAsync(0, 45, "Event", none);
            var afterPostpone = await WaitFor(0, x => x?.PausedUntilUtc is not null);
            check(postponed.Success && !await OnLauncher(() => launcher.IsCountdownRunning(0))
                                    && afterPostpone is { Pending: BotPendingAction.Restart, PausedUntilUtc: { } pausedUntil }
                                    && afterPostpone.PendingAtUtc == pausedUntil && Near(pausedUntil, DateTime.UtcNow.AddMinutes(65))
                                    && launcherLog.Contains("Restart of server 0 postponed by 45 minutes"),
                "Postponing a restart countdown moves its end back by the minutes (20 + 45), the restart follows the pause: " + afterPostpone);
            // Lifting the pause brings the old time back: the restart goes down in the 20 minutes it was planned for, with a countdown
            // that fits (not a new full countdown that starts from nothing).
            check((await actions.ResumeAsync(0, none)).Success, "The postponement can be lifted");
            var resumed = await WaitFor(0, x => x is { Pending: BotPendingAction.Restart, PendingAtUtc: { } t, PausedUntilUtc: null } && t < DateTime.UtcNow.AddMinutes(30));
            check(resumed is { Pending: BotPendingAction.Restart, PendingAtUtc: { } resumedAt, PausedUntilUtc: null } && Near(resumedAt, plannedAt)
                  && await OnLauncher(() => launcher.IsCountdownRunning(0)),
                "After the release the restart is back at the time it was planned for before: " + resumed);

            // The countdown can be cancelled: a restart somebody started by hand just ends, nothing is paused, the players are told.
            check((await StatusOf(0))?.Countdown == true, "A running countdown is shown as one (it can be cancelled)");
            var cancelled = await actions.CancelAsync(0, none);
            var afterCancel = await WaitFor(0, x => x?.Countdown == false);
            check(cancelled.Success && !await OnLauncher(() => launcher.IsCountdownRunning(0)) && afterCancel is { Countdown: false, PausedUntilUtc: null }
                  && await OnLauncher(() => launcher.GetAutomaticRestartsPause(0)) is null,
                "Cancelling a countdown ends it without pausing anything: " + cancelled + " " + afterCancel);
            check((await actions.CancelAsync(0, none)).Outcome == BotActionOutcome.NothingPending && (await actions.CancelAsync(7, none)).Outcome == BotActionOutcome.Failed,
                "Without a countdown there is nothing to cancel, a server that does not exist is refused");

            // A restart the launcher started on its own (schedule, update) would begin again with the next tick: cancelling it holds the
            // automatic restarts for a while, also after it was postponed and taken back (it stays what it was).
            await ui.InvokeAsync(() => Task.FromResult(files.Server.Get(saveA).UseRestartWarnings = true));
            await ui.InvokeAsync(() => { launcher.RestartServer(0); return Task.FromResult(true); });
            var automatic = await WaitFor(0, x => x is { Countdown: true, Automatic: true });
            check(automatic is { Countdown: true, Automatic: true, Pending: BotPendingAction.Restart, Sequence: false },
                "A restart the launcher started on its own is shown as automatic, and its countdown is no 'going down now': " + automatic);
            check((await actions.PostponeAsync(0, 45, "Event", none)).Success, "The automatic restart can be postponed");
            var autoHeld = await WaitFor(0, x => x?.PausedUntilUtc is not null && x.Countdown == false);
            check(autoHeld is { PausedUntilUtc: not null }, "...and then waits for the pause: " + autoHeld);
            // Postponing again puts the pause off by the minutes (a new pause of the minutes from now would end sooner than this one).
            var pauseBefore = autoHeld!.PausedUntilUtc!.Value;
            check((await actions.PostponeAsync(0, 15, "Event", none)).Success, "A second postponement is accepted");
            var extended = await WaitFor(0, x => x?.PausedUntilUtc is { } u && u > pauseBefore.AddMinutes(10));
            check(extended?.PausedUntilUtc is { } extendedUntil && Near(extendedUntil, pauseBefore.AddMinutes(15)),
                "A second postponement adds to the pause that runs, it never brings the restart forward: " + extended?.PausedUntilUtc + " after " + pauseBefore);
            check((await actions.ResumeAsync(0, none)).Success, "The postponement is lifted");
            var autoAgain = await WaitFor(0, x => x is { Countdown: true, PausedUntilUtc: null });
            check(autoAgain is { Automatic: true }, "A postponed automatic restart that is taken back is still an automatic one: " + autoAgain);
            var autoCancel = await actions.CancelAsync(0, none);
            var autoAfter = await WaitFor(0, x => x?.PausedUntilUtc is not null && x.Countdown == false);
            check(autoCancel is { Success: true, Detail: "hold=30" } && autoAfter?.PausedUntilUtc is { } holdUntil && Near(holdUntil, DateTime.UtcNow.AddMinutes(30)),
                "Cancelling it holds the automatic restarts for 30 minutes: " + autoCancel + " " + autoAfter);
            check(autoAfter is { CancelledAtUtc: { } markedAt } && Near(markedAt, DateTime.UtcNow), "A cancel is marked in the status (the community log tells it at once): " + autoAfter?.CancelledAtUtc);
            check((await actions.ResumeAsync(0, none)).Success && await StatusOf(0) is { PausedUntilUtc: null },
                "The hold can be lifted");
            // A new countdown clears the marker; a cancel of a countdown somebody started by hand holds nothing and is marked too.
            check((await actions.RestartAsync(0, TimeSpan.FromMinutes(20), "Test", none)).Success, "A restart by hand starts a countdown");
            var handCountdown = await WaitFor(0, x => x is { Countdown: true });
            check(handCountdown is { CancelledAtUtc: null, Automatic: false, Sequence: false }, "...a new countdown clears the mark, is not automatic and is no 'going down now': " + handCountdown);
            var handCancel = await actions.CancelAsync(0, none);
            var handAfter = await WaitFor(0, x => x is { Countdown: false });
            check(handCancel is { Success: true, Detail: "" } && handAfter is { CancelledAtUtc: { }, PausedUntilUtc: null }, "...cancelling it holds nothing, but is marked: " + handCancel + " " + handAfter);
            await ui.InvokeAsync(() => Task.FromResult(files.Server.Get(saveA).UseRestartWarnings = false));

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

            // What the dashboard shows as selected starts: a mod list that was never chosen is the first one (the launcher does the
            // same), and so is the plain name that older versions stored. What was chosen and is gone is refused, with the reason.
            async Task<string> Reason(string modList, string? save = null)
            {
                config.SetInstanceModlist(1, modList);
                if (save is not null) config.SetInstanceProfile(1, save);
                var result = await actions.StartAsync(1, none);
                config.SetInstanceProfile(1, saveB.Uri.OriginalString);
                return result.Outcome == BotActionOutcome.Failed ? result.Detail : result.Outcome.ToString();
            }
            var gone = files.Mods.Ref(tag + "-gone");
            var neverChosen = await Reason("");
            var plainName = await Reason(mods.Name);
            var linked = await Reason(mods.Uri.OriginalString);
            check(!neverChosen.Contains("mod list") && !plainName.Contains("mod list") && !linked.Contains("mod list"),
                "A mod list that was never chosen, a plain name and a link to a mod list that is there are not refused: " + neverChosen + " | " + plainName + " | " + linked);
            check((await Reason(gone.Uri.OriginalString)).Contains("mod list") && (await Reason(gone.Name)).Contains("mod list"),
                "A chosen mod list that is gone is refused as such");
            check((await Reason(mods.Uri.OriginalString, files.Server.Ref(tag + "-gone").Uri.OriginalString)) == "no save selected",
                "A chosen save that is gone is refused as such");
            config.SetInstanceModlist(1, mods.Uri.OriginalString);
        }
    }

    // When a restart countdown is postponed: the restart goes down at the old time plus the minutes (never sooner), the pause ends
    // one warning lead before.
    private static void PostponementChecks(Action<bool, string> check)
    {
        var now = new DateTime(2026, 10, 24, 15, 32, 0, DateTimeKind.Utc);
        var lead = TimeSpan.FromMinutes(5);
        var (pause, end) = Launcher.PlanRestartPostponement(now.AddMinutes(28), now, TimeSpan.FromMinutes(30), lead);
        check(end == now.AddMinutes(58) && pause == TimeSpan.FromMinutes(53),
            "A restart in 28 minutes postponed by 30 goes down in 58 minutes (not after the 30 minutes from now), the pause ends 5 minutes before: " + pause + " " + end);
        (pause, end) = Launcher.PlanRestartPostponement(now.AddMinutes(20), now, TimeSpan.FromMinutes(45), TimeSpan.Zero);
        check(end == now.AddMinutes(65) && pause == TimeSpan.FromMinutes(65), "Without warnings the pause ends when the restart goes down: " + pause);
        (pause, end) = Launcher.PlanRestartPostponement(now.AddMinutes(-1), now, TimeSpan.FromMinutes(30), lead);
        check(end == now.AddMinutes(30) && pause == TimeSpan.FromMinutes(25), "A countdown that has run out counts from now: " + pause + " " + end);
        (pause, end) = Launcher.PlanRestartPostponement(null, now, TimeSpan.FromMinutes(15), lead);
        check(end == now.AddMinutes(15) && pause == TimeSpan.FromMinutes(10), "Without a known end it counts from now: " + pause + " " + end);

        // The warning before a restart for an update: the configured time, only for a save that warns its players at all.
        var warns = new ServerProfile { UseRestartWarnings = true };
        var quarter = TimeSpan.FromMinutes(15);
        check(Launcher.GetUpdateCountdown(warns, quarter) == quarter, "A save with warnings gets the configured time before an update restart");
        check(Launcher.GetUpdateCountdown(new ServerProfile { UseRestartWarnings = false }, quarter) == TimeSpan.Zero
              && Launcher.GetUpdateCountdown(null, quarter) == TimeSpan.Zero, "...a save without warnings restarts at once, as before");
        check(Launcher.GetUpdateCountdown(warns, TimeSpan.Zero) == TimeSpan.Zero && Launcher.GetUpdateCountdown(warns, TimeSpan.FromMinutes(4)) == TimeSpan.Zero,
            "...0 or less than the longest warning of the save (5 minutes) leaves it to the warnings of the save");
        warns.RestartWarnings = [new RestartWarning(TimeSpan.FromMinutes(30), "x")];
        check(Launcher.GetUpdateCountdown(warns, quarter) == TimeSpan.Zero && Launcher.GetUpdateCountdown(warns, TimeSpan.FromMinutes(45)) == TimeSpan.FromMinutes(45),
            "...and it is never shorter than the longest warning of the save");
        warns.StoppingSequence = new Sequence { Actions = [new SequenceActionWait { WaitTime = TimeSpan.FromSeconds(4) }] };
        check(Launcher.GetUpdateCountdown(warns, TimeSpan.FromMinutes(45)) == TimeSpan.Zero, "...and a save with a stopping sequence of its own keeps it");
        check(Launcher.GetUpdateCountdown(new ServerProfile { UseRestartWarnings = true }, TimeSpan.FromDays(3)) == Config.UpdateRestartCountdownMaximum,
            "...at most 6 hours");
        (pause, end) = Launcher.PlanRestartPostponement(now.AddSeconds(30), now, TimeSpan.FromMinutes(1), lead);
        check(pause is null && end == now.AddSeconds(90), "Too close for a pause (shorter than a minute): a countdown that ends at the new time: " + pause + " " + end);
        (pause, end) = Launcher.PlanRestartPostponement(now.AddMinutes(10), now, TimeSpan.FromMinutes(5), lead);
        check(pause == TimeSpan.FromMinutes(10) && end == now.AddMinutes(15), "A short shift of a restart that is further away still pauses: " + pause + " " + end);
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
