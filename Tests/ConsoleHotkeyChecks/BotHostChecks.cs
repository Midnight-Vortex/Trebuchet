using System.Collections.Concurrent;
using Boulder;
using Boulder.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TrebuchetLib;
using TrebuchetLib.DiscordBot;
using TrebuchetLib.Services;

/// <summary>
/// boulder bot, Trebuchet without the window: the launcher thread (one piece of work at a time, in order, on one thread,
/// stopped work never runs), the tick with its watchdog, the command line (edition flags, no token option) and the host
/// with stand-ins (exit codes, the lock of the window, nothing touched before the checks passed, clean end).
/// </summary>
static class BotHostChecks
{
    // Made up. Put together at run time so secret scanners do not take the source for a real token.
    // A different token in every run: the lock of a token is a machine wide name (parallel runs must not meet).
    private static readonly string SampleToken = string.Join(".", "MTIzNDU2Nzg5MDEyMzQ1Njc4", "GabcDE", "abcdefghijklmnopqrstuvwxyz0123456789ABCD" + Guid.NewGuid().ToString("N")[..8]);

    private static readonly BotHostTimings Fast = new()
    {
        TickInterval = TimeSpan.FromMilliseconds(5),
        TickTimeout = TimeSpan.FromSeconds(2),
        TickStopWait = TimeSpan.FromSeconds(1),
        RetryFirst = TimeSpan.FromMilliseconds(20),
        RetryMax = TimeSpan.FromMilliseconds(80),
        StatePoll = TimeSpan.FromMilliseconds(5),
        ReminderEvery = TimeSpan.FromMilliseconds(50),
        StopLimit = TimeSpan.FromSeconds(5)
    };

    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        await DispatcherChecks(Check);
        await TickLoopChecks(Check);
        CommandChecks(Check);
        InstallationChecks(Check);
        await HostChecks(Check);
        return count;
    }

    #region Launcher thread

    private static async Task DispatcherChecks(Action<bool, string> check)
    {
        using var dispatcher = new SerialBotDispatcher();

        // Many callers on many threads, work that awaits in between: all of it on one thread (so plain dictionaries are safe).
        var inside = 0;
        var mostInside = 0;
        var offThread = 0;
        var table = new Dictionary<int, int>();
        var load = Enumerable.Range(0, 200).Select(i => Task.Run(() => dispatcher.InvokeAsync(async () =>
        {
            var now = Interlocked.Increment(ref inside);
            if (now > Volatile.Read(ref mostInside)) Volatile.Write(ref mostInside, now);
            if (Environment.CurrentManagedThreadId != dispatcher.ThreadId) Interlocked.Increment(ref offThread);
            table[i] = 0;
            await Task.Yield();
            await Task.Delay(1);
            table[i] = i;
            if (Environment.CurrentManagedThreadId != dispatcher.ThreadId) Interlocked.Increment(ref offThread);
            Interlocked.Decrement(ref inside);
            return i;
        }))).ToArray();
        check(await Within(Task.WhenAll(load), 30_000), "The work under load ends");
        check(Volatile.Read(ref mostInside) >= 1, "The work ran");
        check(offThread == 0, "The work and its continuations run on the launcher thread");
        check(load.Select(x => x.Result).SequenceEqual(Enumerable.Range(0, 200)) && table.Count == 200 && table.All(x => x.Key == x.Value),
            "Every caller gets the result of its own work");

        // What the launcher starts and does not await (countdowns, background starts) comes back to the launcher thread.
        var background = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await dispatcher.InvokeAsync(() =>
        {
            _ = Later();
            return Task.FromResult(true);

            async Task Later()
            {
                await Task.Delay(20);
                background.SetResult(Environment.CurrentManagedThreadId);
            }
        });
        check(await Within(background.Task, 5_000) && background.Task.Result == dispatcher.ThreadId,
            "A continuation the work did not await runs on the launcher thread, like on the UI thread");

        // A long piece of work (a tick that waits for Steam) does not keep other work waiting: they interleave at the awaits,
        // like the tick and the bot's actions on the UI thread of the window.
        var order = new List<int>();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = dispatcher.InvokeAsync(async () =>
        {
            order.Add(-1);
            await hold.Task;
            order.Add(-2);
            return -1;
        });
        var queued = Enumerable.Range(0, 50).Select(i => dispatcher.InvokeAsync(async () =>
        {
            order.Add(i);
            await Task.Yield();
            return i;
        })).ToList();
        check(await Within(Task.WhenAll(queued), 10_000) && !first.IsCompleted,
            "Work handed in meanwhile runs while a long piece of work waits");
        check(order.Take(51).SequenceEqual(Enumerable.Range(-1, 51)), "Work starts in the order it was handed in: " + string.Join(",", order.Take(10)));
        hold.SetResult();
        check(await Within(first, 10_000) && order[^1] == -2, "The long work goes on when what it waited for is there");

        // Work that awaits other work of the same dispatcher ends (the thread is not blocked).
        var nested = await dispatcher.InvokeAsync(async () => await dispatcher.InvokeAsync(() => Task.FromResult(5)) + 1);
        check(nested == 6, "Work may await other work of its dispatcher");

        // Errors reach the caller as thrown, before or after an await, and the dispatcher goes on.
        check(await ThrowsAsync<InvalidOperationException>(dispatcher.InvokeAsync<int>(() => throw new InvalidOperationException("at once")), "at once"),
            "An error thrown before the first await reaches the caller");
        check(await ThrowsAsync<FormatException>(dispatcher.InvokeAsync<int>(async () =>
            {
                await Task.Yield();
                throw new FormatException("later");
            }), "later"),
            "An error thrown after an await reaches the caller");
        check(await ThrowsAsync<OperationCanceledException>(dispatcher.InvokeAsync<int>(() => throw new OperationCanceledException("own")), "own"),
            "A cancellation of the work itself reaches the caller");
        check(await dispatcher.InvokeAsync(() => Task.FromResult(42)) == 42, "After errors the dispatcher goes on");

        // Work that hands in more work without waiting for it: no deadlock, it runs after.
        var inner = new TaskCompletionSource<Task<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outer = dispatcher.InvokeAsync(async () =>
        {
            inner.SetResult(Task.Run(() => dispatcher.InvokeAsync(() => Task.FromResult(7))));
            await Task.Delay(20);
            return 1;
        });
        check(await Within(outer, 5_000) && outer.Result == 1, "Work that queued more work through a separate task ends");
        var innerTask = await inner.Task;
        check(await Within(innerTask, 5_000) && innerTask.Result == 7, "The work queued from inside work runs after it");
        Task<int>? direct = null;
        await dispatcher.InvokeAsync(() =>
        {
            direct = dispatcher.InvokeAsync(() => Task.FromResult(8));
            return Task.FromResult(0);
        });
        check(await Within(direct!, 5_000) && direct!.Result == 8, "Work queued right on the launcher thread without waiting runs after");

        // Stopped: queued work never runs, nobody is left waiting, new work is refused. The launcher thread is held on
        // purpose (blocked, which real work never does) so that the other work is still waiting in line when it stops.
        var stopped = new SerialBotDispatcher();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var running = stopped.InvokeAsync(() =>
        {
            started.SetResult();
            release.Wait();
            return Task.FromResult(true);
        });
        await started.Task;
        var ranAfterStop = 0;
        var waiting = Enumerable.Range(0, 5).Select(_ => stopped.InvokeAsync(() =>
        {
            Interlocked.Increment(ref ranAfterStop);
            return Task.FromResult(true);
        })).ToList();
        stopped.Dispose();
        release.Set();
        foreach (var task in waiting)
            check(await ThrowsAsync<OperationCanceledException>(task), "Queued work of a stopped dispatcher is cancelled");
        check(await Within(running.ContinueWith(_ => { }), 5_000), "The caller of running work is not left waiting by the stop");
        var late = stopped.InvokeAsync(() =>
        {
            Interlocked.Increment(ref ranAfterStop);
            return Task.FromResult(true);
        });
        check(await ThrowsAsync<OperationCanceledException>(late), "New work for a stopped dispatcher is cancelled");
        await Task.Delay(100);
        check(Volatile.Read(ref ranAfterStop) == 0 && stopped.IsStopped, "Work queued before or after the stop never runs");
    }

    #endregion

    #region Tick

    private static async Task TickLoopChecks(Action<bool, string> check)
    {
        // A failing tick (thrown at once or later) is logged, the next one runs; the loop ends with its token.
        var log = new ListLogger();
        var calls = 0;
        var failing = new LauncherTickLoop(() =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) throw new InvalidOperationException("tick failed at once");
            if (call == 2) return Task.FromException(new IOException("tick failed later"));
            return Task.CompletedTask;
        }, log) { Interval = TimeSpan.FromMilliseconds(5), Timeout = TimeSpan.FromSeconds(2) };
        using (var stop = new CancellationTokenSource())
        {
            var run = failing.RunAsync(stop.Token);
            check(await WaitUntil(() => Volatile.Read(ref calls) >= 5, 5_000), "Ticks go on after failed ticks");
            await stop.CancelAsync();
            check(await Within(run, 2_000) && run.IsCompletedSuccessfully, "The loop ends when it is cancelled, without an error");
            var after = Volatile.Read(ref calls);
            await Task.Delay(50);
            check(Volatile.Read(ref calls) == after, "No tick starts after the end");
        }
        check(failing.Failed == 2 && log.Count(LogLevel.Error, "Launcher tick failed") == 2
                                   && log.Contains("tick failed at once") && log.Contains("tick failed later"),
            "Both failed ticks are logged with their error");

        // A hung tick: logged, skipped while it hangs (never a second one next to it), ticking again once it ended.
        log = new ListLogger();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedTicks = 0;
        var concurrent = 0;
        var mostConcurrent = 0;
        var hanging = new LauncherTickLoop(async () =>
        {
            var tick = Interlocked.Increment(ref startedTicks);
            var now = Interlocked.Increment(ref concurrent);
            if (now > Volatile.Read(ref mostConcurrent)) Volatile.Write(ref mostConcurrent, now);
            try
            {
                if (tick == 2) await hold.Task;
                else await Task.Yield();
            }
            finally
            {
                Interlocked.Decrement(ref concurrent);
            }
        }, log) { Interval = TimeSpan.FromMilliseconds(5), Timeout = TimeSpan.FromMilliseconds(100) };
        using (var stop = new CancellationTokenSource())
        {
            var run = hanging.RunAsync(stop.Token);
            check(await WaitUntil(() => hanging.Skipped >= 10, 5_000), "Rounds are skipped while a tick hangs");
            check(Volatile.Read(ref startedTicks) == 2 && Volatile.Read(ref mostConcurrent) == 1,
                $"A hung tick is not started again or stacked ({startedTicks} started)");
            check(log.Count(LogLevel.Error, "did not complete within") == 1, "The hung tick is logged once");
            hold.SetResult();
            check(await WaitUntil(() => Volatile.Read(ref startedTicks) >= 4, 5_000), "Once the hung tick ended, ticking goes on");
            check(log.Contains("completed late") && Volatile.Read(ref mostConcurrent) == 1, "The late end is logged, still one tick at a time");
            await stop.CancelAsync();
            check(await Within(run, 2_000), "The loop ends when it is cancelled");
        }

        // The end does not wait for a hung tick, but a running one may end first.
        var forever = new TaskCompletionSource();
        var hung = new LauncherTickLoop(() => forever.Task, new ListLogger())
        {
            Interval = TimeSpan.FromMilliseconds(5), Timeout = TimeSpan.FromMilliseconds(50), StopWait = TimeSpan.FromSeconds(30)
        };
        using (var stop = new CancellationTokenSource())
        {
            var run = hung.RunAsync(stop.Token);
            check(await WaitUntil(() => hung.Skipped >= 2, 5_000), "The never ending tick counts as hung");
            await stop.CancelAsync();
            check(await Within(run, 2_000), "The end does not wait for a hung tick");
        }
        var slowEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new LauncherTickLoop(() =>
        {
            slowStarted.TrySetResult();
            return slowEnd.Task;
        }, new ListLogger()) { Interval = TimeSpan.FromMilliseconds(5), Timeout = TimeSpan.FromSeconds(30), StopWait = TimeSpan.FromSeconds(30) };
        using (var stop = new CancellationTokenSource())
        {
            var run = slow.RunAsync(stop.Token);
            await slowStarted.Task;
            await stop.CancelAsync();
            check(!await Within(run, 100), "The end waits for the tick that runs right now");
            slowEnd.SetResult();
            check(await Within(run, 2_000) && slow.Started == 1, "The loop ends once that tick ended, without a new one");
        }
    }

    #endregion

    #region Command line

    private static void CommandChecks(Action<bool, string> check)
    {
        // The harness built a root before: the factory gives the same one, else the edition check of a second root sees no flags.
        var command = RootCommandFactory.Create();
        check(ReferenceEquals(command, RootCommandFactory.Create()) && command.Parse(["lamb", "client", "--live", "--ptc"]).Errors.Count > 0,
            "One root per process, its edition check works however often it is asked for");
        var plain = command.Parse(["bot"]);
        check(plain.Errors.Count == 0 && plain.CommandResult.Command == BotHostCommand.Command, "boulder bot parses");
        foreach (var edition in Enum.GetValues<GameEdition>())
        {
            var flags = Constants.GetCliArg(edition).Split(' ');
            check(command.Parse(["bot", ..flags]).Errors.Count == 0, $"bot accepts the edition flags of {edition} after it");
            check(command.Parse([..flags, "bot"]).Errors.Count == 0, $"bot accepts the edition flags of {edition} before it");
            check(Constants.ParseEditionArgs(["bot", ..flags]) == edition, $"bot runs for {edition} with its flags");
        }
        check(command.Parse(["bot", "--enhanced", "--ptc", "--experiment"]).Errors.Count == 0, "bot accepts the experiment flag");
        check(command.Parse(["bot", "--live", "--ptc"]).Errors.Count > 0, "bot rejects --live with --ptc");
        check(command.Parse(["bot", "--live", "--enhanced"]).Errors.Count > 0, "bot rejects --live with --enhanced");
        foreach (var option in new[] { "--token", "--discord-token", "--bot-token", "-t", "--unknown" })
        {
            check(command.Parse(["bot", option, SampleToken]).Errors.Count > 0, $"bot rejects {option}");
            check(command.Parse(["bot", $"{option}={SampleToken}"]).Errors.Count > 0, $"bot rejects {option}=...");
        }
        check(command.Parse(["bot", SampleToken]).Errors.Count > 0, "bot takes no argument, so no token either");
        check(BotHostCommand.Command.Options.Count == 0 && BotHostCommand.Command.Arguments.Count == 0
                                                       && BotHostCommand.Command.Subcommands.Count == 0,
            "bot has no option or argument of its own: nothing that could take the token");
    }

    #endregion

    #region Installation

    private static void InstallationChecks(Action<bool, string> check)
    {
        // The lock of the window. A name of its own, so a Trebuchet that runs on this computer does not matter here.
        check(BotHostCommand.InstallationMutexName == "TotTrebuchet", "The host takes the mutex of the window (Trebuchet/Program.cs)");
        var name = "TrebuchetBotHostChecks-" + Guid.NewGuid().ToString("N");
        using (new Mutex(true, name, out var windowCreated))
        {
            check(windowCreated && BotHostCommand.TryLockInstallation(name) is null, "While the window runs the host does not get the lock");
        }
        var host = BotHostCommand.TryLockInstallation(name);
        check(host is not null, "Without the window the host gets the lock");
        using (new Mutex(true, name, out var windowCreated))
            check(!windowCreated, "While the host runs the window sees its mutex taken");
        check(BotHostCommand.TryLockInstallation(name) is null, "A second host does not get the lock");
        host!.Dispose();
        using (var again = BotHostCommand.TryLockInstallation(name))
            check(again is not null, "After the end of the host the lock is free again");

        // The services: registered for every command of Boulder, built only by the bot command.
        var services = new ServiceCollection();
        BotHostCommand.AddServices(services);
        check(services.Count > 0 && services.All(x => x.ImplementationInstance is null),
            "Registering the bot builds nothing: types and factories only");
        services.AddLogging();
        using (var provider = services.BuildServiceProvider())
        {
            var thread = provider.GetRequiredService<SerialBotDispatcher>();
            check(ReferenceEquals(thread, provider.GetRequiredService<IBotDispatcher>()),
                "The bot uses the launcher thread of the tick (one dispatcher)");
            check(services.Single(x => x.ServiceType == typeof(IBotActions)).ImplementationType == typeof(LauncherBotActions)
                  && services.Single(x => x.ServiceType == typeof(IDiscordBotService)).ImplementationType == typeof(DiscordBotService),
                "The host runs the bot of the window: same bridge, same service");
        }

        var root = Path.Combine(Path.GetTempPath(), "TrebuchetBotHostChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Settings: none means none, nothing is created.
            var path = Path.Combine(root, "discord-bot.legacy.json");
            check(BotHostCommand.LoadSettingsFile(path) is null && !File.Exists(path), "Without settings there are none, no file is created");
            DiscordBotConfig.LoadConfig(path);
            check(!Directory.EnumerateFileSystemEntries(root).Any(), "Loading missing settings writes nothing either");
            var saved = DiscordBotConfig.CreateConfig(path);
            saved.GuildId = 100000000000000001UL;
            saved.SaveFile();
            check(BotHostCommand.LoadSettingsFile(path)?.GuildId == 100000000000000001UL, "Saved settings are read");

            // A damaged file is not repaired from its backup: the backup may hold users or roles that were removed on purpose.
            saved.GuildId = 100000000000000002UL;
            saved.SaveFile();
            File.WriteAllText(path, "{ \"GuildId\": \"1000");
            var damaged = File.ReadAllText(path);
            var refused = false;
            try { BotHostCommand.LoadSettingsFile(path); }
            catch (FormatException) { refused = true; }
            check(refused && File.ReadAllText(path) == damaged && !Directory.EnumerateFiles(root, "*.corrupt-*").Any(),
                "Damaged settings are refused and left as they are, not restored from the backup");

            // The token: environment or protected file, never printed.
            var tokenPath = Path.Combine(root, "discord-bot.legacy.token");
            string? variable = null;
            var store = new BotTokenStore(tokenPath, new XorProtector(), _ => variable);
            check(BotHostCommand.CheckToken(store, _ => variable) == BotHostTokenCheck.Missing, "No token anywhere");
            variable = "not-a-token-SECRETPART";
            check(BotHostCommand.CheckToken(store, _ => variable) == BotHostTokenCheck.VariableNotAToken, "A variable without a token is told apart");
            variable = SampleToken;
            check(BotHostCommand.CheckToken(store, _ => variable) == BotHostTokenCheck.Found, "The token of the environment variable");
            variable = null;
            store.Save(SampleToken);
            check(BotHostCommand.CheckToken(store, _ => variable) == BotHostTokenCheck.Found, "The token of the protected file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    #endregion

    #region Host

    private static async Task HostChecks(Action<bool, string> check)
    {
        // Another controller: refused before anything else, a clear message without paths.
        var parts = new FakeParts { Lock = () => null };
        var (exit, log) = await RunHost(parts);
        check(exit == (int)BotHostExitCode.AlreadyRunning && parts.Calls.SequenceEqual(["lock"]),
            "With the window running the host ends with 1 before it reads anything");
        check(log.Contains("already runs") && !log.Lines.Any(x => x.Contains('/') || x.Contains('\\')), "It says why, without a path");
        parts = new FakeParts { Lock = () => throw new UnauthorizedAccessException("no access") };
        check((await RunHost(parts)).Exit == (int)BotHostExitCode.AlreadyRunning && !parts.Calls.Contains("settings"),
            "A lock that can not be taken counts as taken (fail closed)");

        // Settings: missing, unreadable, not valid. Nothing touched, the lock released.
        parts = new FakeParts { Settings = () => null };
        (exit, log) = await RunHost(parts);
        check(exit == (int)BotHostExitCode.InvalidSettings && parts.Untouched && parts.LockReleased && log.Contains("not set up"),
            "Without settings: exit 2, the servers untouched, the lock released");
        parts = new FakeParts { Settings = () => throw new IOException("damaged") };
        check((await RunHost(parts)).Exit == (int)BotHostExitCode.InvalidSettings && parts.Untouched, "Settings that can not be read: exit 2");
        parts = new FakeParts { Settings = () => new DiscordBotConfig() };
        (exit, log) = await RunHost(parts);
        check(exit == (int)BotHostExitCode.InvalidSettings && parts.Untouched && !parts.Calls.Contains("token")
              && log.Contains("server ID") && log.Contains("super admin") && log.Contains("role ID"),
            "Settings that are not valid: exit 2 and what is missing");
        var everyone = ValidConfig();
        everyone.AllowedRoleIds.Add(everyone.GuildId);
        (exit, log) = await RunHost(new FakeParts { Settings = () => everyone });
        check(exit == (int)BotHostExitCode.InvalidSettings && log.Contains("@everyone"), "The role @everyone is refused");

        // Token: none, or a variable without one. Its value is never printed.
        parts = new FakeParts { Token = BotHostTokenCheck.Missing };
        (exit, log) = await RunHost(parts);
        check(exit == (int)BotHostExitCode.NoToken && parts.Untouched && log.Contains(BotTokenStore.EnvironmentVariable),
            "Without a token: exit 3, the servers untouched, the variable named");
        var root = Path.Combine(Path.GetTempPath(), "TrebuchetBotHostChecks-" + Guid.NewGuid().ToString("N"));
        var store = new BotTokenStore(Path.Combine(root, "missing.token"), new XorProtector(), _ => "Bot not-a-token-SECRETPART");
        parts = new FakeParts { TokenCheck = () => BotHostCommand.CheckToken(store, _ => "Bot not-a-token-SECRETPART") };
        (exit, log) = await RunHost(parts);
        check(exit == (int)BotHostExitCode.NoToken && log.Contains("holds no bot token") && !log.Contains("SECRETPART"),
            "A variable without a token: exit 3, its value not printed");

        // Stopped before it began: nothing touched.
        parts = new FakeParts();
        using (var early = new CancellationTokenSource())
        {
            await early.CancelAsync();
            check((await RunHost(parts, early.Token)).Exit == (int)BotHostExitCode.Stopped && parts.Untouched, "Ctrl+C during the checks ends at once");
        }

        // The bot does not start: the exit code says why, the tick ends, everything is let go.
        foreach (var (problem, expected) in new[]
                 {
                     (DiscordBotProblem.TokenInUse, BotHostExitCode.TokenInUse),
                     (DiscordBotProblem.NoToken, BotHostExitCode.NoToken),
                     (DiscordBotProblem.InvalidSettings, BotHostExitCode.InvalidSettings),
                     (DiscordBotProblem.LoginFailed, BotHostExitCode.StartFailed),
                     (DiscordBotProblem.GuildNotFound, BotHostExitCode.StartFailed),
                     (DiscordBotProblem.Unknown, BotHostExitCode.StartFailed)
                 })
        {
            parts = new FakeParts();
            parts.Bot.OnStart = _ => (DiscordBotState.Error, problem);
            (exit, log) = await RunHost(parts);
            check(exit == (int)expected, $"{problem} at the start ends with {(int)expected}, not {exit}");
            check(parts.Bot.Stops >= 1 && parts.Bot.Disposed && parts.Dispatcher.IsStopped && parts.LockReleased,
                $"{problem}: the bot is stopped, the launcher thread ended, the lock released");
            check(log.Contains("did not start") && !log.Contains(SampleToken), $"{problem}: it says so");
        }
        parts = new FakeParts { BotFails = true };
        check((await RunHost(parts)).Exit == (int)BotHostExitCode.StartFailed && parts.Dispatcher.IsStopped, "A bot that can not be created: exit 5");

        // The normal run: attach first, then ticks, all on the launcher thread; Ctrl+C ends it cleanly with 0.
        parts = new FakeParts();
        using (var stop = new CancellationTokenSource())
        {
            log = new ListLogger();
            var run = new BotHost(parts, log, Fast).RunAsync(stop.Token);
            check(await WaitUntil(() => parts.Ticks >= 3 && parts.Bot.State == DiscordBotState.Connected, 5_000), "The host ticks and the bot runs");
            check(!run.IsCompleted && !parts.LockReleased, "It keeps running and holds the lock");
            await stop.CancelAsync();
            check(await Within(run, 10_000) && run.Result == (int)BotHostExitCode.Stopped, "Ctrl+C ends it with 0");
            var ticks = parts.Ticks;
            await Task.Delay(50);
            check(parts.Ticks == ticks, "No tick after the end");
            check(parts.Attaches == 1 && !parts.TickedBeforeAttach, "The running servers are attached once, before the first tick");
            check(parts.OffLauncherThread == 0, "Attach and tick run on the launcher thread");
            check(parts.Bot.Stops >= 1 && parts.Bot.Disposed && parts.Dispatcher.IsStopped && parts.LockReleased,
                "At the end the bot is stopped, the launcher thread ended and the lock released");
            check(log.Contains("connected to Exiles as Trebuchet") && log.Contains("servers keep running"), "It prints the state of the bot and the end");
        }

        // Ticks that fail or hang do not end the host.
        parts = new FakeParts { TickWork = n => n % 2 == 0 ? throw new InvalidOperationException("tick broke") : Task.CompletedTask };
        using (var stop = new CancellationTokenSource())
        {
            log = new ListLogger();
            var run = new BotHost(parts, log, Fast).RunAsync(stop.Token);
            check(await WaitUntil(() => parts.Ticks >= 6, 5_000) && !run.IsCompleted, "Failing ticks do not stop the host");
            await stop.CancelAsync();
            check(await Within(run, 10_000) && run.Result == 0 && log.Contains("tick broke"), "The failures are logged");
        }
        var hangs = new TaskCompletionSource();
        parts = new FakeParts { TickWork = n => n == 2 ? hangs.Task : Task.CompletedTask };
        using (var stop = new CancellationTokenSource())
        {
            log = new ListLogger();
            var run = new BotHost(parts, log, Fast with { TickTimeout = TimeSpan.FromMilliseconds(100) }).RunAsync(stop.Token);
            check(await WaitUntil(() => log.Contains("did not complete within"), 5_000) && parts.Ticks == 2, "A hung tick is skipped, not stacked");
            await stop.CancelAsync();
            check(await Within(run, 10_000) && run.Result == 0 && parts.Dispatcher.IsStopped, "The host ends even with a hung tick");
            hangs.SetResult();
        }

        // Discord not reachable at the start (e.g. after a reboot): the host runs on and starts the bot again until it connects.
        parts = new FakeParts();
        parts.Bot.OnStart = n => n < 3 ? (DiscordBotState.Error, DiscordBotProblem.ConnectionLost) : (DiscordBotState.Connected, DiscordBotProblem.None);
        using (var stop = new CancellationTokenSource())
        {
            log = new ListLogger();
            var run = new BotHost(parts, log, Fast).RunAsync(stop.Token);
            check(await WaitUntil(() => parts.Bot.State == DiscordBotState.Connected, 5_000) && parts.Bot.Starts == 3 && !run.IsCompleted,
                "Without Discord the host keeps running and connects later");
            await Task.Delay(100);
            check(parts.Bot.Starts == 3, "A connected bot is not started again");
            await stop.CancelAsync();
            check(await Within(run, 10_000) && run.Result == 0, "And ends with 0");
        }
        // Lost later and refused then (token reset meanwhile): no more tries, the server automation goes on.
        parts = new FakeParts();
        parts.Bot.OnStart = n => n == 1 ? (DiscordBotState.Connected, DiscordBotProblem.None) : (DiscordBotState.Error, DiscordBotProblem.LoginFailed);
        using (var stop = new CancellationTokenSource())
        {
            log = new ListLogger();
            var run = new BotHost(parts, log, Fast).RunAsync(stop.Token);
            check(await WaitUntil(() => parts.Bot.State == DiscordBotState.Connected, 5_000), "Connected");
            parts.Bot.Set(DiscordBotState.Error, DiscordBotProblem.ConnectionLost);
            check(await WaitUntil(() => parts.Bot.Problem == DiscordBotProblem.LoginFailed, 5_000), "Lost: started again");
            var ticks = parts.Ticks;
            await Task.Delay(200);
            check(parts.Bot.Starts == 2 && parts.Ticks > ticks && !run.IsCompleted, "A refused token is not tried again, the tick goes on");
            check(log.Contains("Discord refused the token"), "It says why");
            check(await WaitUntil(() => log.Contains("The Discord bot is not running"), 5_000), "A bot that is down for good is mentioned again in the log");
            await stop.CancelAsync();
            check(await Within(run, 10_000) && run.Result == 0, "And ends with 0");
        }
        // The server reported missing after a good start (a Discord outage, the bot removed and invited again): tried again.
        parts = new FakeParts();
        using (var stop = new CancellationTokenSource())
        {
            log = new ListLogger();
            var run = new BotHost(parts, log, Fast).RunAsync(stop.Token);
            check(await WaitUntil(() => parts.Bot.State == DiscordBotState.Connected, 5_000), "Connected");
            parts.Bot.Set(DiscordBotState.Error, DiscordBotProblem.GuildNotFound);
            check(await WaitUntil(() => parts.Bot.Starts == 2 && parts.Bot.State == DiscordBotState.Connected, 5_000) && !run.IsCompleted,
                "A server that went missing after a good start is looked for again, the host does not run on without a bot");
            await stop.CancelAsync();
            check(await Within(run, 10_000) && run.Result == 0, "And ends with 0");
        }
    }

    private static async Task<(int Exit, ListLogger Log)> RunHost(FakeParts parts, CancellationToken token = default)
    {
        var log = new ListLogger();
        var run = new BotHost(parts, log, Fast).RunAsync(token);
        if (!await Within(run, 10_000)) throw new Exception("The host did not end");
        return (run.Result, log);
    }

    private static DiscordBotConfig ValidConfig()
    {
        var config = new DiscordBotConfig { GuildId = 100000000000000001UL, SuperAdminUserId = 200000000000000001UL };
        config.AddRole(300000000000000001UL);
        return config;
    }

    #endregion

    #region Helpers

    private static async Task<bool> Within(Task task, int milliseconds)
    {
        return await Task.WhenAny(task, Task.Delay(milliseconds)) == task;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > end) return false;
            await Task.Delay(5);
        }
        return true;
    }

    private static async Task<bool> ThrowsAsync<TException>(Task task, string? message = null) where TException : Exception
    {
        try
        {
            if (!await Within(task, 5_000)) return false;
            await task;
            return false;
        }
        catch (TException ex)
        {
            return message is null || ex.Message == message;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Select(x => (byte)(x ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => data.Select(x => (byte)(x ^ 0x5A)).ToArray();
    }

    private sealed class ListLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Text)> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.Select(x => x.Text).ToList(); }
        }

        public bool Contains(string text) => Lines.Any(x => x.Contains(text, StringComparison.Ordinal));

        public int Count(LogLevel level, string text)
        {
            lock (_lines) return _lines.Count(x => x.Level == level && x.Text.Contains(text, StringComparison.Ordinal));
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception) + (exception is null ? string.Empty : " | " + exception.Message);
            lock (_lines) _lines.Add((logLevel, text));
        }
    }

    private sealed class ReleasedLock(FakeParts parts) : IDisposable
    {
        public void Dispose() => parts.LockReleased = true;
    }

    private sealed class FakeParts : IBotHostParts
    {
        private int _ticks;
        private int _attaches;
        private int _offThread;

        public FakeParts()
        {
            Lock = () => new ReleasedLock(this);
        }

        public ConcurrentQueue<string> Calls { get; } = new();
        public Func<IDisposable?> Lock { get; init; }
        public Func<DiscordBotConfig?> Settings { get; init; } = ValidConfig;
        public BotHostTokenCheck Token { get; init; } = BotHostTokenCheck.Found;
        public Func<BotHostTokenCheck>? TokenCheck { get; init; }
        public Func<int, Task>? TickWork { get; init; }
        public bool BotFails { get; init; }
        public SerialBotDispatcher Dispatcher { get; } = new();
        public FakeBot Bot { get; } = new();
        public volatile bool LockReleased;
        public volatile bool TickedBeforeAttach;

        public int Ticks => Volatile.Read(ref _ticks);
        public int Attaches => Volatile.Read(ref _attaches);
        public int OffLauncherThread => Volatile.Read(ref _offThread);
        /// <summary>No launcher work and no bot.</summary>
        public bool Untouched => Attaches == 0 && Ticks == 0 && !Calls.Contains("bot") && !Calls.Contains("dispatcher");

        public GameEdition Edition => GameEdition.Legacy;

        public IDisposable? TryLockInstallation()
        {
            Calls.Enqueue("lock");
            return Lock();
        }

        public DiscordBotConfig? LoadSettings()
        {
            Calls.Enqueue("settings");
            return Settings();
        }

        public BotHostTokenCheck CheckToken()
        {
            Calls.Enqueue("token");
            return TokenCheck?.Invoke() ?? Token;
        }

        public SerialBotDispatcher GetDispatcher()
        {
            Calls.Enqueue("dispatcher");
            return Dispatcher;
        }

        public Task AttachRunningServers()
        {
            Calls.Enqueue("attach");
            OnLauncherThread();
            Interlocked.Increment(ref _attaches);
            return Task.CompletedTask;
        }

        public Task Tick()
        {
            OnLauncherThread();
            if (Attaches == 0) TickedBeforeAttach = true;
            var tick = Interlocked.Increment(ref _ticks);
            return TickWork?.Invoke(tick) ?? Task.CompletedTask;
        }

        public IDiscordBotService CreateBot()
        {
            Calls.Enqueue("bot");
            if (BotFails) throw new InvalidOperationException("no bot");
            return Bot;
        }

        private void OnLauncherThread()
        {
            if (Environment.CurrentManagedThreadId != Dispatcher.ThreadId) Interlocked.Increment(ref _offThread);
        }
    }

    private sealed class FakeBot : IDiscordBotService
    {
        private int _starts;
        private int _stops;
        private volatile bool _disposed;

        public Func<int, (DiscordBotState, DiscordBotProblem)> OnStart { get; set; } = _ => (DiscordBotState.Connected, DiscordBotProblem.None);
        public int Starts => Volatile.Read(ref _starts);
        public int Stops => Volatile.Read(ref _stops);
        public bool Disposed => _disposed;

        public DiscordBotState State { get; private set; }
        public DiscordBotProblem Problem { get; private set; }
        public IReadOnlyList<BotConfigIssue> Issues => [];
        public string? BotName => "Trebuchet";
        public string? GuildName => "Exiles";
        public int LatencyMs => 42;
        public DateTime? ConnectedSinceUtc => null;

        public event EventHandler? StateChanged;

        public void Set(DiscordBotState state, DiscordBotProblem problem)
        {
            State = state;
            Problem = problem;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task StartAsync(CancellationToken token)
        {
            // Like the service: a running one is not started twice.
            if (State is DiscordBotState.Connected or DiscordBotState.Starting) return Task.CompletedTask;
            var start = Interlocked.Increment(ref _starts);
            Set(DiscordBotState.Starting, DiscordBotProblem.None);
            var (state, problem) = OnStart(start);
            Set(state, problem);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stops);
            Set(DiscordBotState.Stopped, DiscordBotProblem.None);
            return Task.CompletedTask;
        }

        public Task ApplySettingsAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<BotCheckItem>> CheckSecurityAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<BotCheckItem>>([]);

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    #endregion
}
