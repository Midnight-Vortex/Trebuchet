using Microsoft.Extensions.Logging;
using TrebuchetLib.Services;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// The bridge from the bot into the launcher. Every launcher access runs through the <see cref="IBotDispatcher"/> on the
/// thread the launcher is used on. Actions that take minutes (countdowns, starts, updates) are started there and go on
/// in the background: the answer waits only a moment for them and their later failure is logged. Never throws.
/// </summary>
public sealed class LauncherBotActions : IBotActions, IDisposable
{
    /// <summary>A scheduled restart further ahead is not shown as pending.</summary>
    public static readonly TimeSpan ScheduledRestartLookAhead = TimeSpan.FromHours(1);
    // Hard limit of a delay or postponement, whatever the settings say.
    private static readonly TimeSpan MaxDelay = TimeSpan.FromHours(24);
    // How long an answer waits for a launcher action that is still busy (Discord wants an answer within seconds).
    private static readonly TimeSpan ResultWait = TimeSpan.FromSeconds(2);
    // The launcher thread did not take the work within this time: it is given up and does not run any more.
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);

    private readonly Launcher _launcher;
    private readonly AppSetup _setup;
    private readonly AppFiles _files;
    private readonly IBotDispatcher _dispatcher;
    private readonly ILogger<LauncherBotActions> _logger;

    public LauncherBotActions(Launcher launcher, AppSetup setup, AppFiles files, IBotDispatcher dispatcher,
        ILogger<LauncherBotActions> logger)
    {
        _launcher = launcher;
        _setup = setup;
        _files = files;
        _dispatcher = dispatcher;
        _logger = logger;
        _launcher.ServerStatusChanged += OnLauncherStatusChanged;
    }

    public event EventHandler? StatusChanged;

    public void Dispose() => _launcher.ServerStatusChanged -= OnLauncherStatusChanged;

    private void OnLauncherStatusChanged(object? sender, EventArgs e)
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Discord bot: a listener of the status change failed");
        }
    }

    public int InstanceCount => Math.Max(0, _setup.Config.ServerInstanceCount);

    public async Task<IReadOnlyList<BotServerStatus>> GetStatusesAsync(CancellationToken token)
    {
        var run = await DispatchAsync("status", _ => Task.FromResult(_launcher.GetServerStatuses()), token);
        if (run.End != DispatchEnd.Ran || run.Value is not { } statuses) return [];
        try
        {
            var utcNow = DateTime.UtcNow;
            return statuses.Where(x => !string.IsNullOrWhiteSpace(x.SaveName))
                .Select(x => ToBotStatus(x, utcNow))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord bot: could not read the server states");
            return [];
        }
    }

    public Task<BotActionResult> PostponeAsync(int instance, int minutes, string reason, CancellationToken token)
    {
        var text = BotInputRules.SanitizeLauncherReason(reason);
        var parameters = $"{minutes} min, reason: {text}";
        if (minutes < 1 || TimeSpan.FromMinutes(minutes) > MaxDelay)
            return Task.FromResult(Report("postpone", instance, parameters, Fail("invalid minutes")));
        var pause = TimeSpan.FromMinutes(minutes);
        return ActAsync("postpone", instance, parameters, async (status, _, t) =>
        {
            if (Unavailable(status) is { } refused) return refused;
            // A pending stop is planned anew: its countdown is replaced by one that ends in the chosen time.
            if (status.CountdownRunning && !status.CountdownRestarts)
            {
                // The new end is the old end plus the minutes: postponing must never bring the stop forward.
                var now = DateTime.UtcNow;
                var remaining = status.CountdownEndsUtc is { } end && end > now ? end - now : TimeSpan.Zero;
                var replan = _launcher.PostponeStop(instance, remaining + pause);
                if (replan.IsCompletedSuccessfully && !await replan) return new BotActionResult(BotActionOutcome.NothingPending);
                return await FinishAsync(replan, "postpone stop", instance, wait: false, t);
            }
            // A restart or update countdown is moved like a stop: it ends at the old end plus the minutes (never sooner than
            // before, as a pause of the minutes from now would). The restart is postponed (the players are told) and runs when
            // the pause is over, earlier if the pause is lifted.
            if (status.CountdownRunning && status.CountdownRestarts)
            {
                var replan = _launcher.PostponeRestart(instance, pause);
                if (!(replan.IsCompletedSuccessfully && !await replan))
                    return await FinishAsync(replan, "postpone", instance, wait: true, t);
            }
            // Already postponed: the pause is put off by the minutes (a new pause of the minutes from now could end sooner than the one that runs).
            if (status.PausedUntilUtc is { } until && until > DateTime.UtcNow)
            {
                var extend = _launcher.ExtendAutomaticRestartsPause(instance, pause);
                if (!(extend.IsCompletedSuccessfully && !await extend))
                    return await FinishAsync(extend, "postpone", instance, wait: true, t);
            }
            // Anything else automatic waits for the end of the pause.
            return await FinishAsync(_launcher.PauseAutomaticRestarts(instance, pause), "postpone", instance, wait: true, t);
        }, token);
    }

    public Task<BotActionResult> ResumeAsync(int instance, CancellationToken token)
    {
        return ActAsync("resume", instance, string.Empty, (_, _, _) =>
        {
            if (_launcher.GetAutomaticRestartsPause(instance) is null)
                return Task.FromResult(new BotActionResult(BotActionOutcome.NothingPending));
            _launcher.ResumeAutomaticRestarts(instance);
            return Task.FromResult(new BotActionResult(BotActionOutcome.Done));
        }, token);
    }

    public Task<BotActionResult> CancelAsync(int instance, CancellationToken token)
    {
        return ActAsync("cancel", instance, string.Empty, async (status, _, t) =>
        {
            if (Unavailable(status) is { } refused) return refused;
            if (!status.CountdownRunning) return new BotActionResult(BotActionOutcome.NothingPending);
            var cancel = _launcher.CancelCountdown(instance);
            if (cancel.IsCompletedSuccessfully && await cancel is null) return new BotActionResult(BotActionOutcome.NothingPending);
            var done = await FinishAsync(cancel, "cancel", instance, wait: true, t);
            if (!done.Success || !cancel.IsCompletedSuccessfully) return done;
            // A restart the schedule or an update started on its own holds the automatic restarts for a while: the answer says so.
            return cancel.Result switch
            {
                null => new BotActionResult(BotActionOutcome.NothingPending),
                { } hold when hold > TimeSpan.Zero => new BotActionResult(BotActionOutcome.Done, $"hold={(int)Math.Round(hold.TotalMinutes)}"),
                _ => done
            };
        }, token);
    }

    public Task<BotActionResult> RestartAsync(int instance, TimeSpan delay, string reason, CancellationToken token)
    {
        var text = BotInputRules.SanitizeLauncherReason(reason);
        var parameters = $"in {(int)Math.Ceiling(delay.TotalMinutes)} min, reason: {text}";
        if (delay < TimeSpan.Zero || delay > MaxDelay)
            return Task.FromResult(Report("restart", instance, parameters, Fail("invalid delay")));
        return ActAsync("restart", instance, parameters, async (status, _, t) =>
        {
            if (Unavailable(status) is { } refused) return refused;
            var restart = _launcher.RestartServerIn(instance, delay, text.Length > 0 ? text : null);
            return await FinishAsync(restart, "restart", instance, wait: delay <= TimeSpan.Zero, t);
        }, token);
    }

    public Task<BotActionResult> StopAsync(int instance, TimeSpan delay, string reason, CancellationToken token)
    {
        var text = BotInputRules.SanitizeLauncherReason(reason);
        var parameters = $"in {(int)Math.Ceiling(delay.TotalMinutes)} min, reason: {text}";
        if (delay < TimeSpan.Zero || delay > MaxDelay)
            return Task.FromResult(Report("stop", instance, parameters, Fail("invalid delay")));
        return ActAsync("stop", instance, parameters, async (status, _, t) =>
        {
            if (Unavailable(status) is { } refused) return refused;
            var stop = _launcher.CloseServerIn(instance, delay, text.Length > 0 ? text : null);
            return await FinishAsync(stop, "stop", instance, wait: delay <= TimeSpan.Zero, t);
        }, token);
    }

    public Task<BotActionResult> StartAsync(int instance, CancellationToken token)
    {
        return ActAsync("start", instance, string.Empty, async (status, all, t) =>
        {
            if (status.State is { } state)
                return state.IsRunning() && !state.IsStopping()
                    ? new BotActionResult(BotActionOutcome.AlreadyRunning)
                    : new BotActionResult(BotActionOutcome.Busy, "stopping");
            if (status.Starting) return new BotActionResult(BotActionOutcome.Busy, "starting");
            if (status.ShuttingDown) return new BotActionResult(BotActionOutcome.Busy, "stopping");
            if (!status.Installed) return Fail("instance not installed");
            // The launcher would fall back to another save or mod list: the bot only starts what was chosen.
            if (!HasSaveAndModList(instance)) return Fail("no save or mod list selected");
            // One save cannot run twice (the launcher refuses it as locked).
            if (all.Any(x => x.Instance != instance && (x.State is not null || x.Starting)
                             && string.Equals(x.SaveName, status.SaveName, StringComparison.OrdinalIgnoreCase)))
                return new BotActionResult(BotActionOutcome.Busy, "save in use by another instance");
            // Pre-launch update, starting sequence and the lock of the save are the launcher's, as for the dashboard.
            return await FinishAsync(_launcher.CatapultServer(instance), "start", instance, wait: true, t);
        }, token);
    }

    public Task<BotActionResult> UpdateAsync(CancellationToken token)
    {
        return ActAsync("update", null, string.Empty, async (_, _, t) =>
        {
            if (_setup.Config.AutoUpdateStatus == AutoUpdateStatus.Never)
                return Fail("updates are switched off in the settings");
            var update = _launcher.PerformUpdatesIfIdle();
            if (!await WaitBrieflyAsync(update, "update", -1, ResultWait, t))
                return new BotActionResult(BotActionOutcome.Done, "under way");
            if (!update.IsCompletedSuccessfully) return FailureOf(update, "update", -1);
            var updated = await update;
            return updated switch
            {
                null => new BotActionResult(BotActionOutcome.Busy, "a start, update or download runs"),
                true => new BotActionResult(BotActionOutcome.Done),
                false => Fail("update failed")
            };
        }, token);
    }

    public Task<BotActionResult> CheckUpdatesAsync(CancellationToken token)
    {
        return ActAsync("checkupdates", null, string.Empty, async (_, _, t) =>
        {
            // Unlike an update, the answer is the result: it waits for Steam (the dispatch has its own limit).
            var check = _launcher.CheckForUpdatesNow();
            LauncherUpdateCheck? found;
            try
            {
                found = await check.WaitAsync(t);
            }
            catch (OperationCanceledException) when (t.IsCancellationRequested)
            {
                return Fail("cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discord bot: the update check failed");
                return Fail("the update check failed");
            }
            if (found is null) return new BotActionResult(BotActionOutcome.Busy, "a start, update or download runs");
            if (!found.Succeeded) return Fail("Steam did not answer");
            return new BotActionResult(BotActionOutcome.Done, $"mods={found.OutdatedMods} server={found.ServerUpdate}",
                new BotUpdateInfo(found.OutdatedMods, found.ServerUpdate));
        }, token);
    }

    /// <summary>
    /// The status the bot shows for a launcher status: state, players while online, what is about to happen and when.
    /// </summary>
    public static BotServerStatus ToBotStatus(LauncherServerStatus status, DateTime utcNow)
    {
        var state = StateOf(status);
        var online = state == BotServerState.Online;
        var (pending, pendingAt) = state is BotServerState.Online or BotServerState.Starting && status.State is not null
            ? PendingOf(status, utcNow)
            : (BotPendingAction.None, null);
        return new BotServerStatus(
            status.Instance,
            // The name of the server (first field of its save), the name of the save while that keeps its default.
            DisplayName(status),
            state,
            online ? Math.Max(0, status.Players) : 0,
            online ? Math.Max(0, status.MaxPlayers) : 0,
            status.State is not null ? status.StartedUtc : null,
            pending,
            pendingAt,
            status.PausedUntilUtc is { } paused && paused > utcNow ? paused : null,
            status.ProcessRestarts || status.RestartRequested || (status.CountdownRunning && status.CountdownRestarts),
            status.CountdownRunning,
            status.CountdownRunning && status.CountdownRestarts && status.CountdownAutomatic,
            status.SequenceRunning && !status.CountdownRunning);
    }

    private static BotServerState StateOf(LauncherServerStatus status) => status.State switch
    {
        ProcessState.ONLINE => BotServerState.Online,
        ProcessState.NEW or ProcessState.RUNNING => BotServerState.Starting,
        ProcessState.STOPPING => BotServerState.Stopping,
        ProcessState.CRASHED or ProcessState.FAILED => BotServerState.Crashed,
        ProcessState.STOPPED => BotServerState.Stopped,
        // No server process.
        _ => status.Starting ? BotServerState.Starting
            : status.ShuttingDown ? BotServerState.Stopping
            : status.Crashed ? BotServerState.Crashed
            : BotServerState.Stopped
    };

    private static (BotPendingAction, DateTime?) PendingOf(LauncherServerStatus status, DateTime utcNow)
    {
        var update = status.InUpdateRound ? BotPendingAction.Update : BotPendingAction.Restart;
        if (status.CountdownRunning)
            return (status.CountdownRestarts ? update : BotPendingAction.Stop, status.CountdownEndsUtc);
        if (status.RestartRequested) return (update, null);
        // A starting or stopping sequence of the save: nothing automatic happens meanwhile.
        if (status.SequenceRunning) return (BotPendingAction.None, null);
        // Postponed restarts and updates begin once the pause is over, and the server goes down after the warnings.
        var pauseEnd = status.PausedUntilUtc is { } paused && paused > utcNow ? paused + status.RestartWarningLead : (DateTime?)null;
        if (status.UpdatePending) return (BotPendingAction.Update, pauseEnd);
        if (status.RestartPostponed) return (BotPendingAction.Restart, pauseEnd);
        if (status.NextScheduledRestartUtc is { } next && next - utcNow <= ScheduledRestartLookAhead)
            return (BotPendingAction.Restart, next);
        return (BotPendingAction.None, null);
    }

    private static string DisplayName(LauncherServerStatus status)
    {
        var name = BotInputRules.SanitizeName(status.ServerName);
        return name.Length > 0 ? name : SafeName(status.SaveName);
    }

    // A save name is a folder name. Defensive anyway: never a path, one clean line.
    private static string SafeName(string name)
    {
        var last = name.Replace('\\', '/');
        var cut = last.LastIndexOf('/');
        if (cut >= 0) last = last[(cut + 1)..];
        var clean = BotInputRules.SanitizeForLog(last);
        return clean.Length > 0 ? clean : "-";
    }

    // Postpone, restart and stop need a running server that is not busy with something they would break.
    private static BotActionResult? Unavailable(LauncherServerStatus status)
    {
        if (status.State is not { } state)
            return new BotActionResult(BotActionOutcome.NotRunning, status.Starting ? "starting" : string.Empty);
        if (state.IsStopping()) return new BotActionResult(BotActionOutcome.Busy, "stopping");
        if (!state.IsRunning()) return new BotActionResult(BotActionOutcome.NotRunning);
        // A stopping or starting sequence of the save is not interrupted (countdowns are replaced or postponed).
        if (status.SequenceRunning && !status.CountdownRunning) return new BotActionResult(BotActionOutcome.Busy, "sequence running");
        if (status.RestartRequested && !status.CountdownRunning) return new BotActionResult(BotActionOutcome.Busy, "restart under way");
        return null;
    }

    private bool HasSaveAndModList(int instance)
    {
        var config = _setup.Config;
        return _files.Server.TryResolve(config.GetInstanceProfile(instance), out _)
               && _files.TryParseModListRef(config.GetInstanceModlist(instance), out var modList)
               && modList.TryGetModList(out _);
    }

    /// <summary>
    /// Runs an action on the launcher thread with the current state of all instances. <paramref name="instance"/> null:
    /// the action is not about one instance.
    /// </summary>
    private async Task<BotActionResult> ActAsync(string action, int? instance, string parameters,
        Func<LauncherServerStatus, IReadOnlyList<LauncherServerStatus>, CancellationToken, Task<BotActionResult>> act,
        CancellationToken token)
    {
        var shown = instance ?? -1;
        if (instance is { } checkedInstance && (checkedInstance < 0 || checkedInstance >= InstanceCount))
            return Report(action, shown, parameters, Fail("invalid instance"));

        var serverName = string.Empty;
        var run = await DispatchAsync(action, async t =>
        {
            var all = _launcher.GetServerStatuses();
            // The settings may have changed while the work waited for the launcher thread.
            var status = instance is { } i ? all.FirstOrDefault(x => x.Instance == i) : null;
            if (instance is not null && status is null) return Fail("invalid instance");
            if (status is not null) serverName = DisplayName(status);
            return await act(status ?? new LauncherServerStatus { Instance = -1, SaveName = string.Empty }, all, t);
        }, token);

        var result = run.End switch
        {
            DispatchEnd.Ran => run.Value ?? Fail("no result"),
            DispatchEnd.Cancelled => Fail("cancelled"),
            DispatchEnd.TimedOut => Fail("the launcher did not answer"),
            _ => Fail("error")
        };
        return Report(action, shown, parameters, result, serverName);
    }

    /// <summary>
    /// Starts a launcher action and waits a moment for it. Done when it finished or still runs (countdowns, starts and
    /// updates take minutes), Failed when it threw.
    /// </summary>
    private async Task<BotActionResult> FinishAsync(Task task, string action, int instance, bool wait, CancellationToken token)
    {
        if (!await WaitBrieflyAsync(task, action, instance, wait ? ResultWait : TimeSpan.Zero, token))
            return new BotActionResult(BotActionOutcome.Done, "under way");
        return task.IsCompletedSuccessfully ? new BotActionResult(BotActionOutcome.Done) : FailureOf(task, action, instance);
    }

    /// <returns>False when the task still runs: it goes on in the background, a failure is logged.</returns>
    private async Task<bool> WaitBrieflyAsync(Task task, string action, int instance, TimeSpan wait, CancellationToken token)
    {
        if (!task.IsCompleted && wait > TimeSpan.Zero)
            await Task.WhenAny(task, Task.Delay(wait, token));
        if (task.IsCompleted) return true;
        _ = ObserveAsync(task, action, instance);
        return false;
    }

    private async Task ObserveAsync(Task task, string action, int instance)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord bot: {Action} of instance {Instance} failed", action, instance);
        }
    }

    private BotActionResult FailureOf(Task task, string action, int instance)
    {
        if (task.IsCanceled) return Fail("cancelled");
        _logger.LogError(task.Exception?.GetBaseException(), "Discord bot: {Action} of instance {Instance} failed", action, instance);
        return Fail("the launcher could not do it");
    }

    private static BotActionResult Fail(string detail) => new(BotActionOutcome.Failed, detail);

    private BotActionResult Report(string action, int instance, string parameters, BotActionResult result, string serverName = "")
    {
        // "0 (Name of the server)": the log names the server, not only its number.
        var server = instance < 0 ? "-" : serverName.Length > 0 ? $"{instance} ({serverName})" : instance.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _logger.Log(result.Outcome == BotActionOutcome.Failed ? LogLevel.Warning : LogLevel.Information,
            "Discord bot: {Action} server {Server} {Parameters}: {Outcome} {Detail}",
            action, server, parameters, result.Outcome, result.Detail);
        return result;
    }

    private enum DispatchEnd { Ran, Cancelled, TimedOut, Failed }

    private readonly record struct Dispatched<T>(DispatchEnd End, T? Value);

    /// <summary>
    /// Runs the work on the launcher thread. Work that has not started when the caller gives up (cancelled, timed out)
    /// never runs: the caller was told it did not happen. Exceptions are logged, never thrown.
    /// </summary>
    private async Task<Dispatched<T>> DispatchAsync<T>(string action, Func<CancellationToken, Task<T>> work, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new Dispatched<T>(DispatchEnd.Cancelled, default);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(DispatchTimeout);
        // Taken now: work released after this method returned must not depend on the disposed source.
        var limitToken = limit.Token;
        var gate = new RunGate();
        Task<Dispatched<T>> task;
        try
        {
            task = _dispatcher.InvokeAsync(async () =>
            {
                if (!gate.TryRun()) return new Dispatched<T>(DispatchEnd.Cancelled, default);
                try
                {
                    return new Dispatched<T>(DispatchEnd.Ran, await work(limitToken));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Discord bot: {Action} failed", action);
                    return new Dispatched<T>(DispatchEnd.Failed, default);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord bot: {Action} could not be handed to the launcher", action);
            return new Dispatched<T>(DispatchEnd.Failed, default);
        }

        try
        {
            return await task.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            if (gate.TryAbandon())
                return new Dispatched<T>(token.IsCancellationRequested ? DispatchEnd.Cancelled : DispatchEnd.TimedOut, default);
            // It runs already and its waits end with the token: what it did is the answer.
            try
            {
                return await task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discord bot: {Action} failed", action);
                return new Dispatched<T>(DispatchEnd.Failed, default);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord bot: {Action} failed", action);
            return new Dispatched<T>(DispatchEnd.Failed, default);
        }
    }

    // Decides once between running queued work and giving it up.
    private sealed class RunGate
    {
        private int _state;
        public bool TryRun() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        public bool TryAbandon() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
    }
}
