using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using tot_lib;
using tot_lib.OsSpecific;
using TrebuchetLib.DiscordBot;
using TrebuchetLib.OsSpecific;
using TrebuchetLib.Processes;
using TrebuchetLib.Sequences;
using TrebuchetLib.YuuIni;

namespace TrebuchetLib.Services;

public class Launcher : IDisposable, IProgress<SequenceProgress>
{
    public Launcher(AppFiles appFiles, 
        IOsPlatformSpecific osSpecific,
        ITrebuchetOsSpecific tOsSpecific,
        AppSetup setup, 
        Steam steam,
        BackupManager backupManager,
        ConanProcessFactory processFactory,
        ILogger<Launcher> logger)
    {
        _appFiles = appFiles;
        _osSpecific = osSpecific;
        _tOsSpecific = tOsSpecific;
        _setup = setup;
        _steam = steam;
        _backupManager = backupManager;
        _processFactory = processFactory;
        _logger = logger;

        _startDates = LoadStartDates();
    }
    
    private readonly Dictionary<int, IConanServerProcess> _serverProcesses = [];
    private readonly Dictionary<int, SequenceRunner> _serverSequences = [];
    private IConanProcess? _conanClientProcess;
    private bool _hasCatapulted;
    private int _tickCounter;
    private readonly List<IPRefWithModList> _modListNeedUpdate = [];
    private readonly List<PublishedMod> _modNeedUpdate = [];
    private List<ulong> _reportedModUpdates = [];
    private bool _updateWaitLogged;
    private readonly HashSet<ulong> _failedModUpdates = [];
    private readonly ServerUpdateTracker _serverUpdate = new();
    private DateTime _nextUpdateCheckUtc;
    private bool _lastUpdateCheckFailed;
    // A failed check (Steam unreachable) is retried after this delay instead of on every 2 s tick.
    private static readonly TimeSpan UpdateCheckRetryDelay = TimeSpan.FromMinutes(1);
    private readonly List<StartDates> _startDates;
    private readonly AppFiles _appFiles;
    private readonly IOsPlatformSpecific _osSpecific;
    private readonly ITrebuchetOsSpecific _tOsSpecific;
    private readonly AppSetup _setup;
    private readonly Steam _steam;
    private readonly BackupManager _backupManager;
    private readonly ConanProcessFactory _processFactory;
    private readonly ILogger<Launcher> _logger;
    private string _lastRestartReason = string.Empty;
    private DateTime _lastExternalIpCheck = DateTime.MinValue;
    private static readonly TimeSpan InstalledCountMaxAge = TimeSpan.FromSeconds(5);
    private int _installedCount;
    private DateTime _installedCountAtUtc;
    private readonly HashSet<int> _countdownInstances = [];
    // Reason of the restart a running countdown leads to (stop countdowns are not in it).
    private readonly Dictionary<int, string> _restartCountdownReasons = [];
    // When the running countdown of an instance ends (its restart or stop), for GetServerStatuses.
    private readonly Dictionary<int, DateTime> _countdownEndsUtc = [];
    // Instances whose last server process crashed and no new one runs yet, for GetServerStatuses.
    private readonly HashSet<int> _crashedInstances = [];
    // Restarts whose countdown was cancelled by a pause: they run when the pause is over, or earlier when the pause is lifted.
    private readonly Dictionary<int, PostponedRestart> _postponedRestarts = [];
    // The reason of a postponed restart and when its countdown was to end before it was postponed (null: not known).
    // Manual: the countdown before it was started by hand (the window, Discord), not by the schedule or an update.
    private sealed record PostponedRestart(string Reason, DateTime? PlannedEndUtc, bool Manual);
    private static readonly TimeSpan MinimumPause = TimeSpan.FromMinutes(1);
    // Countdowns somebody started by hand (the window, Discord): the schedule and the updates did not start them.
    private readonly HashSet<int> _manualCountdowns = [];
    // When somebody cancelled the countdown of an instance (until a new countdown begins or the server is gone).
    private readonly Dictionary<int, DateTime> _countdownCancelledUtc = [];
    // Stops and restarts without countdown that are under way (counted: two may overlap), for GetServerStatuses.
    private readonly Dictionary<int, int> _goingDown = [];

    private IDisposable GoingDown(int instance)
    {
        _goingDown[instance] = _goingDown.GetValueOrDefault(instance) + 1;
        return new GoingDownScope(this, instance);
    }

    private sealed class GoingDownScope(Launcher launcher, int instance) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            var left = launcher._goingDown.GetValueOrDefault(instance) - 1;
            if (left <= 0) launcher._goingDown.Remove(instance);
            else launcher._goingDown[instance] = left;
        }
    }
    // Automatic restarts (schedule, updates, RequiresRestart.txt) paused per instance, e.g. during an RP event.
    private readonly Dictionary<int, DateTime> _automaticRestartsPausedUntil = [];
    public const int CrashRestartLimit = 3;
    public static readonly TimeSpan CrashRestartWindow = TimeSpan.FromMinutes(15);
    private readonly CrashRestartGuard _crashGuard = new(CrashRestartLimit, CrashRestartWindow);
    // Instances started from the tick that are still in their starting sequence.
    private readonly HashSet<int> _backgroundStarts = [];
    // Instances whose server process is being launched right now (updates, ini, process start).
    private readonly HashSet<int> _launchingInstances = [];
    // Launching instances past their pre-launch update: their files are fresh and about to be in use.
    private readonly HashSet<int> _startingProcesses = [];
    // One pre-launch update at a time: starts of several instances at once updated the same files concurrently.
    private readonly SemaphoreSlim _prelaunchUpdateGate = new(1, 1);
    // Instances with a restart on its way (countdown, Discord notice): the tick must not request it again.
    private readonly HashSet<int> _restartsPending = [];
    // When the user last stopped or killed an instance: a failed automatic start is not retried after that.
    private readonly Dictionary<int, DateTime> _manualStopsUtc = [];
    public static readonly TimeSpan StartRetryDelay = TimeSpan.FromMinutes(1);
    // Backup and scripts after a stop run outside the tick: a long backup froze the monitoring of every server.
    private readonly Dictionary<int, Task> _shutdownTasks = [];
    // Update round: instances restarted together so an update can install (it needs them all stopped). Their starts
    // are held until every instance of the round is stopped, then the first start installs the update.
    private readonly HashSet<int> _updateRound = [];
    private readonly HashSet<int> _heldStarts = [];
    private DateTime _heldSinceUtc;
    // Grace for a server of the round that is not on its way to a restart (its restart failed or was cancelled).
    private static readonly TimeSpan HeldStartTimeout = TimeSpan.FromMinutes(5);
    private bool _updateRoundWaitLogged;
    // Instances still running after they were reported as crashed (kill pending or failed).
    private readonly HashSet<int> _crashedStillRunning = [];

    public event EventHandler? StateChanged;
    public event EventHandler<SequenceProgress>? SequenceProgressChanged; 

    /// <summary>
    /// Something <see cref="GetServerStatuses"/> shows has changed: a server started, went online (the game log says so),
    /// is stopping, stopped or crashed, a countdown began or ended, a pause or postponement was set. Raised on whatever
    /// thread noticed it, not only on the launcher thread: handlers only take note (wake a loop) and read the state later.
    /// </summary>
    public event EventHandler? ServerStatusChanged;

    public void Dispose()
    {
        _conanClientProcess?.Dispose();
        _conanClientProcess = null;
        foreach (var item in _serverProcesses)
            item.Value.Dispose();
        _serverProcesses.Clear();
    }

    public async Task CatapultClient(bool isBattleEye, ClientConnectionRef? autoConnect)
    {
        var profile = _appFiles.Client.Resolve(_setup.Config.SelectedClientProfile);
        var modlist = _appFiles.ResolveModList(_setup.Config.SelectedClientModlist);
        await CatapultClient(profile, modlist, isBattleEye, autoConnect);
    }

    public async Task<Process> CatapultClientProcess(bool isBattleEye, ClientConnectionRef? autoConnect)
    {
        var profile = _appFiles.Client.Resolve(_setup.Config.SelectedClientProfile);
        var modlist = _appFiles.ResolveModList(_setup.Config.SelectedClientModlist);
        return await CatapultClientProcess(profile, modlist, isBattleEye, autoConnect);
    }

    /// <summary>
    ///     Launch a client process while taking care of everything. Generate the modlist, generate the ini settings, etc.
    ///     Process is created on a separate thread, and fire the event ClientProcessStarted when the process is running.
    /// </summary>
    /// <param name="profileName"></param>
    /// <param name="modlistName"></param>
    /// <param name="isBattleEye">Launch with BattlEye anti cheat.</param>
    /// <param name="autoConnect">Launch and try to connect to a server automatically</param>
    /// <exception cref="FileNotFoundException"></exception>
    /// <exception cref="ArgumentException">
    ///     Profiles can only be used by one process at a times, since they contain the db of
    ///     the game.
    /// </exception>
    public async Task CatapultClient(ClientProfileRef profileName, IPRefWithModList modlistName, bool isBattleEye, ClientConnectionRef? autoConnect)
    {
        if (_conanClientProcess != null) return;

        var process = await CatapultClientProcess(profileName, modlistName, isBattleEye, autoConnect);

        _conanClientProcess = await _processFactory.Create().SetProcess(process).BuildClient();
        OnStateChanged();
    }

    public async Task<Process> CatapultClientProcess(ClientProfileRef profileRef, IPRefWithModList modListRef, bool isBattleEye, ClientConnectionRef? autoConnect)
    {
        var data = new Dictionary<string, object>
        {
            {@"profile", profileRef},
            {"modlist", modListRef},
            {"isBattleEye", isBattleEye}
        };
        if (autoConnect is not null)
            data["autoConnect"] = autoConnect.Connection;
        
        using var scope = _logger.BeginScope(data);
        _logger.LogInformation("Launching");
        
        if (!_appFiles.Client.TryGet(profileRef, out var profile))
            throw new Exception($"{profileRef} profile not found.");
        if (!modListRef.TryGetModList(out var modList))
            throw new Exception($"{modListRef} modlist not found.");
        if (IsClientProfileLocked(profileRef))
            throw new Exception($"Profile {profileRef} folder is currently locked by another process.");

        SetupJunction(_setup.GetPrimaryJunction(), profile.ProfileFolder);

        await _setup.WriteIni(profile);
        
        if (autoConnect is not null && autoConnect.TryGet(out var connection))
        {
            if (!IsAutoConnectInfoValid(connection))
                throw new Exception("Auto connection address is invalid");
            await _setup.WriteLastConnection(connection);
        }

        await EnsureWorkshopModsCompatible(modList);
        
        var process = await CreateClientProcess(profile, modList, isBattleEye, autoConnect is not null);
        var args = process.StartInfo.Arguments ?? string.Empty;
        _logger.LogInformation(
            "Starting client process: exe={Exe}, workDir={WorkDir}, battleEye={BattleEye}, argsLength={ArgsLength}, args={Args}",
            process.StartInfo.FileName,
            process.StartInfo.WorkingDirectory,
            isBattleEye,
            args.Length,
            args);
        process.Start();
        _logger.LogInformation("Client parent process started PID={Pid}", process.Id);

        var childProcess = await CatchClientChildProcess(process);
        if (childProcess == null)
            throw new Exception("Could not launch the game");
        
        ConfigureProcess(profile.ProcessPriority, profile.CPUThreadAffinity, childProcess);

        return childProcess;
    }

    public async Task CatapultServer(int instance)
    {
        var profile = _appFiles.Server.Resolve(_setup.Config.GetInstanceProfile(instance));
        var modlist = _appFiles.ResolveModList(_setup.Config.GetInstanceModlist(instance));
        await CatapultServer(profile, modlist, instance);
    }

    public async Task<Process> CatapultServerProcess(int instance)
    {
        var profile = _appFiles.Server.Resolve(_setup.Config.GetInstanceProfile(instance));
        var modlist = _appFiles.ResolveModList(_setup.Config.GetInstanceModlist(instance));
        return await CatapultServerProcess(profile, modlist, instance);
    }
    
    /// <summary>
    ///     Launch a server process while taking care of everything. Generate the modlist, generate the ini settings, etc.
    ///     Process is created on a separate thread, and fire the event ServerProcessStarted when the process is running.
    /// </summary>
    /// <param name="profileName"></param>
    /// <param name="listRef"></param>
    /// <param name="instance">Index of the instance you want to launch</param>
    /// <exception cref="FileNotFoundException"></exception>
    /// <exception cref="ArgumentException">
    ///     Profiles can only be used by one process at a times, since they contain the db of
    ///     the game.
    /// </exception>
    public async Task CatapultServer(ServerProfileRef profileName, IPRefWithModList listRef, int instance)
    {
        if (_serverProcesses.ContainsKey(instance)) return;
        // The backup after the last stop may still copy this save: start once it is done.
        if (_shutdownTasks.TryGetValue(instance, out var shutdown))
            await shutdown;
        if (_serverProcesses.ContainsKey(instance)) return;
        
        var profile = _appFiles.Server.Get(profileName);
        if (profile.StartingSequence.Actions.Count > 0 && !_serverSequences.ContainsKey(instance))
        {
            await CatapultServerSequence(instance, profile);
            return;
        }

        // Two starts of one instance (button and automatic start) must not launch two servers on the same save.
        if (!_launchingInstances.Add(instance)) return;
        OnServerStatusChanged();
        try
        {
            var process = await CatapultServerProcess(profileName, listRef, instance);

            var builder = _processFactory.Create()
                .SetProcess(process)
                .SetServerInfos(profile, instance)
                .SetLogFile(_appFiles.Server.GetGameLogs(profileName))
                .StartLogAtBeginning();
            if (profile.EnableRCon)
                builder.UseRCon();

            var serverProcess = await builder.BuildServer();
            if (!_serverProcesses.TryAdd(instance, serverProcess))
            {
                // Attached meanwhile by the process scan: this second watcher (query thread, log reader, RCon) leaked.
                serverProcess.Dispose();
                return;
            }
            serverProcess.StateChanged += OnServerStateChanged;
            _crashedInstances.Remove(instance);
            OnStateChanged();
        }
        finally
        {
            _launchingInstances.Remove(instance);
            _startingProcesses.Remove(instance);
            OnServerStatusChanged();
        }
    }

    /// <summary>
    /// Starts a server from the tick without waiting for it. A starting sequence can take minutes (e.g. wait until
    /// online) and waits for states only the tick updates: awaiting it in the tick froze every refresh until its
    /// timeout, so the dashboard showed the running server as stopped.
    /// </summary>
    private void CatapultServerInBackground(int instance)
    {
        if (!_backgroundStarts.Add(instance)) return;
        OnServerStatusChanged();
        _ = RunBackgroundStart(instance);
    }

    private async Task RunBackgroundStart(int instance)
    {
        var attemptUtc = DateTime.UtcNow;
        try
        {
            await CatapultServer(instance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server {Server}", GetInstanceLabel(instance));
        }
        finally
        {
            _backgroundStarts.Remove(instance);
            OnServerStatusChanged();
        }

        try
        {
            await RetryFailedStart(instance, attemptUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retry the start of server {Server}", GetInstanceLabel(instance));
        }
    }

    /// <summary>
    /// 24/7: an automatic start that failed (pre-launch update, locked file, failed starting sequence) left the server
    /// down without any notice. It is tried again like a crashed server, at most <see cref="CrashRestartLimit"/> times
    /// per <see cref="CrashRestartWindow"/>, then Discord is told. A stop or kill by the user ends the retries.
    /// </summary>
    private async Task RetryFailedStart(int instance, DateTime attemptUtc)
    {
        bool StoppedByUser() => _manualStopsUtc.TryGetValue(instance, out var stopped) && stopped >= attemptUtc;
        if (_serverProcesses.ContainsKey(instance) || _launchingInstances.Contains(instance) || StoppedByUser()) return;

        var profile = GetInstanceProfile(instance);
        if (!AllowCrashRestart(instance))
        {
            if (profile is not null) await NotifyCrashLoop(instance, profile);
            return;
        }

        _logger.LogWarning("Server {Server} did not start, next try in {Seconds} s", GetInstanceLabel(instance), StartRetryDelay.TotalSeconds);
        await Task.Delay(StartRetryDelay);
        if (_serverProcesses.ContainsKey(instance) || _launchingInstances.Contains(instance) || StoppedByUser()) return;
        CatapultServerInBackground(instance);
    }
    
    public async Task<Process> CatapultServerProcess(ServerProfileRef profileName, IPRefWithModList listRef, int instance)
    {
        var data = new Dictionary<string, object>
        {
            {@"profile", profileName},
            {"modlist", listRef.Uri.OriginalString},
            {"instance", instance}
        };
        using var scope = _logger.BeginScope(data);
        _logger.LogInformation("Launching");
        
        if (!_appFiles.Server.TryGet(profileName, out var profile))
            throw new FileNotFoundException($"{profileName} profile not found.");
        if (!listRef.TryGetModList(out var list))
            throw new FileNotFoundException($"{listRef} modlist not found.");
        if (IsServerProfileLocked(profileName))
            throw new ArgumentException($"Profile {profileName} folder is currently locked by another process.");

        await _prelaunchUpdateGate.WaitAsync();
        try
        {
            if (!await PerformCatapultUpdates())
                throw new Exception("Pre-launch update failed");
            _startingProcesses.Add(instance);
        }
        finally
        {
            _prelaunchUpdateGate.Release();
        }

        try
        {
            return await StartServerProcess(instance, profile, list);
        }
        catch
        {
            _startingProcesses.Remove(instance);
            throw;
        }
    }

    private async Task<Process> StartServerProcess(int instance, ServerProfile profile, IEnumerable<string> list)
    {
        var savedPath = Path.Combine(_setup.GetInstancePath(instance), Constants.FolderGameSave);
        SetupJunction(savedPath, profile.ProfileFolder);

        await _setup.WriteIni(profile, instance);
        if (profile.ScriptMode == ServerProfile.ScriptModeBeforeStart)
            await RunProfileScript(instance, profile, "Server startup");
        var process = await CreateServerProcess(instance, profile, list);
        _logger.LogInformation(
            "Starting server process: exe={Exe}, workDir={WorkDir}, instance={Instance}",
            process.StartInfo.FileName,
            process.StartInfo.WorkingDirectory,
            instance);
        process.Start();
        _logger.LogInformation("Server parent process started PID={Pid} (server {Server})", process.Id, GetInstanceLabel(instance));

        var childProcess = await CatchServerChildProcess(process, instance);
        if (childProcess == null)
            throw new Exception("Could not launch the server");

        _logger.LogInformation(
            "Attached server shipping process PID={Pid} (instance {Instance})",
            childProcess.Id,
            instance);

        ConfigureProcess(profile.ProcessPriority, profile.CPUThreadAffinity, childProcess);

        AddStartDate(instance);
        return childProcess;
    }
    
    public IEnumerable<int> GetActiveServers()
    {
        return _serverProcesses.Keys;
    }

    /// <summary>
    /// Attaches the servers that already run. Boulder has no tick: without it, a start from the command line saw no
    /// running server and its pre-launch update replaced files they use.
    /// </summary>
    public Task AttachRunningServers() => FindExistingServers();

    /// <summary>
    ///     Ask a particular server instance to close. If the process is borked, this will not work.
    /// </summary>
    /// <param name="instance"></param>
    /// <param name="reason">Reason for the messages, null for the manual shutdown reason of the settings.</param>
    public async Task CloseServer(int instance, string? reason = null)
    {
        using var goingDown = GoingDown(instance);
        _logger.LogInformation("Close server {Server}", GetInstanceLabel(instance));
        _manualStopsUtc[instance] = DateTime.UtcNow;
        if (_serverProcesses.TryGetValue(instance, out var watcher))
        {
            var uri = _setup.Config.GetInstanceProfile(instance);
            // Stopping during a restart countdown: the countdown is abandoned, the stop happens now.
            if (_countdownInstances.Contains(instance))
                await CancelServerSequence(instance);

            if (!_appFiles.Server.TryResolve(uri, out var reference)
                || reference.Get().StoppingSequence.Actions.Count == 0
                || _serverSequences.ContainsKey(instance))
            {
                if (reference is not null && !_serverSequences.ContainsKey(instance))
                {
                    var profile = reference.Get();
                    var stopReason = !string.IsNullOrWhiteSpace(reason) ? reason
                        : string.IsNullOrEmpty(_lastRestartReason) ? _setup.GetReasonManualShutdown() : _lastRestartReason;
                    // DSL: the shutdown message only goes to Discord (players are being disconnected anyway).
                    var message = string.IsNullOrWhiteSpace(profile.MessageShutdown)
                        ? _setup.GetServerShutdownNotification(stopReason)
                        : profile.MessageShutdown;
                    await NotifyDiscordSafe(instance, profile, message, stopReason);
                    _lastRestartReason = string.Empty;
                }
                await watcher.StopAsync();
                return;
            }

            _lastRestartReason = string.IsNullOrWhiteSpace(reason) ? _setup.GetReasonManualShutdown() : reason;
            await StopServerWithSequence(instance, reference.Get());
        }
    }

    /// <summary>
    /// Restart requested by the tick (schedule, updates, RequiresRestart.txt). Until the server is stopping (countdown,
    /// Discord notice) the instance counts as pending, so the next ticks do not request and announce it again.
    /// </summary>
    public async void RestartServer(int instance)
    {
        if (!_restartsPending.Add(instance)) return;
        try
        {
            await RestartServerAsync(instance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart server");
        }
        finally
        {
            _restartsPending.Remove(instance);
        }
    }
    
    /// <param name="manual">Somebody asked for it by hand (the window, the web panel): cancelling its countdown just ends it.</param>
    public async Task RestartServerAsync(int instance, bool manual = false)
    {
        using var goingDown = GoingDown(instance);
        // The reason shows in the log why a server restarted (schedule, server update, mod update, ...).
        if (string.IsNullOrEmpty(_lastRestartReason))
            _logger.LogInformation("Restarting server {Server}", GetInstanceLabel(instance));
        else
            _logger.LogInformation("Restarting server {Server}, reason: {Reason}", GetInstanceLabel(instance), _lastRestartReason);
        if (_serverProcesses.TryGetValue(instance, out var watcher))
        {
            var uri = _setup.Config.GetInstanceProfile(instance);
            _appFiles.Server.TryResolve(uri, out var reference);
            var profile = reference?.Get();
            if (profile is not null && !_serverSequences.ContainsKey(instance))
            {
                // A custom stopping sequence wins over the built-in DSL warnings.
                if (profile.StoppingSequence.Actions.Count > 0)
                {
                    await RestartServerWithSequence(instance, profile);
                    return;
                }
                if (profile.UseRestartWarnings && profile.RestartWarnings.Any(x => x.Before > TimeSpan.Zero))
                {
                    await RunSequence(instance, BuildRestartCountdown(profile), () => RestartServerAsync(instance), isCountdown: true, isRestart: true, manual: manual);
                    return;
                }
            }

            if (profile is not null && !_serverSequences.ContainsKey(instance))
            {
                await NotifyDiscordSafe(instance, profile,
                    _setup.GetServerShutdownNotification(_lastRestartReason), _lastRestartReason);
                _lastRestartReason = string.Empty;
            }
            await watcher.RestartAsync();
        }
    }

    /// <summary>
    /// Crash loop protection for 24/7 operation: a server crashing again and again (broken mod, bad config, port in
    /// use) is restarted at most <see cref="CrashRestartLimit"/> times per <see cref="CrashRestartWindow"/>.
    /// </summary>
    public bool AllowCrashRestart(int instance) => _crashGuard.Allow(instance, DateTime.UtcNow);

    private async Task NotifyCrashLoop(int instance, ServerProfile profile)
    {
        _logger.LogError("Server {Server} crashed {Count} times within {Minutes} minutes, automatic restart paused",
            GetInstanceLabel(instance), CrashRestartLimit, CrashRestartWindow.TotalMinutes);
        await NotifyDiscordSafe(instance, profile, _setup.Config.NotificationCrashLoop,
            configure: ctx =>
            {
                ctx.Extra["count"] = CrashRestartLimit.ToString();
                ctx.Extra["minutes"] = ((int)CrashRestartWindow.TotalMinutes).ToString();
            });
    }

    /// <summary>
    /// DSL "Backup and Automation" after a server stopped (crash included): script before backup, backup with
    /// day limit, script after backup. Never throws; the restart decision follows afterwards.
    /// </summary>
    private async Task RunShutdownAutomation(int instance)
    {
        try
        {
            if (!_appFiles.Server.TryResolve(_setup.Config.GetInstanceProfile(instance), out var reference)) return;
            var profile = reference.Get();
            if (profile.ScriptMode == ServerProfile.ScriptModeShutdownBeforeBackup)
                await RunProfileScript(instance, profile, "Server shutdown");

            if (profile.BackupOnShutdown)
            {
                try
                {
                    var target = await _backupManager.PerformShutdownBackup(reference);
                    _logger.LogInformation("Backup of instance {Instance} written to {Target}", instance, target);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Backup of instance {Instance} failed", instance);
                }
            }

            if (profile.ScriptMode == ServerProfile.ScriptModeShutdownAfterBackup)
                await RunProfileScript(instance, profile, "Server shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shutdown automation of instance {Instance} failed", instance);
        }
    }

    private async Task RunProfileScript(int instance, ServerProfile profile, string purpose)
    {
        if (string.IsNullOrWhiteSpace(profile.ScriptPath)) return;
        var arguments = ServerMacros.Expand(profile.ScriptArguments, CreateMacroContext(instance));
        if (await ScriptRunner.Run(profile.ScriptPath, arguments, _logger, purpose)) return;
        // DSL 1.7.9: report a script that could not run on Discord.
        await NotifyDiscordSafe(instance, profile, $"{purpose} script could not run: {profile.ScriptPath}");
    }

    /// <summary>
    /// DSL restart flow: timed warnings (RCon + Discord), early restart when nobody is connected, then the
    /// "Restarted" message and the restart itself.
    /// </summary>
    private Sequence BuildRestartCountdown(ServerProfile profile)
    {
        var sequence = new Sequence();
        sequence.Actions.Add(new SequenceActionRestartCountdown(profile.RestartWarnings.ToList(), profile.RestartEarlyWhenEmpty));
        sequence.Actions.Add(string.IsNullOrWhiteSpace(profile.MessageRestarted)
            ? new SequenceActionBroadcast { Message = _setup.GetServerShutdownNotification("{reason}"), SendRCon = false }
            : new SequenceActionBroadcast { Message = profile.MessageRestarted });
        sequence.Actions.Add(new SequenceMainAction());
        return sequence;
    }

    /// <summary>
    /// Restart requested by the user (dashboard / web panel): runs the stopping sequence (warnings) like scheduled restarts.
    /// </summary>
    public async Task RestartServerManualAsync(int instance, string? reason = null)
    {
        if (!_serverProcesses.ContainsKey(instance)) return;
        _lastRestartReason = string.IsNullOrWhiteSpace(reason) ? _setup.GetReasonManualRestart() : reason;
        await RestartServerAsync(instance, manual: true);
    }

    /// <summary>
    /// Stop chosen by the user with a delay. During the delay players are warned with the message method of the save:
    /// when the countdown starts and at the warning times of the save that fit into the delay. Zero stops now.
    /// </summary>
    /// <param name="reason">Reason for the warnings and messages, null for the manual shutdown reason of the settings.</param>
    public async Task CloseServerIn(int instance, TimeSpan delay, string? reason = null)
    {
        if (!_serverProcesses.ContainsKey(instance)) return;
        _manualStopsUtc[instance] = DateTime.UtcNow;
        var stopReason = string.IsNullOrWhiteSpace(reason) ? _setup.GetReasonManualShutdown() : reason;
        _logger.LogInformation("Stopping server {Server} in {Minutes} min, reason: {Reason}", GetInstanceLabel(instance),
            (int)Math.Ceiling(Math.Max(0, delay.TotalMinutes)), stopReason);
        if (delay <= TimeSpan.Zero || GetInstanceProfile(instance) is not { } profile)
        {
            await CloseServer(instance, reason);
            return;
        }
        _lastRestartReason = stopReason;
        // The main action stops directly: CloseServer would cancel this countdown.
        await RunSequence(instance, BuildTimedCountdown(profile, delay, restart: false), () => StopServerNow(instance, stopReason), isCountdown: true, manual: true);
    }

    /// <summary>
    /// Moves the stop a running stop countdown leads to: the countdown is replaced by one that stops the server in
    /// <paramref name="delay"/>, with the reason of the stop (its first warning tells the players the new time).
    /// </summary>
    /// <returns>False at once when no stop countdown runs. Otherwise the task runs the new countdown like
    /// <see cref="CloseServerIn"/>.</returns>
    public async Task<bool> PostponeStop(int instance, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero || !_serverProcesses.ContainsKey(instance)
            || !_countdownInstances.Contains(instance) || _restartCountdownReasons.ContainsKey(instance))
            return false;
        var reason = _serverSequences.TryGetValue(instance, out var runner) ? runner.Arguments.Reason : null;
        _logger.LogInformation("Stop of server {Server} postponed by {Minutes} minutes", GetInstanceLabel(instance),
            (int)Math.Round(delay.TotalMinutes));
        await CloseServerIn(instance, delay, reason);
        return true;
    }

    /// <summary>
    /// Restart chosen by the user with a delay, warned like <see cref="CloseServerIn"/>. Zero restarts now, without the
    /// warnings (a custom stopping sequence of the save still runs).
    /// </summary>
    /// <param name="reason">Reason for the warnings and messages, null for the manual restart reason of the settings.</param>
    public async Task RestartServerIn(int instance, TimeSpan delay, string? reason = null, bool manual = true)
    {
        if (!_serverProcesses.TryGetValue(instance, out var watcher)) return;
        var profile = GetInstanceProfile(instance);
        _lastRestartReason = string.IsNullOrWhiteSpace(reason) ? _setup.GetReasonManualRestart() : reason;
        _logger.LogInformation("Restarting server {Server} in {Minutes} min, reason: {Reason}", GetInstanceLabel(instance),
            (int)Math.Ceiling(Math.Max(0, delay.TotalMinutes)), _lastRestartReason);
        if (delay > TimeSpan.Zero && profile is not null)
        {
            // Inside the countdown RestartServerAsync restarts directly.
            await RunSequence(instance, BuildTimedCountdown(profile, delay, restart: true), () => RestartServerAsync(instance), isCountdown: true, isRestart: true, manual: manual);
            return;
        }

        // Counted before the countdown is dropped: the status never shows "nothing planned" while the server is on its way down.
        using var goingDown = GoingDown(instance);
        if (_countdownInstances.Contains(instance))
            await CancelServerSequence(instance);
        if (profile is not null && !_serverSequences.ContainsKey(instance))
        {
            if (profile.StoppingSequence.Actions.Count > 0)
            {
                await RestartServerWithSequence(instance, profile);
                return;
            }
            await NotifyDiscordSafe(instance, profile, _setup.GetServerShutdownNotification(_lastRestartReason), _lastRestartReason);
        }
        _lastRestartReason = string.Empty;
        await watcher.RestartAsync();
    }

    /// <summary>A stop or restart countdown with warnings to the players runs for the instance.</summary>
    public bool IsCountdownRunning(int instance) => _countdownInstances.Contains(instance);

    /// <returns>When the automatic restarts of the instance resume, null when they are not paused.</returns>
    public DateTime? GetAutomaticRestartsPause(int instance)
    {
        return _automaticRestartsPausedUntil.TryGetValue(instance, out var until) && until > DateTime.UtcNow
            ? until
            : null;
    }

    /// <summary>
    /// Cancels a running stop or restart countdown (the players are told) and pauses the automatic restarts of the
    /// instance (schedule, server and mod updates, RequiresRestart.txt) for <paramref name="pause"/>, e.g. during an RP
    /// event. Manual stops and restarts still work, outdated mods are updated with the first restart after the pause.
    /// </summary>
    /// <param name="waitForNotice">
    /// False: returns once the state is changed, without waiting for the notice to the players (RCon, Discord) that goes on in the
    /// background (the Discord bot answers its user at once, the notice reaches the players a moment later).
    /// </param>
    public Task PauseAutomaticRestarts(int instance, TimeSpan pause, bool waitForNotice = true) =>
        PauseAutomaticRestartsCore(instance, pause, pause, waitForNotice: waitForNotice);

    /// <summary>
    /// When a restart countdown is postponed by <paramref name="shift"/> and the restart that follows the pause goes down at the new
    /// time: the old end plus the shift (an end that has passed counts as now), never earlier. The pause ends one warning lead before
    /// that, because the countdown that follows it takes that long.
    /// </summary>
    /// <returns>
    /// The pause (null when it would be shorter than a minute: then the countdown is replaced by one that ends at the new time) and
    /// the new end.
    /// </returns>
    internal static (TimeSpan? Pause, DateTime NewEndUtc) PlanRestartPostponement(DateTime? plannedEndUtc, DateTime nowUtc, TimeSpan shift,
        TimeSpan warningLead)
    {
        var start = plannedEndUtc is { } planned && planned > nowUtc ? planned : nowUtc;
        var newEnd = start + shift;
        var pause = newEnd - warningLead - nowUtc;
        return (pause >= MinimumPause ? pause : null, newEnd);
    }

    /// <summary>
    /// Moves the restart a running restart countdown leads to later by <paramref name="shift"/>, like <see cref="PostponeStop"/> moves a
    /// stop: the restart goes down at the old time plus the shift, not (as a plain pause of the shift from now would) soon after. The
    /// automatic restarts are paused until the countdown that leads to it begins; lifting the pause early brings the old time back.
    /// </summary>
    /// <returns>False at once when no restart countdown runs.</returns>
    public async Task<bool> PostponeRestart(int instance, TimeSpan shift, bool waitForNotice = true)
    {
        if (shift <= TimeSpan.Zero || !_serverProcesses.ContainsKey(instance) || !_countdownInstances.Contains(instance)
            || !_restartCountdownReasons.TryGetValue(instance, out var reason))
            return false;
        var now = DateTime.UtcNow;
        DateTime? plannedEnd = _countdownEndsUtc.TryGetValue(instance, out var end) ? end : null;
        var (pause, newEnd) = PlanRestartPostponement(plannedEnd, now, shift, GetInstanceRestartWarningLead(instance));
        if (pause is { } wait)
        {
            await PauseAutomaticRestartsCore(instance, wait, shift, waitForNotice: waitForNotice);
            return true;
        }
        // Too close for a pause: a countdown that ends at the new time (its first warning tells the players when). It stays what it was:
        // started by hand or by the schedule.
        var manualCountdown = _manualCountdowns.Contains(instance);
        LeaveUpdateRound(instance);
        _logger.LogInformation("Restart of server {Server} postponed by {Minutes} minutes", GetInstanceLabel(instance),
            (int)Math.Round(shift.TotalMinutes));
        await RestartServerIn(instance, newEnd - now, reason, manualCountdown);
        return true;
    }

    /// <summary>
    /// Cancels a running stop or restart countdown and nothing else: the players are told that it does not happen. A restart that
    /// the schedule or an update started on its own would begin again with the next tick, so for such a restart the automatic
    /// restarts wait until the daily window of the schedule has passed (a restart somebody started by hand just ends).
    /// </summary>
    /// <returns>False at once when no countdown runs.</returns>
    public async Task<TimeSpan?> CancelCountdown(int instance, bool waitForNotice = true)
    {
        if (!_countdownInstances.Contains(instance)) return null;
        var startedItself = _restartCountdownReasons.ContainsKey(instance) && !_manualCountdowns.Contains(instance);
        var hold = startedItself ? TimeSpan.FromMinutes(BotCommands.CancelHoldMinutes) : TimeSpan.Zero;
        await PauseAutomaticRestartsCore(instance, hold, TimeSpan.Zero, postpone: false, waitForNotice: waitForNotice);
        return hold;
    }

    /// <summary>
    /// Puts off a pause that already runs by <paramref name="shift"/>: the restart it holds back goes down later by that much (never sooner,
    /// as a new pause of the shift from now would). The players hear of it when a restart was postponed.
    /// </summary>
    /// <returns>False at once when the automatic restarts are not paused.</returns>
    public async Task<bool> ExtendAutomaticRestartsPause(int instance, TimeSpan shift, bool waitForNotice = true)
    {
        if (shift <= TimeSpan.Zero || !_automaticRestartsPausedUntil.TryGetValue(instance, out var until) || until <= DateTime.UtcNow) return false;
        _automaticRestartsPausedUntil[instance] = until + shift;
        var minutes = Math.Max(1, (int)Math.Round(shift.TotalMinutes));
        _logger.LogInformation("Pause of the automatic restarts of server {Server} extended by {Minutes} minutes", GetInstanceLabel(instance), minutes);
        OnServerStatusChanged();
        if (_postponedRestarts.ContainsKey(instance))
            await Notice(instance, _setup.Config.NotificationRestartPostponed.Replace("{minutes}", minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)), waitForNotice);
        return true;
    }

    // The notice to the players: awaited, or started and left to finish (a failure is only logged then).
    private async Task Notice(int instance, string message, bool wait)
    {
        var notice = BroadcastMessage(instance, message);
        if (wait)
        {
            await notice;
            return;
        }
        _ = ObserveNoticeAsync(notice, instance);
    }

    private async Task ObserveNoticeAsync(Task notice, int instance)
    {
        try
        {
            await notice;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The notice to the players of server {Server} failed", GetInstanceLabel(instance));
        }
    }

    private async Task PauseAutomaticRestartsCore(int instance, TimeSpan pause, TimeSpan announcedShift, bool postpone = true, bool waitForNotice = true)
    {
        if (pause > TimeSpan.Zero)
        {
            _automaticRestartsPausedUntil[instance] = DateTime.UtcNow + pause;
            _logger.LogInformation("Automatic restarts of server {Server} paused until {Until}", GetInstanceLabel(instance),
                DateTime.Now + pause);
            OnServerStatusChanged();
        }
        // A paused instance cannot take part in a common update restart: the others start without waiting for it.
        LeaveUpdateRound(instance);

        if (!_countdownInstances.Contains(instance)) return;
        // A restart countdown cancelled by a pause is postponed: it restarts when the pause is over and the players
        // are told by how long (they were told it was cancelled, and a scheduled restart then never came).
        string? postponedReason = null;
        DateTime? plannedEnd = _countdownEndsUtc.TryGetValue(instance, out var end) ? end : null;
        var manualCountdown = _manualCountdowns.Contains(instance);
        if (postpone && pause > TimeSpan.Zero && _restartCountdownReasons.TryGetValue(instance, out var reason))
            postponedReason = reason;
        // Cancelled for good (not postponed): marked before the countdown is dropped, so that whoever looks at the status knows it was
        // cancelled and does not have to wait to be sure.
        if (postponedReason is null) _countdownCancelledUtc[instance] = DateTime.UtcNow;
        await CancelServerSequence(instance);
        if (postponedReason is not null)
        {
            _postponedRestarts[instance] = new PostponedRestart(postponedReason, plannedEnd, manualCountdown);
            OnServerStatusChanged();
            var minutes = Math.Max(1, (int)Math.Round(announcedShift.TotalMinutes));
            _logger.LogInformation("Restart of server {Server} postponed by {Minutes} minutes", GetInstanceLabel(instance), minutes);
            await Notice(instance, _setup.Config.NotificationRestartPostponed.Replace("{minutes}",
                minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)), waitForNotice);
            return;
        }
        _logger.LogInformation("Countdown of server {Server} cancelled", GetInstanceLabel(instance));
        await Notice(instance, _setup.Config.NotificationCountdownCancelled, waitForNotice);
    }

    public void ResumeAutomaticRestarts(int instance)
    {
        if (!_automaticRestartsPausedUntil.Remove(instance)) return;
        _logger.LogInformation("Automatic restarts of server {Server} resumed", GetInstanceLabel(instance));
        OnServerStatusChanged();
        // A restart that was postponed is taken back: it goes down when it was planned before (not with a new countdown that
        // starts from nothing), and it does not wait for the next tick.
        if (_postponedRestarts.ContainsKey(instance) && _serverProcesses.TryGetValue(instance, out var process)
            && process.State.IsRunning() && !process.State.IsStopping()
            && !_serverSequences.ContainsKey(instance) && !_restartsPending.Contains(instance))
            RunPostponedRestart(instance, resumedEarly: true);
    }

    // The restart a pause postponed begins. Taken back before its time it goes down at the time it was planned for, with the
    // warnings that fit into the time left; when the pause ran out it begins with its usual countdown.
    private void RunPostponedRestart(int instance, bool resumedEarly)
    {
        if (!_postponedRestarts.Remove(instance, out var postponed)) return;
        var reason = string.IsNullOrEmpty(postponed.Reason) ? _setup.GetReasonAutomatedRestart() : postponed.Reason;
        if (resumedEarly && postponed.PlannedEndUtc is { } planned && planned - DateTime.UtcNow >= MinimumPause
            && GetInstanceProfile(instance) is not null)
        {
            _ = RestartAtPlannedTimeAsync(instance, planned - DateTime.UtcNow, reason, postponed.Manual);
            return;
        }
        _lastRestartReason = reason;
        RestartServer(instance);
    }

    private async Task RestartAtPlannedTimeAsync(int instance, TimeSpan delay, string reason, bool manual)
    {
        try
        {
            await RestartServerIn(instance, delay, reason, manual);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart server {Server} at the time it was planned for", GetInstanceLabel(instance));
        }
    }

    private bool AreAutomaticRestartsPaused(int instance)
    {
        if (!_automaticRestartsPausedUntil.TryGetValue(instance, out var until)) return false;
        if (until > DateTime.UtcNow) return true;
        _automaticRestartsPausedUntil.Remove(instance);
        _logger.LogInformation("Pause of the automatic restarts of server {Server} is over", GetInstanceLabel(instance));
        return false;
    }

    /// <summary>
    /// A read-only picture of every server instance (0 to ServerInstanceCount - 1) for remote control: state, players
    /// and what is about to happen. Plain values only: nothing in it reaches back into the launcher, no paths, addresses
    /// or passwords. Like every other member, call it on the thread the launcher is used on.
    /// </summary>
    public IReadOnlyList<LauncherServerStatus> GetServerStatuses()
    {
        var count = Math.Max(0, _setup.Config.ServerInstanceCount);
        int installed;
        // Remote control asks every few seconds: once every instance is installed the folders are only listed again after a
        // short while. While one is missing the answer is not kept, a fresh install shows at once.
        if (_installedCount >= count && DateTime.UtcNow - _installedCountAtUtc < InstalledCountMaxAge)
            installed = _installedCount;
        else
        {
            try
            {
                installed = _steam.GetInstalledInstances();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not count the installed server instances");
                installed = 0;
            }
            _installedCount = installed;
            _installedCountAtUtc = DateTime.UtcNow;
        }
        var statuses = new List<LauncherServerStatus>(count);
        for (var instance = 0; instance < count; instance++)
            statuses.Add(GetServerStatus(instance, installed));
        return statuses;
    }

    private LauncherServerStatus GetServerStatus(int instance, int installedInstances)
    {
        _serverProcesses.TryGetValue(instance, out var process);
        var running = process is not null && process.State.IsRunning() && !process.State.IsStopping();
        var countdown = _countdownInstances.Contains(instance);
        var paused = GetAutomaticRestartsPause(instance);
        return new LauncherServerStatus
        {
            Instance = instance,
            SaveName = GetInstanceSaveName(instance),
            ServerName = GetInstanceServerName(instance),
            Installed = instance < installedInstances,
            State = process?.State,
            Players = process?.Players ?? 0,
            MaxPlayers = process?.MaxPlayers ?? 0,
            StartedUtc = process?.StartUtc,
            // Starting sequence, launch, or a start held for an update round: a server process follows.
            Starting = process is null && (_launchingInstances.Contains(instance) || _backgroundStarts.Contains(instance)
                                           || _heldStarts.Contains(instance) || _serverSequences.ContainsKey(instance)),
            ShuttingDown = process is null && _shutdownTasks.ContainsKey(instance),
            Crashed = process is null && _crashedInstances.Contains(instance),
            SequenceRunning = _serverSequences.ContainsKey(instance),
            CountdownRunning = countdown,
            CountdownRestarts = countdown && _restartCountdownReasons.ContainsKey(instance),
            CountdownAutomatic = countdown && _restartCountdownReasons.ContainsKey(instance) && !_manualCountdowns.Contains(instance),
            CountdownCancelledUtc = _countdownCancelledUtc.TryGetValue(instance, out var cancelled) ? cancelled : null,
            GoingDownNow = _goingDown.ContainsKey(instance),
            CountdownEndsUtc = countdown && _countdownEndsUtc.TryGetValue(instance, out var end) ? end : null,
            RestartRequested = _restartsPending.Contains(instance),
            ProcessRestarts = process?.RequestRestart ?? false,
            RestartPostponed = _postponedRestarts.ContainsKey(instance),
            InUpdateRound = _updateRound.Contains(instance),
            UpdatePending = process is not null && running && IsUpdatePending(instance, process),
            NextScheduledRestartUtc = process is not null && running ? GetNextScheduledRestartUtc(instance, process, paused) : null,
            PausedUntilUtc = paused,
            RestartWarningLead = process is not null && running ? GetInstanceRestartWarningLead(instance) : TimeSpan.Zero
        };
    }

    private TimeSpan GetInstanceRestartWarningLead(int instance)
    {
        try
        {
            return GetInstanceProfile(instance) is { } profile ? GetRestartWarningLead(profile) : TimeSpan.Zero;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    // The "Server name" of the save the instance runs, empty while it keeps the default name.
    private string GetInstanceServerName(int instance)
    {
        try
        {
            var name = GetInstanceProfile(instance)?.ServerName?.Trim() ?? string.Empty;
            return string.Equals(name, ServerProfile.ServerNameDefault, StringComparison.Ordinal) ? string.Empty : name;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>How the log names a server: "0", or "0 (Name of the server)" when its save has a name of its own.</summary>
    public string GetInstanceLabel(int instance)
    {
        var name = BotInputRules.SanitizeForLog(GetInstanceServerName(instance));
        return name.Length == 0 ? instance.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{instance} ({name})";
    }

    // The name of the save selected for the instance (also when its folder is gone), empty when none is selected.
    private string GetInstanceSaveName(int instance)
    {
        var selected = _setup.Config.GetInstanceProfile(instance);
        if (string.IsNullOrWhiteSpace(selected)) return string.Empty;
        return _appFiles.TryParse<ServerProfile, ServerProfileRef>(selected, out var reference) ? reference.Name : string.Empty;
    }

    // The decision of PerformPeriodicUpdateCheck: one of the next checks restarts the server for an update.
    private bool IsUpdatePending(int instance, IConanServerProcess process)
    {
        if (_lastUpdateCheckFailed || _setup.Config.AutoUpdateStatus != AutoUpdateStatus.CheckForUpdates) return false;
        if (_serverUpdate.ShouldRestart(process.StartUtc)) return true;
        if (_modNeedUpdate.Count == 0) return false;
        try
        {
            var mods = _appFiles.ResolveModList(_setup.Config.GetInstanceModlist(instance)).GetModsFromList().ToHashSet();
            return _modNeedUpdate.Any(x => mods.Contains(x.PublishedFileId) && !_failedModUpdates.Contains(x.PublishedFileId));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the mods of instance {Instance}", instance);
            return false;
        }
    }

    /// <summary>
    /// When the schedule of the save restarts the running server next (the decision of PerformAutomaticRestarts), the
    /// warnings of the save included. Null when it does not within a day.
    /// </summary>
    private DateTime? GetNextScheduledRestartUtc(int instance, IConanServerProcess process, DateTime? pausedUntilUtc)
    {
        try
        {
            if (GetInstanceProfile(instance) is not { AutoRestart: true } profile) return null;
            var notBeforeUtc = process.StartUtc + GetAutoRestartMinUptime(profile);
            if (pausedUntilUtc is { } paused && paused > notBeforeUtc) notBeforeUtc = paused;
            var todayFull = profile.AutoRestartMaxPerDay > 0 && StartDateCountToday(instance) >= profile.AutoRestartMaxPerDay;
            if (GetNextDailyRestart(profile.AutoRestartDailyTime, DateTime.Now, notBeforeUtc.ToLocalTime(), todayFull) is not { } next)
                return null;
            return DateTime.SpecifyKind(next, DateTimeKind.Local).ToUniversalTime() + GetRestartWarningLead(profile);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not work out the next scheduled restart of instance {Instance}", instance);
            return null;
        }
    }

    // After a daily restart time the tick still starts the restart during this window.
    private static readonly TimeSpan DailyRestartWindow = TimeSpan.FromMinutes(5);

    private static TimeSpan GetAutoRestartMinUptime(ServerProfile profile) =>
        profile.AutoRestartMinUptime.TotalMinutes < 10 ? TimeSpan.FromMinutes(10) : profile.AutoRestartMinUptime;

    // The countdown a scheduled restart begins with (RestartServerAsync). A custom stopping sequence has no known length.
    private static TimeSpan GetRestartWarningLead(ServerProfile profile)
    {
        if (profile.StoppingSequence.Actions.Count > 0 || !profile.UseRestartWarnings) return TimeSpan.Zero;
        return profile.RestartWarnings.Where(x => x.Before > TimeSpan.Zero).Select(x => x.Before).DefaultIfEmpty().Max();
    }

    /// <summary>
    /// When daily restart times start the next automatic restart, as the tick checks them: the first moment from
    /// <paramref name="nowLocal"/> on that lies in the 5 minute window after a restart time and not before
    /// <paramref name="notBeforeLocal"/> (minimum uptime, pause). Looks at today and tomorrow. All times are local.
    /// </summary>
    /// <param name="todayFull">The restarts allowed per day are used up today.</param>
    public static DateTime? GetNextDailyRestart(IEnumerable<TimeSpan> dailyTimes, DateTime nowLocal, DateTime notBeforeLocal,
        bool todayFull)
    {
        DateTime? next = null;
        var earliest = notBeforeLocal > nowLocal ? notBeforeLocal : nowLocal;
        for (var day = todayFull ? 1 : 0; day <= 1; day++)
        {
            var date = nowLocal.Date.AddDays(day);
            foreach (var time in dailyTimes)
            {
                if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) continue;
                var windowStart = date + time;
                // The tick compares the time of day: the window ends at midnight.
                var windowEnd = windowStart + DailyRestartWindow;
                if (windowEnd >= date.AddDays(1)) windowEnd = date.AddDays(1).AddTicks(-1);
                var at = earliest > windowStart ? earliest : windowStart;
                if (at <= windowEnd && (next is null || at < next)) next = at;
            }
        }
        return next;
    }

    /// <summary>
    /// <see cref="PerformCatapultUpdates"/> for remote control, only while nothing else works on the files: no server
    /// is being started and no other update or Steam download runs. Running servers keep running, the update check of
    /// the tick restarts them for what they need.
    /// </summary>
    /// <returns>Null when busy, otherwise the result of the update.</returns>
    public async Task<bool?> PerformUpdatesIfIdle()
    {
        if (_launchingInstances.Count > 0 || _backgroundStarts.Count > 0 || _heldStarts.Count > 0
            || _serverSequences.Keys.Any(x => !_serverProcesses.ContainsKey(x))
            || _steam.Status != SteamStatus.StandBy)
            return null;
        if (!await _prelaunchUpdateGate.WaitAsync(0)) return null;
        try
        {
            return await PerformCatapultUpdates();
        }
        finally
        {
            _prelaunchUpdateGate.Release();
        }
    }

    /// <summary>
    /// Asks Steam now whether mods or the server need an update, the same check the tick makes every few minutes, and
    /// installs nothing. What it finds is used like the result of the tick (a restart for it follows the settings of the
    /// automatic updates). Null while something else works on the files: a start, an update or a Steam download.
    /// </summary>
    public async Task<LauncherUpdateCheck?> CheckForUpdatesNow()
    {
        if (_steam.Status != SteamStatus.StandBy) return null;
        if (!await _prelaunchUpdateGate.WaitAsync(0)) return null;
        try
        {
            var modsAnswered = await CheckModUpdates();
            var serverAnswered = await CheckServerUpdate();
            return new LauncherUpdateCheck(modsAnswered && serverAnswered,
                _modNeedUpdate.Select(x => x.PublishedFileId).Distinct().Count(), _serverUpdate.NeedUpdate);
        }
        finally
        {
            _prelaunchUpdateGate.Release();
        }
    }

    private ServerProfile? GetInstanceProfile(int instance)
    {
        return _appFiles.Server.TryResolve(_setup.Config.GetInstanceProfile(instance), out var reference) ? reference.Get() : null;
    }

    private async Task StopServerNow(int instance, string reason)
    {
        if (!_serverProcesses.TryGetValue(instance, out var watcher)) return;
        if (GetInstanceProfile(instance) is { } profile)
        {
            var message = string.IsNullOrWhiteSpace(profile.MessageShutdown)
                ? _setup.GetServerShutdownNotification(reason)
                : profile.MessageShutdown;
            await NotifyDiscordSafe(instance, profile, message, reason);
        }
        await watcher.StopAsync();
    }

    /// <summary>
    /// Countdown for a stop or restart in <paramref name="delay"/>: a first message when it starts, then the warning
    /// times of the save that fit into the delay (restarts use the warning texts of the save, stops their own text).
    /// </summary>
    private Sequence BuildTimedCountdown(ServerProfile profile, TimeSpan delay, bool restart)
    {
        var template = restart ? _setup.Config.NotificationTimedRestart : _setup.Config.NotificationTimedStop;
        string MessageAt(TimeSpan before) => template.Replace("{minutes}",
            Math.Max(1, (int)Math.Ceiling(before.TotalMinutes)).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var times = profile.RestartWarnings.Where(x => x.Before > TimeSpan.Zero && x.Before <= delay).ToList();
        var warnings = restart
            ? times.ToList()
            : times.Select(x => new RestartWarning(x.Before, MessageAt(x.Before))).ToList();
        if (!warnings.Any(x => x.Before == delay))
            warnings.Add(new RestartWarning(delay, MessageAt(delay)));

        var sequence = new Sequence();
        sequence.Actions.Add(new SequenceActionRestartCountdown(warnings, profile.RestartEarlyWhenEmpty));
        if (restart)
            sequence.Actions.Add(string.IsNullOrWhiteSpace(profile.MessageRestarted)
                ? new SequenceActionBroadcast { Message = _setup.GetServerShutdownNotification("{reason}"), SendRCon = false }
                : new SequenceActionBroadcast { Message = profile.MessageRestarted });
        sequence.Actions.Add(new SequenceMainAction());
        return sequence;
    }

    /// <summary>
    /// Macro values for messages of an instance (Discord, RCon, sequences, web panel).
    /// </summary>
    public MacroContext CreateMacroContext(int instance, string reason = "")
    {
        ServerProfile? profile = null;
        try
        {
            var uri = _setup.Config.GetInstanceProfile(instance);
            if (_appFiles.Server.TryResolve(uri, out var reference))
                profile = reference.Get();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No profile for macro context of instance {Instance}", instance);
        }

        string revision;
        try
        {
            revision = _steam.GetInstanceBuildId(instance).ToString();
        }
        catch (Exception)
        {
            revision = string.Empty;
        }

        return new MacroContext
        {
            Profile = profile,
            Process = _serverProcesses.GetValueOrDefault(instance),
            Instance = instance,
            Reason = reason,
            ExternalIp = ExternalIpProvider.Cached,
            EngineVersion = _setup.IsLegacy ? "UE4" : "UE5",
            BranchName = Constants.GetEditionDisplayName(_setup.Edition),
            ServerRevision = revision
        };
    }

    private static readonly TimeSpan DiscordTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Discord must never block a stop/restart: failures are only logged.
    /// </summary>
    public async Task NotifyDiscordSafe(int instance, ServerProfile profile, string message, string reason = "",
        Action<MacroContext>? configure = null)
    {
        try
        {
            var context = CreateMacroContext(instance, reason);
            configure?.Invoke(context);
            // A slow or rate limited webhook delayed restarts (they wait for the notice) without a limit.
            await DiscordWebHooks.Notify(profile, message, context).WaitAsync(DiscordTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord notification failed for instance {Instance}", instance);
        }
    }

    /// <summary>
    /// Send a chat/broadcast message to the players of an instance through RCon and Discord (DSL "Send" box).
    /// </summary>
    public async Task<bool> BroadcastMessage(int instance, string message, string? rconMethod = null,
        string reason = "", bool rcon = true, bool discord = true)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var context = CreateMacroContext(instance, reason);
        var text = ServerMacros.Expand(message, context);
        rconMethod ??= context.Profile?.GetRConBroadcastCommand() ?? "broadcast";
        var sent = false;
        if (rcon && _serverProcesses.TryGetValue(instance, out var process) && process.RCon is { } rconClient
            && process.State is ProcessState.RUNNING or ProcessState.ONLINE)
        {
            try
            {
                var response = await rconClient.Send($"{rconMethod} {text}", CancellationToken.None);
                sent = response.Exception is null;
                if (response.Exception is not null)
                    _logger.LogWarning(response.Exception, "RCon broadcast failed on instance {Instance}", instance);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RCon broadcast failed on instance {Instance}", instance);
            }
        }

        if (discord && context.Profile is { } profile && profile.GetDiscordWebHooks().Any())
        {
            await NotifyDiscordSafe(instance, profile, message, reason);
            sent = true;
        }
        return sent;
    }

    public IConanProcess? GetClientProcess()
    {
        return _conanClientProcess;
    }

    public IRcon? GetServerRcon(int instance)
    {
        if (_serverProcesses.TryGetValue(instance, out var watcher))
            return watcher.RCon;
        throw new ArgumentException($"Server instance {instance} is not running.");
    }

    /// <summary>
    ///     Get the server port information for all the running server processes.
    /// </summary>
    /// <returns></returns>
    /// <summary>
    /// Sends a message with the message method of the save to every running server that uses it and returns the
    /// RCon answers, so the method can be tested from the server save.
    /// </summary>
    public async Task<List<RConTestResult>> SendRConTestMessage(string profileName, string message, CancellationToken token)
    {
        var results = new List<RConTestResult>();
        foreach (var process in _serverProcesses.Values.ToList())
        {
            var instance = process.Infos.Instance;
            if (process.State is not (ProcessState.RUNNING or ProcessState.ONLINE)) continue;
            if (!_appFiles.Server.TryResolve(_setup.Config.GetInstanceProfile(instance), out var reference) || reference.Name != profileName) continue;
            var context = CreateMacroContext(instance);
            var command = $"{context.Profile?.GetRConBroadcastCommand() ?? "broadcast"} {ServerMacros.Expand(message, context)}";
            if (process.RCon is not { } rcon)
            {
                results.Add(new RConTestResult(instance, command, string.Empty, null, false));
                continue;
            }
            try
            {
                var response = await rcon.Send(command, token);
                results.Add(new RConTestResult(instance, command, response.Response, response.Exception, true));
            }
            catch (Exception ex)
            {
                results.Add(new RConTestResult(instance, command, string.Empty, ex, true));
            }
        }
        return results;
    }

    public IEnumerable<IConanServerProcess> GetServerProcesses()
    {
        foreach (var p in _serverProcesses.Values)
            yield return p;
    }

    public bool IsAnyServerRunning()
    {
        return _serverProcesses.Count > 0;
    }

    public bool IsClientRunning()
    {
        return _conanClientProcess != null;
    }

    /// <summary>
    ///     Kill the client process.
    /// </summary>
    public async Task KillClient()
    {
        if (_conanClientProcess == null) return;
        _logger.LogInformation("Kill client");
        await _conanClientProcess.KillAsync();
    }

    /// <summary>
    ///     Kill a particular server instance.
    /// </summary>
    /// <param name="instance"></param>
    public async Task KillServer(int instance)
    {
        _manualStopsUtc[instance] = DateTime.UtcNow;
        if (_serverProcesses.TryGetValue(instance, out var watcher))
        {
            _logger.LogInformation("Kill server {Server}", GetInstanceLabel(instance));
            try
            {
                await CancelServerSequence(instance);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not cancel sequence of server {Instance}", instance);
            }
            await watcher.KillAsync();
        }
    }

    public async Task Tick()
    {
        // Servers that kept running while Trebuchet was closed must be attached before deciding what to start,
        // otherwise a second server was launched on the same instance (or the start failed on the locked save).
        if (!_hasCatapulted)
        {
            await TickStage(FindExistingServers, nameof(FindExistingServers));
            // A failed or timed out WMI scan reports no server: starting the instances then launched a second server
            // on a running one. While more server processes run than were attached, wait a few ticks for a scan that
            // sees them (other installations can run servers with the same name, so not forever).
            if (_initialScanRetries < InitialScanMaxRetries && CountServerProcessesByName() > _serverProcesses.Count)
            {
                _initialScanRetries++;
                return;
            }
        }

        if (!_hasCatapulted && _setup.Catapult)
        {
            _hasCatapulted = true;
            for (int i = 0; i < _setup.Config.ServerInstanceCount; i++)
            {
                var instance = i;
                if(!_serverProcesses.ContainsKey(instance))
                    CatapultServerInBackground(instance);
            }
        } else if (!_hasCatapulted)
        {
            _hasCatapulted = true;
            for (int i = 0; i < _setup.Config.ServerInstanceCount; i++)
            {
                var instance = i;
                if(_serverProcesses.ContainsKey(instance)) continue;
                await TickStage(() =>
                {
                    var profileUri = _setup.Config.GetInstanceProfile(instance);
                    var serverPRef = _appFiles.Server.Resolve(profileUri);
                    var serverProfile = serverPRef.Get();
                    if(serverProfile.RestartWhenDown)
                        CatapultServerInBackground(instance);
                    return Task.CompletedTask;
                }, nameof(CatapultServer));
            }
        }

        // Each stage is isolated: a process dying mid-tick (kill, crash) must never take the launcher down.
        await TickStage(CleanStoppedProcesses, nameof(CleanStoppedProcesses));
        await TickStage(ReleaseHeldStarts, nameof(ReleaseHeldStarts));

        // Process discovery is relatively expensive (WMI); refresh every tick when missing, else every other tick.
        _tickCounter++;
        var needClientScan = _conanClientProcess is null || (_tickCounter % 2) == 0;
        var needServerScan = _serverProcesses.Count < _setup.Config.ServerInstanceCount || (_tickCounter % 2) == 0;
        if (needClientScan)
            await TickStage(FindExistingClient, nameof(FindExistingClient));
        if (needServerScan)
            await TickStage(FindExistingServers, nameof(FindExistingServers));

        await TickStage(PerformPeriodicUpdateCheck, nameof(PerformPeriodicUpdateCheck));
        await TickStage(PerformAutomaticRestarts, nameof(PerformAutomaticRestarts));
        await TickStage(PerformRequiresRestartCheck, nameof(PerformRequiresRestartCheck));
        _ = TickStage(PerformExternalIpCheck, nameof(PerformExternalIpCheck));

        var refreshTasks = new List<Task>();
        if (_conanClientProcess is { } client)
            refreshTasks.Add(TickStage(client.RefreshAsync, @"RefreshClient"));
        foreach (var process in _serverProcesses.Values.ToList())
        {
            ServerProfile? profile = null;
            try
            {
                var name = _appFiles.Server.Resolve(_setup.Config.GetInstanceProfile(process.Infos.Instance));
                if (_appFiles.Server.Exists(name))
                {
                    profile = _appFiles.Server.Get(name);
                    process.KillZombies = profile.KillZombies;
                    process.ZombieCheckSeconds = profile.ZombieCheckSeconds;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve profile of instance {Instance}", process.Infos.Instance);
            }

            var p = process;
            var pr = profile;
            refreshTasks.Add(TickStage(async () =>
            {
                await p.RefreshAsync();
                if (pr is not null) EnforceProcessSettings(p, pr);
            }, @"RefreshServer"));
        }
        if (refreshTasks.Count > 0)
            await Task.WhenAll(refreshTasks);
    }

    private int _initialScanRetries;
    private const int InitialScanMaxRetries = 5;

    private static int CountServerProcessesByName()
    {
        try
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Constants.FileServerBin));
            foreach (var process in processes) process.Dispose();
            return processes.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private async Task TickStage(Func<Task> stage, string name)
    {
        try
        {
            await stage();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launcher tick stage {Stage} failed", name);
        }
    }

    public async Task<ConanClientProcessInfos?> FindClientProcess()
    {
        var clientFolder = _setup.GetClientFolder();
        if (string.IsNullOrWhiteSpace(clientFolder))
            return null;

        var processName = _setup.GetClientProcessName();
        var clientRoot = Path.GetFullPath(clientFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var processes = await _tOsSpecific.GetProcessesWithName(processName);

        foreach (var data in processes.OrderByDescending(p => p.start))
        {
            if (data.IsEmpty) continue;
            if (!IsProcessUnderClientInstall(data.filename, clientRoot)) continue;
            if (!data.TryGetProcess(out var process)) continue;

            return new ConanClientProcessInfos()
            {
                Process = process,
                Start = data.start
            };
        }

        return null;
    }
    
    public async IAsyncEnumerable<ConanServerProcessInfos> FindServerProcesses()
    {
        var processes = await _tOsSpecific.GetProcessesWithName(Constants.FileServerBin);
        foreach (var p in processes)
        {
            if (!_setup.TryGetInstanceIndexFromPath(p.filename, out var instance)) continue;
            if (!p.TryGetProcess(out var process)) continue;

            var gameLogs = Path.Combine(_setup.GetInstancePath(instance),
                Constants.FolderGameSave,
                Constants.FolderGameSaveLog,
                Constants.FileGameLogFile);
            yield return new ConanServerProcessInfos()
            {
                Process = process,
                Start = p.start,
                Instance = instance,
                GameLogs = gameLogs
            };
        }
    }

    public bool HasModListUpdates(IPRefWithModList modList)
    {
        return _modListNeedUpdate.Contains(modList);
    }

    public bool HasServerUpdate()
    {
        return _serverUpdate.NeedUpdate;
    }

    /// <summary>
    /// Update before a server starts. Only what is outdated is downloaded (one Steam request each finds it): updating
    /// every mod and copying every server instance on each start made restarts take minutes. Mods of running servers
    /// and the server files while another server runs or starts are left alone (UpdateMods / UpdateServers).
    /// </summary>
    public async Task<bool> PerformCatapultUpdates()
    {
        if (_setup.Config.AutoUpdateStatus == AutoUpdateStatus.Never) return true;
        var serversBusy = IsAnyServerRunning() || _startingProcesses.Count > 0;

        if (await CheckModUpdates())
        {
            var outdated = _modNeedUpdate.Select(x => x.PublishedFileId).Distinct().ToList();
            if (outdated.Count > 0 && !await UpdateMods(outdated)) return false;
        }
        // No answer from Steam: try the full update as before.
        else if (!serversBusy && !await UpdateMods()) return false;

        if (serversBusy) return true;
        // Without an answer from Steam the last known state counts: with every instance installed and current a
        // download attempt could only fail too (it delayed the start by another connection timeout).
        await CheckServerUpdate();
        // This start can install a server update (found now or before): servers started after it are not restarted
        // again for it.
        _serverUpdate.ReportInstallAttempt(DateTime.UtcNow);
        if (!_serverUpdate.NeedUpdate && AreInstanceCopiesCurrent()) return true;
        return await UpdateServers();
    }

    private bool AreInstanceCopiesCurrent()
    {
        for (var i = 1; i < _setup.Config.ServerInstanceCount; i++)
            if (!_steam.IsInstanceCopyCurrent(i))
                return false;
        return true;
    }

    /// <summary>Mods of the servers that run or are starting: their files are in use.</summary>
    private HashSet<ulong> GetModsOfRunningServers()
    {
        var mods = new HashSet<ulong>();
        foreach (var instance in _serverProcesses.Keys.Concat(_startingProcesses).Distinct())
            mods.UnionWith(_appFiles.ResolveModList(_setup.Config.GetInstanceModlist(instance)).GetModsFromList());
        return mods;
    }

    public async Task<bool> VerifyFiles()
    {
        if (IsAnyServerRunning() || IsClientRunning()) return false;
        _logger.LogInformation(@"Verifying files, clearing caches");
        _steam.ClearSteamCache();
        _steam.InvalidateInstanceCopies();
        _steam.ClearModDetailsCache();
        
        try
        {
            await UpdateServers();
            await UpdateMods();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify files");
            return false;
        }
    }

    public Task<bool> UpdateMods()
    {
        _modListNeedUpdate.Clear();
        var lists = new List<IPRefWithModList>();
        for (int i = 0; i < _setup.Config.ServerInstanceCount; i++)
        {
            if(_appFiles.TryParseModListRef(_setup.Config.GetInstanceModlist(i), out var modListRef))
                lists.Add(modListRef);
        }
        if(_appFiles.TryParseModListRef(_setup.Config.SelectedClientModlist, out var clientModList))
            lists.Add(clientModList);
        if (lists.Count == 0) return Task.FromResult(true);
        return UpdateMods(lists.GetModsFromList().ToList());
    }
    
    public async Task<bool> UpdateMods(List<ulong> mods)
    {
        // A running server keeps its mod versions until it restarts: updating its mods now would hide that it needs
        // one (and Legacy servers keep the files open). Mods only other servers use are updated.
        var inUse = GetModsOfRunningServers();
        var skipped = mods.Where(inUse.Contains).ToList();
        if (skipped.Count > 0)
        {
            _logger.LogInformation("Mods {Mods} are used by a running server, they are updated when it restarts",
                string.Join(", ", skipped));
            mods = mods.Except(skipped).ToList();
            if (mods.Count == 0) return true;
        }
        // The Enhanced game client runs on extracted copies, Legacy keeps the workshop files open.
        if (IsClientRunning() && !CanReplaceModFiles(mods))
        {
            _logger.LogInformation("Mods are not updated now, their files are in use by the game client");
            return true;
        }
        
        try
        {
            await WaitForSteamIdle();
            using(_logger.BeginScope(("mods", mods)))
                _logger.LogInformation("Updating mods");
            await ScriptRunner.Run(_setup.Config.ScriptBeforeUpdate, string.Empty, _logger, "Before update");
            try
            {
                await _steam.UpdateMods(mods);
            }
            finally
            {
                await ScriptRunner.Run(_setup.Config.ScriptAfterUpdate, string.Empty, _logger, "After update");
            }
            await CheckModUpdates();
            var failed = _modNeedUpdate.Where(x => mods.Contains(x.PublishedFileId)).ToList();
            if (failed.Count > 0)
            {
                _logger.LogWarning("Mods could not be updated: {Mods}", string.Join(", ", failed.Select(x => $"{x.Title} ({x.PublishedFileId})")));
                foreach (var mod in failed) _failedModUpdates.Add(mod.PublishedFileId);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update mods");
            return false;
        }
    }

    public async Task<bool> UpdateServers()
    {
        // The game client does not use the server files. A server that is starting uses them already.
        if (IsAnyServerRunning() || _startingProcesses.Count > 0) return true;
        await WaitForSteamIdle();

        try
        {
            _logger.LogInformation("Updating servers");
            await ScriptRunner.Run(_setup.Config.ScriptBeforeUpdate, string.Empty, _logger, "Before update");
            bool updated;
            try
            {
                updated = await _steam.UpdateServerInstances();
            }
            finally
            {
                await ScriptRunner.Run(_setup.Config.ScriptAfterUpdate, string.Empty, _logger, "After update");
            }
            await CheckServerUpdate();
            if (!updated)
                _logger.LogWarning("Server files could not be updated, the servers start with the installed version");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update servers");
            return false;
        }
    }
    
    public async Task<bool> CheckModUpdates()
    {
        try
        {
            _logger.LogInformation("Checking mod updates");
            _modListNeedUpdate.Clear();
            var lists = new List<IPRefWithModList>();
            for (int i = 0; i < _setup.Config.ServerInstanceCount; i++)
            {
                if(_appFiles.TryParseModListRef(_setup.Config.GetInstanceModlist(i), out var modListRef))
                    lists.Add(modListRef);
            }
            if(_appFiles.TryParseModListRef(_setup.Config.SelectedClientModlist, out var clientModList))
                lists.Add(clientModList);
            var ids = lists.GetModsFromList().Distinct().ToList();
            if (ids.Count == 0)
            {
                _modNeedUpdate.Clear();
                return true;
            }
            
            // Fresh details: with the 15 minute cache a new mod version was only seen up to 15 minutes later.
            var details = await _steam.RequestModDetails(ids, fresh: true);
            // No answer from the Steam web API (down, timeout) is no result: the pre-launch update then falls back to
            // updating every mod, and no restart is decided on stale data.
            if (details.Count == 0) return false;

            _modNeedUpdate.Clear();
            // Without the file from Steam a mod cannot be downloaded: a restart for it would only repeat itself.
            _modNeedUpdate.AddRange(details
                .Where(d => d.Status.Status != UGCStatus.UpToDate && d.HasWorkshopFile));
            _modListNeedUpdate.AddRange(lists.Where(x => x.GetModsFromList().Intersect(_modNeedUpdate.Select(y => y.PublishedFileId)).Any()));
            ReportModUpdates();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check mod updates");
            return false;
        }
    }

    public void Report(SequenceProgress progress)
    {
        SequenceProgressChanged?.Invoke(this, progress);
    }

    private Task StopServerWithSequence(int instance, ServerProfile profile)
    {
        return RunSequence(instance, profile.StoppingSequence, () => CloseServer(instance));
    }
    
    private Task RestartServerWithSequence(int instance, ServerProfile profile)
    {
        return RunSequence(instance, profile.StoppingSequence, () => RestartServerAsync(instance));
    }

    private Task CatapultServerSequence(int instance, ServerProfile profile)
    {
        return RunSequence(instance, profile.StartingSequence, () => CatapultServer(instance));
    }

    private async Task RunSequence(int instance, Sequence sequence, Func<Task> mainAction, bool isCountdown = false,
        bool isRestart = false, bool manual = false)
    {
        if (sequence.Actions.Count == 0) return;
        await CancelServerSequence(instance);
        var reason = _lastRestartReason;
        if (isCountdown) _countdownInstances.Add(instance);
        if (isCountdown && manual) _manualCountdowns.Add(instance);
        if (isCountdown) _countdownCancelledUtc.Remove(instance);
        if (isCountdown && isRestart) _restartCountdownReasons[instance] = reason;
        if (isCountdown)
            _countdownEndsUtc[instance] = DateTime.UtcNow
                + (sequence.Actions.OfType<SequenceActionRestartCountdown>().FirstOrDefault()?.Length ?? TimeSpan.Zero);

        var args = new SequenceArgs()
        {
            BackupManager = _backupManager,
            Instance = instance,
            Launcher = this,
            Logger = _logger,
            Reason = reason,
            MainAction = mainAction,
            MacroExpander = text => ServerMacros.Expand(text, CreateMacroContext(instance, reason))
        };
        _lastRestartReason = string.Empty;
        var runner = new SequenceRunner(sequence, args, this);
        _serverSequences[instance] = runner;
        OnServerStatusChanged();

        try
        {
            await runner.ExecuteSequence();
            // A sequence without the main action would never start/stop the server: run it at the end.
            if (!sequence.Actions.Any(x => x is SequenceMainAction))
            {
                _logger.LogWarning("Sequence of instance {Instance} has no main action, running it after the sequence", instance);
                await mainAction();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Sequence of server {Server} cancelled", GetInstanceLabel(instance));
            Report(new SequenceProgress(instance, 0, 0));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute sequence");
            Report(new SequenceProgress(instance, 0, 0));
        }
        finally
        {
            // A failed sequence must not stay registered, or the instance is never cleaned up/restarted again.
            if (_serverSequences.TryGetValue(instance, out var current) && ReferenceEquals(current, runner))
            {
                _serverSequences.Remove(instance);
                _countdownInstances.Remove(instance);
                _manualCountdowns.Remove(instance);
                _restartCountdownReasons.Remove(instance);
                _countdownEndsUtc.Remove(instance);
                OnServerStatusChanged();
            }
        }
    }

    private async Task CancelServerSequence(int instance)
    {
        if (_serverSequences.TryGetValue(instance, out var oldSequence))
        {
            await oldSequence.Cts.CancelAsync();
            _serverSequences.Remove(instance);
            _countdownInstances.Remove(instance);
            _manualCountdowns.Remove(instance);
            _restartCountdownReasons.Remove(instance);
            _countdownEndsUtc.Remove(instance);
            OnServerStatusChanged();
        }
    }

    private Task PerformAutomaticRestarts()
    {
        foreach (var instance in _serverProcesses.Values.ToList())
        {
            if(!instance.State.IsRunning() || instance.State.IsStopping()) continue;
            if(_serverSequences.ContainsKey(instance.Infos.Instance)) continue;
            if(_restartsPending.Contains(instance.Infos.Instance)) continue;
            if(AreAutomaticRestartsPaused(instance.Infos.Instance)) continue;

            // Pause over: the restart postponed by it runs now (with its countdown and warnings again).
            if (_postponedRestarts.ContainsKey(instance.Infos.Instance))
            {
                RunPostponedRestart(instance.Infos.Instance, resumedEarly: false);
                continue;
            }

            var profileUri = _setup.Config.GetInstanceProfile(instance.Infos.Instance);
            var serverPRef = _appFiles.Server.Resolve(profileUri);
            var serverProfile = serverPRef.Get();
            
            if(!serverProfile.AutoRestart) continue;
            
            var minUptime = GetAutoRestartMinUptime(serverProfile);
            if((DateTime.UtcNow - instance.StartUtc ) < minUptime) continue;

            if(StartDateCountToday(instance.Infos.Instance) >= serverProfile.AutoRestartMaxPerDay
               && serverProfile.AutoRestartMaxPerDay > 0) continue;

            var time = DateTime.Now.TimeOfDay;
            if(serverProfile.AutoRestartDailyTime
               .All(x => time < x || time > x + DailyRestartWindow)) continue;

            _lastRestartReason = _setup.GetReasonAutomatedRestart();
            RestartServer(instance.Infos.Instance);
        }

        return Task.CompletedTask;
    }

    private List<StartDates> LoadStartDates()
    {
        try
        {
            var path = Path.Combine(_setup.GetBaseInstancePath(),
                Constants.FileStartDateJson
            );
            if (!File.Exists(path)) return [];

            var json = File.ReadAllText(path);
            if (string.IsNullOrEmpty(json)) return [];

            var result = JsonSerializer.Deserialize<List<StartDates>>(json);
            if (result is null) return [];
            return result;
        }
        catch
        {
            return [];
        }
    }

    private void AddStartDate(int instance)
    {
        _startDates.Add(new StartDates(instance, DateTime.UtcNow));
        while(_startDates.Count > 50) 
            _startDates.RemoveAt(0);

        try
        {
            var json = JsonSerializer.Serialize(_startDates);
            var path = Path.Combine(_setup.GetBaseInstancePath(),
                Constants.FileStartDateJson
            );
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save start dates");
        }
    }

    private int StartDateCount(TimeSpan timeSpan, int instance)
    {
        var date = DateTime.UtcNow - timeSpan;
        return _startDates.Count(x => x.Date > date && x.Instance == instance);
    }

    private int StartDateCountToday(int instance)
    {
        return StartDateCount(DateTime.Now.TimeOfDay, instance);
    }

    private async Task PerformPeriodicUpdateCheck()
    {
        // While Steam downloads (e.g. "update all mods" from the dashboard) half written mods look corrupted: no check
        // and no restart until it is done.
        if (_steam.Status != SteamStatus.StandBy) return;
        var frequency = _setup.Config.UpdateCheckFrequency;
        if (frequency < TimeSpan.FromMinutes(1))
            frequency = TimeSpan.FromMinutes(1);
        // The settings field takes huge numbers of days: DateTime.UtcNow + frequency would overflow on every tick.
        if (frequency > TimeSpan.FromDays(365))
            frequency = TimeSpan.FromDays(365);
        if (DateTime.UtcNow >= _nextUpdateCheckUtc)
        {
            var checkedOk = await CheckModUpdates() && await CheckServerUpdate();
            _lastUpdateCheckFailed = !checkedOk;
            // A failed check (Steam unreachable) is retried after a minute: on every 2 s tick it flooded the log and
            // the Steam API.
            _nextUpdateCheckUtc = DateTime.UtcNow + (checkedOk ? frequency : UpdateCheckRetryDelay);
        }
        // Without an answer from Steam nothing restarts for updates, they could not be installed anyway.
        if (_lastUpdateCheckFailed) return;
        if (_setup.Config.AutoUpdateStatus != AutoUpdateStatus.CheckForUpdates) return;

        var active = _serverProcesses.Values.Where(x => x.State.IsRunning() && !x.State.IsStopping()).ToList();
        if (active.Count == 0) return;

        // A server update installs only while no server runs: all servers restart for it together.
        if (active.Any(x => _serverUpdate.ShouldRestart(x.StartUtc)))
        {
            if (await StartUpdateRound(active.Select(x => x.Infos.Instance).ToList(), _ => _setup.GetReasonServerUpdate(), null))
                _serverUpdate.ReportRoundStarted();
            return;
        }
        foreach (var process in active)
            if (_serverUpdate.NeedUpdate && _serverUpdate.ReportNotInstalled(process.Infos.Instance))
                _logger.LogWarning(
                    "Server update (build {Build}) was not installed when instance {Instance} started, it is installed with its next restart",
                    _serverUpdate.SteamBuildId, process.Infos.Instance);

        // Mod updates: every server using an outdated mod restarts, together, so its files are free when the first of
        // them starts and installs it. Servers using other mods keep running.
        // Mods of listed servers that do not run normally (stopping, hung, still starting) cannot be replaced now:
        // a round for them could not install them and would repeat with every check.
        var blockedMods = new HashSet<ulong>();
        foreach (var process in _serverProcesses.Values.Where(x => !active.Contains(x)))
            blockedMods.UnionWith(_appFiles.ResolveModList(_setup.Config.GetInstanceModlist(process.Infos.Instance)).GetModsFromList());
        var outdatedByInstance = new Dictionary<int, List<PublishedMod>>();
        foreach (var process in active)
        {
            var modListRef = _appFiles.ResolveModList(_setup.Config.GetInstanceModlist(process.Infos.Instance));
            var mods = modListRef.GetModsFromList().ToHashSet();
            // An update that failed is not retried for the same mods, or every check would restart the server again.
            var outdated = _modNeedUpdate
                .Where(x => mods.Contains(x.PublishedFileId) && !_failedModUpdates.Contains(x.PublishedFileId)
                            && !blockedMods.Contains(x.PublishedFileId))
                .ToList();
            if (outdated.Count > 0)
                outdatedByInstance[process.Infos.Instance] = outdated;
        }
        if (outdatedByInstance.Count == 0)
        {
            _updateWaitLogged = false;
            return;
        }

        // The Enhanced game client only reads a workshop file while extracting it (it runs on copies), Legacy keeps
        // the files open: mod updates wait only while an outdated file is really in use.
        var outdatedIds = outdatedByInstance.Values.SelectMany(x => x).Select(x => x.PublishedFileId).Distinct().ToList();
        if (IsClientRunning() && !CanReplaceModFiles(outdatedIds))
        {
            if (!_updateWaitLogged)
                _logger.LogInformation("Mod updates wait until the game client is closed (it uses the outdated mod files)");
            _updateWaitLogged = true;
            return;
        }
        _updateWaitLogged = false;

        await StartUpdateRound(outdatedByInstance.Keys.ToList(),
            instance => _setup.GetReasonModUpdate(outdatedByInstance[instance]), outdatedByInstance);
    }

    /// <summary>
    /// Restarts these servers together for an update (each with its warnings/countdown). Their starts are held until
    /// all of them are stopped (<see cref="StartAfterRestart"/>). Waits while one of them is paused or busy: restarting
    /// only the others would bring them back without the update and restart them again.
    /// </summary>
    /// <returns>True when the round was started.</returns>
    private async Task<bool> StartUpdateRound(List<int> instances, Func<int, string> reasonOf,
        IReadOnlyDictionary<int, List<PublishedMod>>? outdatedMods)
    {
        if (instances.Any(x => _serverSequences.ContainsKey(x) || _restartsPending.Contains(x) || _updateRound.Contains(x)))
            return false;
        var paused = instances.Where(AreAutomaticRestartsPaused).ToList();
        if (paused.Count > 0)
        {
            if (!_updateRoundWaitLogged)
                _logger.LogInformation("Updates wait until the automatic restarts of instance {Instances} are no longer paused",
                    string.Join(", ", paused));
            _updateRoundWaitLogged = true;
            return false;
        }
        _updateRoundWaitLogged = false;

        foreach (var instance in instances)
            _updateRound.Add(instance);
        foreach (var instance in instances)
        {
            // Players hear which mods changed first, then the restart warnings of the save count down.
            if (outdatedMods is not null && outdatedMods.TryGetValue(instance, out var outdated))
            {
                var announcement = _setup.Config.NotificationModUpdateDetected
                    .Replace("{modList}", string.Join(", ", outdated.Select(x => x.Title)));
                await BroadcastMessage(instance, announcement, discord: false);
            }
            _lastRestartReason = reasonOf(instance);
            RestartServer(instance);
        }
        return true;
    }

    /// <summary>
    /// Steam runs one download at a time and refuses a second one: a restart would fail with "pre-launch update
    /// failed" while the dashboard updates mods. Waits for the running download instead (at most an hour).
    /// </summary>
    private async Task WaitForSteamIdle()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromHours(1);
        var logged = false;
        while (_steam.Status != SteamStatus.StandBy && DateTime.UtcNow < deadline)
        {
            if (!logged)
            {
                _logger.LogInformation("Waiting for the running Steam download to finish");
                logged = true;
            }
            await Task.Delay(1000);
        }
    }

    /// <summary>
    /// True when no process holds a file of these mods (they can be replaced by the update).
    /// </summary>
    private bool CanReplaceModFiles(IEnumerable<ulong> mods)
    {
        foreach (var mod in mods)
        {
            if (!_setup.TryGetModPath(mod.ToString(), out var path)) continue;
            var folder = Path.GetDirectoryName(path);
            if (folder is null || !Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>Logs the outdated mods once each time the list changes (the check runs every few minutes).</summary>
    private void ReportModUpdates()
    {
        var outdated = _modNeedUpdate.Select(x => x.PublishedFileId).OrderBy(x => x).ToList();
        if (outdated.SequenceEqual(_reportedModUpdates)) return;
        _reportedModUpdates = outdated;
        _updateWaitLogged = false;
        // Other versions now: a failed update may work this time.
        _failedModUpdates.Clear();
        if (outdated.Count == 0) return;
        _logger.LogInformation("Mod updates available: {Mods}",
            string.Join(", ", _modNeedUpdate.Select(x => $"{x.Title} ({x.PublishedFileId}, {x.Status.Status})")));
    }

    private async Task<bool> CheckServerUpdate()
    {
        _logger.LogInformation("Checking server updates");
        try
        {
            var instancesMissing = _steam.GetInstalledInstances() < _setup.Config.ServerInstanceCount;
            var steamBuild = instancesMissing ? 0 : await _steam.GetSteamBuildId();
            // Without an answer from Steam the build reads 0: that is no update (it restarted the servers after every
            // failed check, e.g. once the Steam connection dropped after a few days).
            if (!_serverUpdate.Report(steamBuild, _steam.GetInstanceBuildId(0), instancesMissing, DateTime.UtcNow))
                _logger.LogWarning("Could not read the server build from Steam, the server update check is skipped");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check server updates");
            return false;
        }
    }
    
    private bool IsAutoConnectInfoValid(ClientConnection connection)
    {
        if (!IPAddress.TryParse(connection.IpAddress, out _)) return false;
        if (connection.Port is < 0 or > 65535) return false;
        return true;
    }

    private async Task EnsureWorkshopModsCompatible(IEnumerable<string> modList)
    {
        var ids = modList
            .Where(m => ulong.TryParse(m, out _))
            .Select(ulong.Parse)
            .ToList();
        if (ids.Count == 0) return;
        await _steam.EnsureModsCompatibleWithEdition(ids);
    }

    /// <summary>
    /// True when <paramref name="filename"/> is under the configured client install root
    /// (directory-boundary safe — avoids matching sibling folders like "Conan Exiles UE4").
    /// </summary>
    private bool IsProcessUnderClientInstall(string? filename, string? clientRootPrefix = null)
    {
        if (string.IsNullOrEmpty(filename)) return false;
        try
        {
            var full = Path.GetFullPath(filename);
            string root;
            if (!string.IsNullOrEmpty(clientRootPrefix))
                root = clientRootPrefix;
            else
            {
                var clientFolder = _setup.GetClientFolder();
                if (string.IsNullOrWhiteSpace(clientFolder))
                    return false;
                root = Path.GetFullPath(clientFolder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            if (full.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;
            var prefix = root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<Process> CreateClientProcess(ClientProfile profile, IEnumerable<string> modList, bool isBattleEye, bool autoConnect)
    {
        var filename = _setup.GetBinFile(isBattleEye);
        if (!File.Exists(filename))
            throw new FileNotFoundException(
                $"Client binary for {Constants.GetEditionDisplayName(_setup.Edition)} was not found: {filename}",
                filename);

        var modlistFile = CreateModListFile("client");
        await File.WriteAllLinesAsync(modlistFile, _setup.GetModsPath(modList));
        ReplaceModListFile("client", modlistFile);
        var args = profile.GetClientArgs(modlistFile, autoConnect, _setup.Edition);

        var dir = Path.GetDirectoryName(filename);
        if (dir == null)
            throw new Exception($"Failed to start process, invalid directory {filename}");

        var process = new Process();
        process.StartInfo.FileName = filename;
        process.StartInfo.WorkingDirectory = dir;
        process.StartInfo.Arguments = args;
        process.StartInfo.UseShellExecute = false;
        process.EnableRaisingEvents = true;

        return process;
    }

    private async Task<Process?> CatchClientChildProcess(Process parent)
    {
        var gameBinName = _setup.GetClientProcessName();
        var launchedName = Path.GetFileName(parent.StartInfo.FileName);

        // Direct launch of the game binary (no BattlEye stub) — parent is the client.
        if (string.Equals(launchedName, gameBinName, StringComparison.OrdinalIgnoreCase) && !parent.HasExited)
            return parent;

        DateTime notBefore;
        try
        {
            notBefore = parent.StartTime.ToUniversalTime().AddSeconds(-2);
        }
        catch
        {
            notBefore = DateTime.UtcNow.AddSeconds(-5);
        }

        var clientFolder = _setup.GetClientFolder();
        if (string.IsNullOrWhiteSpace(clientFolder))
            return null;

        var clientRoot = Path.GetFullPath(clientFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        // WMI enumeration is relatively expensive; start snappy, then back off.
        var delayMs = 25;
        while (!parent.HasExited && DateTime.UtcNow < deadline)
        {
            var candidates = await _tOsSpecific.GetProcessesWithName(gameBinName);
            var match = candidates
                .Where(p => !p.IsEmpty && p.start >= notBefore && IsProcessUnderClientInstall(p.filename, clientRoot))
                .OrderByDescending(p => p.start)
                .FirstOrDefault();

            if (!match.IsEmpty && match.TryGetProcess(out var targetProcess))
                return targetProcess;

            await Task.Delay(delayMs);
            if (delayMs < 100)
                delayMs = Math.Min(100, delayMs + 25);
        }

        return null;
    }

    private void ConfigureProcess(int priority, long threadAffinity, Process process)
    {
        if (priority == ServerProfile.ProcessPriorityUnchanged) return;
        try
        {
            process.PriorityClass = GetPriority(priority);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                process.ProcessorAffinity = (IntPtr)Tools.Clamp2CPUThreads(threadAffinity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply priority/affinity to process {Pid}", SafePid(process));
        }
    }

    private static int SafePid(Process process)
    {
        try { return process.Id; } catch { return 0; }
    }

    private string SafeBuildId(int instance)
    {
        try { return _steam.GetInstanceBuildId(instance).ToString(); } catch { return "n/a"; }
    }

    /// <summary>
    /// DSL 1.9.4: when a performance mode is set, force priority/affinity back if the server changed them.
    /// </summary>
    private void EnforceProcessSettings(IConanServerProcess process, ServerProfile profile)
    {
        if (!profile.EnforceProcessSettings || profile.ProcessPriority == ServerProfile.ProcessPriorityUnchanged) return;
        if (!process.State.IsRunning() || process.State.IsStopping()) return;
        if (process.EnsureProcessSettings(GetPriority(profile.ProcessPriority), Tools.Clamp2CPUThreads(profile.CPUThreadAffinity)))
            _logger.LogInformation("Re-applied priority/affinity on server instance {Instance}", process.Infos.Instance);
    }

    /// <summary>
    /// DSL "RequiresRestart.txt": a file dropped in the instance or profile folder triggers a restart, its content is the reason.
    /// Files older than the server process are stale and removed, which avoids endless restart loops.
    /// </summary>
    private Task PerformRequiresRestartCheck()
    {
        foreach (var process in _serverProcesses.Values.ToList())
        {
            var instance = process.Infos.Instance;
            if (!process.State.IsRunning() || process.State.IsStopping()) continue;
            if (_serverSequences.ContainsKey(instance)) continue;
            if (_restartsPending.Contains(instance)) continue;
            // The file stays until the pause is over and restarts the server then.
            if (AreAutomaticRestartsPaused(instance)) continue;
            if (!_appFiles.Server.TryResolve(_setup.Config.GetInstanceProfile(instance), out var reference)) continue;
            var profile = reference.Get();
            if (!profile.RequiresRestartFileCheck) continue;

            foreach (var file in new[]
                     {
                         Path.Combine(_setup.GetInstancePath(instance), Constants.FileRequiresRestart),
                         Path.Combine(profile.ProfileFolder, Constants.FileRequiresRestart)
                     })
            {
                if (!File.Exists(file)) continue;
                string reason;
                DateTime written;
                try
                {
                    written = File.GetLastWriteTimeUtc(file);
                    reason = File.ReadAllText(file).Trim();
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not process {File}", file);
                    continue;
                }

                if (written < process.StartUtc)
                {
                    _logger.LogInformation("Ignored stale {File} (older than the server process)", file);
                    continue;
                }

                if (reason.Length > 200) reason = reason[..200];
                _logger.LogInformation("{File} found, restarting instance {Instance}: {Reason}", file, instance, reason);
                _lastRestartReason = string.IsNullOrWhiteSpace(reason) ? Constants.FileRequiresRestart : reason;
                RestartServer(instance);
                break;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// DSL 1.3.6: watch the external IP and report changes to Discord.
    /// </summary>
    private async Task PerformExternalIpCheck()
    {
        if (_serverProcesses.Count == 0) return;
        if (DateTime.UtcNow - _lastExternalIpCheck < TimeSpan.FromMinutes(10)) return;
        _lastExternalIpCheck = DateTime.UtcNow;

        var previous = ExternalIpProvider.Cached;
        var current = await ExternalIpProvider.GetAsync(force: true);
        if (string.IsNullOrEmpty(current))
        {
            _logger.LogInformation("Could not fetch the external IP");
            return;
        }
        if (string.IsNullOrEmpty(previous) || previous == current) return;

        _logger.LogWarning("External IP changed from {Old} to {New}", previous, current);
        foreach (var process in _serverProcesses.Values.ToList())
        {
            var instance = process.Infos.Instance;
            if (!_appFiles.Server.TryResolve(_setup.Config.GetInstanceProfile(instance), out var reference)) continue;
            var profile = reference.Get();
            if (!profile.NotifyExternalIpChange) continue;
            await NotifyDiscordSafe(instance, profile, _setup.Config.NotificationExternalIpChanged,
                configure: ctx => ctx.Extra["old_ip"] = previous);
        }
    }

    private ProcessPriorityClass GetPriority(int index)
    {
        switch (index)
        {
            case 1:
                return ProcessPriorityClass.AboveNormal;

            case 2:
                return ProcessPriorityClass.High;

            case 3:
                return ProcessPriorityClass.RealTime;

            default:
                return ProcessPriorityClass.Normal;
        }
    }

    private async Task<Process> CreateServerProcess(int instance, ServerProfile profile, IEnumerable<string> modlist)
    {
        var process = new Process();

        var filename = _setup.GetIntanceBinary(instance);

        // Server depot remains shared Live (443030); do not apply Enhanced client workshop-tag rules here.
        if (_setup.IsEnhanced)
            _logger.LogWarning(
                "Enhanced edition uses shared server AppID {ServerAppId}; verify the dedicated server build matches the Enhanced client",
                _setup.ServerAppId);

        var modfileFile = CreateModListFile($"server{instance}");
        await File.WriteAllLinesAsync(modfileFile, _setup.GetModsPath(modlist));
        ReplaceModListFile($"server{instance}", modfileFile);
        
        var args = profile.GetServerArgs(instance, modfileFile);

        var dir = Path.GetDirectoryName(filename);
        if (dir == null)
            throw new Exception($"Failed to start process, invalid directory {filename}");

        process.StartInfo.FileName = filename;
        process.StartInfo.WorkingDirectory = dir;
        process.StartInfo.Arguments = args;
        // UseShellExecute=false so args/working directory are applied reliably to the proxy→shipping tree.
        process.StartInfo.UseShellExecute = false;
        process.EnableRaisingEvents = true;
        return process;
    }

    /// <summary>
    /// Resolve the dedicated-server shipping process for an instance.
    /// Do not use the first WMI child of the proxy — that can be CrashReportClient
    /// or another short-lived helper, which made the launcher report an instant crash.
    /// </summary>
    private async Task<Process?> CatchServerChildProcess(Process parent, int instance)
    {
        var launchedName = Path.GetFileName(parent.StartInfo.FileName);
        if (string.Equals(launchedName, Constants.FileServerBin, StringComparison.OrdinalIgnoreCase)
            && !parent.HasExited)
            return parent;

        DateTime notBefore;
        try
        {
            notBefore = parent.StartTime.ToUniversalTime().AddSeconds(-2);
        }
        catch
        {
            notBefore = DateTime.UtcNow.AddSeconds(-5);
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        var delayMs = 25;
        // Keep searching after the proxy exits — shipping may already be running as a sibling/orphan.
        while (DateTime.UtcNow < deadline)
        {
            var candidates = await _tOsSpecific.GetProcessesWithName(Constants.FileServerBin);
            var match = candidates
                .Where(p => !p.IsEmpty
                            && p.start >= notBefore
                            && _setup.TryGetInstanceIndexFromPath(p.filename, out var idx)
                            && idx == instance)
                .OrderByDescending(p => p.start)
                .FirstOrDefault();

            if (!match.IsEmpty && match.TryGetProcess(out var targetProcess))
                return targetProcess;

            await Task.Delay(delayMs);
            if (delayMs < 100)
                delayMs = Math.Min(100, delayMs + 25);
        }

        return null;
    }
    
    private async Task FindExistingClient()
    {
        if (_conanClientProcess != null) return;

        var process = await FindClientProcess();
        if (process is not null)
        {
            _conanClientProcess = await _processFactory.Create()
                .SetStartDate(process.Start)
                .SetProcess(process.Process)
                .BuildClient();
            OnStateChanged();
        }
    }

    private async Task FindExistingServers()
    {
        await foreach (var process in FindServerProcesses())
        {
            // A server Trebuchet is launching right now is attached by its start: attaching it here too leaked one of
            // the two watchers (query thread, log reader, RCon).
            if (_serverProcesses.ContainsKey(process.Instance) || _launchingInstances.Contains(process.Instance))
            {
                process.Process.Dispose();
                continue;
            }
            IConanServerProcess serverProcess;
            try
            {
                var serverInfos = await _setup.GetInfosFromIni(process.Instance);
                var builder = _processFactory.Create()
                    .SetStartDate(process.Start)
                    .SetProcess(process.Process)
                    .SetServerInfos(serverInfos)
                    .SetLogFile(process.GameLogs);
                if (serverInfos.RConPort > 0)
                    builder.UseRCon();
                serverProcess = await builder.BuildServer();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A locked ini or an offline folder of ONE server must not keep the others from being attached (a second
                // server would be started on an instance that runs). Tried again with the next scan.
                process.Process.Dispose();
                LogAttachFailure(process.Instance, ex);
                continue;
            }
            if (_serverProcesses.ContainsKey(process.Instance) || _launchingInstances.Contains(process.Instance)
                || !_serverProcesses.TryAdd(process.Instance, serverProcess))
            {
                serverProcess.Dispose();
                continue;
            }
            serverProcess.StateChanged += OnServerStateChanged;
            _crashedInstances.Remove(process.Instance);
            OnStateChanged();
        }
    }

    private const string ModListFilePrefix = "trebuchet-mods-";
    private readonly Dictionary<string, string> _modListFiles = [];
    private bool _modListFilesSwept;

    /// <summary>
    /// The mod list file a game process reads at its start. <c>Path.GetTempFileName</c> left one file per start for ever
    /// (and fails once 65535 exist): one file per server instance (and the client) is kept, the previous one removed.
    /// Files of earlier runs are swept after 30 days (a server started by an earlier run may still hold its list).
    /// </summary>
    private string CreateModListFile(string owner)
    {
        if (!_modListFilesSwept)
        {
            _modListFilesSwept = true;
            SweepOldModListFiles();
        }
        return Path.Combine(Path.GetTempPath(), $"{ModListFilePrefix}{owner}-{Guid.NewGuid():N}.txt");
    }

    private void ReplaceModListFile(string owner, string created)
    {
        if (_modListFiles.TryGetValue(owner, out var previous) && previous != created)
            TryDeleteFile(previous);
        _modListFiles[owner] = created;
    }

    private static void SweepOldModListFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), ModListFilePrefix + "*.txt"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-30))
                    TryDeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still read by a process or locked: the sweep removes it later.
        }
    }

    private readonly Dictionary<int, DateTime> _attachFailureLoggedUtc = [];

    /// <summary>Once a minute per instance: the scan runs every few seconds.</summary>
    private void LogAttachFailure(int instance, Exception ex)
    {
        var now = DateTime.UtcNow;
        if (_attachFailureLoggedUtc.TryGetValue(instance, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        _attachFailureLoggedUtc[instance] = now;
        _logger.LogWarning(ex, "Could not attach the running server of instance {Instance}, trying again", instance);
    }

    private async void OnServerStateChanged(object? sender, ProcessState e)
    {
        try
        {
            // Starting, online (the log says the server is ready), stopping, stopped, crashed: show it at once.
            OnServerStatusChanged();
            if (sender is not IConanServerProcess process) return;
            var uri = _setup.Config.GetInstanceProfile(process.Infos.Instance);
            if (!_appFiles.Server.TryResolve(uri, out var reference)) return;
            switch (e)
            {
                case ProcessState.CRASHED:
                    await NotifyDiscordSafe(process.Infos.Instance, reference.Get(),
                        _setup.GetCrashNotification(process.Infos.Title));
                    return;
                case ProcessState.ONLINE:
                    _logger.LogInformation("Server {Server} online (PID {Pid}, build {Build})",
                        GetInstanceLabel(process.Infos.Instance), process.PId, SafeBuildId(process.Infos.Instance));
                    var readyProfile = reference.Get();
                    await NotifyDiscordSafe(process.Infos.Instance, readyProfile,
                        string.IsNullOrWhiteSpace(readyProfile.MessageReady)
                            ? _setup.GetOnlineNotification(process.Infos.Title)
                            : readyProfile.MessageReady);
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform notification");
        }
    }

    private async Task CleanStoppedProcesses()
    {
        if (_conanClientProcess != null && !_conanClientProcess.State.IsRunning())
        {
            _logger.LogInformation("Client stopped");
            _conanClientProcess.Dispose();
            _conanClientProcess = null;
            OnStateChanged();
        }

        foreach (var server in _serverProcesses.ToList())
        {
            if (server.Value.State.IsRunning()) continue;
            if(_serverSequences.ContainsKey(server.Value.Infos.Instance)) continue;

            // A hung server reported as crashed can still run (its kill is pending or failed): starting another one
            // now ran two servers on the same save, and the process scan attached the hung one again. It is removed
            // once it is gone. (Only crashed ones: a stopped server has exited, its process id may belong to another
            // process by now.)
            if (server.Value.State == ProcessState.CRASHED && !server.Value.Process.HasExitedSafe(server.Value.PId))
            {
                if (_crashedStillRunning.Add(server.Key))
                    _logger.LogWarning("Server {Server} is reported as {State} but still runs, waiting until it is gone",
                        GetInstanceLabel(server.Key), server.Value.State);
                server.Value.Process.TryKill(server.Value.PId, out _);
                continue;
            }
            _crashedStillRunning.Remove(server.Key);

            var exitCodeText = "n/a";
            server.Value.Process.TryRefresh();
            try
            {
                exitCodeText = server.Value.Process.ExitCode.ToString();
            }
            catch (Exception)
            {
                // Attached via GetProcessById — exit code unavailable
            }

            _logger.LogInformation(
                "Server {Server} stopped (state={State}, exitCode={ExitCode})",
                GetInstanceLabel(server.Key),
                server.Value.State,
                exitCodeText);
            _serverProcesses.Remove(server.Key);
            _countdownCancelledUtc.Remove(server.Key);
            _postponedRestarts.Remove(server.Key);
            if (server.Value.State == ProcessState.CRASHED) _crashedInstances.Add(server.Key);
            else _crashedInstances.Remove(server.Key);
            server.Value.StateChanged -= OnServerStateChanged;
            // Backup, scripts and the restart run outside the tick: a long backup froze the monitoring of every other
            // server, and the dashboard showed the stopped server as running until the backup was done.
            // Started before the change is reported: it marks the instance as shutting down, so nobody (the Discord bot)
            // reads "stopped" for a moment between the stop and its aftermath.
            _ = FinishStoppedServer(server.Key, server.Value);
            OnStateChanged();
        }
    }

    private async Task FinishStoppedServer(int instance, IConanServerProcess server)
    {
        try
        {
            var automation = RunShutdownAutomation(instance);
            _shutdownTasks[instance] = automation;
            OnServerStatusChanged();
            try
            {
                await automation;
            }
            finally
            {
                if (_shutdownTasks.TryGetValue(instance, out var current) && ReferenceEquals(current, automation))
                    _shutdownTasks.Remove(instance);
                OnServerStatusChanged();
            }

            var name = _appFiles.Server.Resolve(_setup.Config.GetInstanceProfile(instance));
            if (server.RequestRestart)
                StartAfterRestart(instance);
            else
            {
                _updateRound.Remove(instance);
                if (server.State == ProcessState.CRASHED && _appFiles.Server.Get(name).RestartWhenDown)
                {
                    if (AllowCrashRestart(instance))
                        CatapultServerInBackground(instance);
                    else
                        await NotifyCrashLoop(instance, _appFiles.Server.Get(name));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart server {Server}", GetInstanceLabel(instance));
        }
        finally
        {
            server.Dispose();
        }
    }

    /// <summary>
    /// Starts a restarted server again. In an update round it waits until every server of the round is stopped, so
    /// the first start can install the update (files of running servers are never replaced).
    /// </summary>
    private void StartAfterRestart(int instance)
    {
        if (_updateRound.Contains(instance) && _updateRound.Any(x => x != instance && _serverProcesses.ContainsKey(x)))
        {
            if (_heldStarts.Count == 0) _heldSinceUtc = DateTime.UtcNow;
            _heldStarts.Add(instance);
            OnServerStatusChanged();
            _logger.LogInformation("Server {Server} starts once the other servers of the update are stopped", GetInstanceLabel(instance));
            return;
        }
        _updateRound.Remove(instance);
        CatapultServerInBackground(instance);
    }

    /// <summary>Starts the held servers of an update round once all of its servers are stopped.</summary>
    private Task ReleaseHeldStarts()
    {
        if (_heldStarts.Count == 0) return Task.CompletedTask;
        var stillRunning = _updateRound.Where(x => !_heldStarts.Contains(x) && _serverProcesses.ContainsKey(x)).ToList();
        // Servers counting down or stopping are on their way: wait for them, however long their warnings are.
        var notRestarting = stillRunning.Where(x => !_restartsPending.Contains(x) && !_serverSequences.ContainsKey(x)
                                                    && !_serverProcesses[x].State.IsStopping()).ToList();
        if (stillRunning.Count > 0 && notRestarting.Count == 0)
        {
            _heldSinceUtc = DateTime.UtcNow;
            return Task.CompletedTask;
        }
        if (notRestarting.Count > 0)
        {
            // Safety net: a server of the round that does not restart must not keep the others down.
            if (DateTime.UtcNow - _heldSinceUtc < HeldStartTimeout) return Task.CompletedTask;
            _logger.LogWarning("Servers {Instances} did not restart for the update, starting the others anyway",
                string.Join(", ", notRestarting));
        }
        var held = _heldStarts.ToList();
        _heldStarts.Clear();
        _updateRound.Clear();
        foreach (var instance in held)
            CatapultServerInBackground(instance);
        return Task.CompletedTask;
    }

    private void LeaveUpdateRound(int instance)
    {
        _updateRound.Remove(instance);
    }

    // A game whose Saved folder is no junction (a real folder, a link Trebuchet did not make) uses no save of Trebuchet.
    // Path.GetFullPath("") threw for it: while such a server ran, every start of another instance failed.
    private bool IsClientProfileLocked(ClientProfileRef profileRef)
    {
        if (_conanClientProcess == null) return false;
        var target = GetCurrentClientJunction();
        if (string.IsNullOrEmpty(target)) return false;
        var junction = Path.GetFullPath(target);
        var profilePath = Path.GetFullPath(_appFiles.Client.GetDirectory(profileRef));
        return string.Equals(junction, profilePath, StringComparison.Ordinal);
    }

    private bool IsServerProfileLocked(ServerProfileRef profileRef)
    {
        var profilePath = Path.GetFullPath(_appFiles.Server.GetDirectory(profileRef));
        foreach (var s in _serverProcesses.Values)
        {
            var instance = s.Infos.Instance;
            var target = GetCurrentServerJunction(instance);
            if (string.IsNullOrEmpty(target)) continue;
            var junction = Path.GetFullPath(target);
            if (string.Equals(junction, profilePath, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private string GetCurrentClientJunction()
    {
        var path = Path.Combine(_setup.GetClientFolder(), Constants.FolderGameSave);
        return _osSpecific.GetSymbolicLinkTarget(path);
    }

    private string GetCurrentServerJunction(int instance)
    {
        var path = Path.Combine(_setup.GetInstancePath(instance), Constants.FolderGameSave);
        return _osSpecific.GetSymbolicLinkTarget(path);
    }

    private void SetupJunction(string junction, string targetPath)
    {
        _logger.LogInformation("Setup new junction {junction} > {target}", junction, targetPath);
        _osSpecific.MakeSymbolicLink(junction, targetPath);
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
        OnServerStatusChanged();
    }

    private void OnServerStatusChanged()
    {
        try
        {
            ServerStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A listener must never break the launcher.
            _logger.LogDebug(ex, "A listener of the server status failed");
        }
    }
}
