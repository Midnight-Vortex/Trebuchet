using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using tot_lib;
using tot_lib.OsSpecific;
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
    private bool _serverNeedUpdate;
    private DateTime _lastUpdateCheckTime;
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
    private readonly HashSet<int> _countdownInstances = [];
    public const int CrashRestartLimit = 3;
    public static readonly TimeSpan CrashRestartWindow = TimeSpan.FromMinutes(15);
    private readonly CrashRestartGuard _crashGuard = new(CrashRestartLimit, CrashRestartWindow);
    // Instances started from the tick that are still in their starting sequence.
    private readonly HashSet<int> _backgroundStarts = [];
    // Instances whose server process is being launched right now (updates, ini, process start).
    private readonly HashSet<int> _launchingInstances = [];

    public event EventHandler? StateChanged;
    public event EventHandler<SequenceProgress>? SequenceProgressChanged; 

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
        
        var profile = _appFiles.Server.Get(profileName);
        if (profile.StartingSequence.Actions.Count > 0 && !_serverSequences.ContainsKey(instance))
        {
            await CatapultServerSequence(instance, profile);
            return;
        }

        // Two starts of one instance (button and automatic start) must not launch two servers on the same save.
        if (!_launchingInstances.Add(instance)) return;
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
            serverProcess.StateChanged += OnServerStateChanged;
            _serverProcesses.TryAdd(instance, serverProcess);
            OnStateChanged();
        }
        finally
        {
            _launchingInstances.Remove(instance);
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
        _ = RunBackgroundStart(instance);
    }

    private async Task RunBackgroundStart(int instance)
    {
        try
        {
            await CatapultServer(instance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server {Instance}", instance);
        }
        finally
        {
            _backgroundStarts.Remove(instance);
        }
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

        if (!await PerformCatapultUpdates())
            throw new Exception("Pre-launch update failed");
        
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
        _logger.LogInformation("Server parent process started PID={Pid} (instance {Instance})", process.Id, instance);

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
    ///     Ask a particular server instance to close. If the process is borked, this will not work.
    /// </summary>
    /// <param name="instance"></param>
    public async Task CloseServer(int instance)
    {
        _logger.LogInformation($"Close Server {instance}");
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
                    var reason = string.IsNullOrEmpty(_lastRestartReason) ? _setup.GetReasonManualShutdown() : _lastRestartReason;
                    // DSL: the shutdown message only goes to Discord (players are being disconnected anyway).
                    var message = string.IsNullOrWhiteSpace(profile.MessageShutdown)
                        ? _setup.GetServerShutdownNotification(reason)
                        : profile.MessageShutdown;
                    await NotifyDiscordSafe(instance, profile, message, reason);
                    _lastRestartReason = string.Empty;
                }
                await watcher.StopAsync();
                return;
            }

            _lastRestartReason = _setup.GetReasonManualShutdown();
            await StopServerWithSequence(instance, reference.Get());
        }
    }

    public async void RestartServer(int instance)
    {
        try
        {
            await RestartServerAsync(instance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart server");
        }
    }
    
    public async Task RestartServerAsync(int instance)
    {
        _logger.LogInformation($"Restarting Server {instance}");
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
                    await RunSequence(instance, BuildRestartCountdown(profile), () => RestartServerAsync(instance), isCountdown: true);
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
        _logger.LogError("Server instance {Instance} crashed {Count} times within {Minutes} minutes, automatic restart paused",
            instance, CrashRestartLimit, CrashRestartWindow.TotalMinutes);
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
        await RestartServerAsync(instance);
    }

    /// <summary>
    /// Stop chosen by the user with a delay. During the delay players are warned with the message method of the save:
    /// when the countdown starts and at the warning times of the save that fit into the delay. Zero stops now.
    /// </summary>
    public async Task CloseServerIn(int instance, TimeSpan delay)
    {
        if (!_serverProcesses.ContainsKey(instance)) return;
        if (delay <= TimeSpan.Zero || GetInstanceProfile(instance) is not { } profile)
        {
            await CloseServer(instance);
            return;
        }
        _lastRestartReason = _setup.GetReasonManualShutdown();
        // The main action stops directly: CloseServer would cancel this countdown.
        await RunSequence(instance, BuildTimedCountdown(profile, delay, restart: false), () => StopServerNow(instance), isCountdown: true);
    }

    /// <summary>
    /// Restart chosen by the user with a delay, warned like <see cref="CloseServerIn"/>. Zero restarts now, without the
    /// warnings (a custom stopping sequence of the save still runs).
    /// </summary>
    public async Task RestartServerIn(int instance, TimeSpan delay)
    {
        if (!_serverProcesses.TryGetValue(instance, out var watcher)) return;
        var profile = GetInstanceProfile(instance);
        _lastRestartReason = _setup.GetReasonManualRestart();
        if (delay > TimeSpan.Zero && profile is not null)
        {
            // Inside the countdown RestartServerAsync restarts directly.
            await RunSequence(instance, BuildTimedCountdown(profile, delay, restart: true), () => RestartServerAsync(instance), isCountdown: true);
            return;
        }

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

    private ServerProfile? GetInstanceProfile(int instance)
    {
        return _appFiles.Server.TryResolve(_setup.Config.GetInstanceProfile(instance), out var reference) ? reference.Get() : null;
    }

    private async Task StopServerNow(int instance)
    {
        if (!_serverProcesses.TryGetValue(instance, out var watcher)) return;
        if (GetInstanceProfile(instance) is { } profile)
        {
            var reason = _setup.GetReasonManualShutdown();
            var message = string.IsNullOrWhiteSpace(profile.MessageShutdown)
                ? _setup.GetServerShutdownNotification(reason)
                : profile.MessageShutdown;
            await NotifyDiscordSafe(instance, profile, message, reason);
        }
        await watcher.StopAsync();
    }

    /// <summary>Reminder times of a countdown chosen in the dashboard, besides the warning times of the save.</summary>
    private static readonly int[] TimedCountdownReminderMinutes = [60, 30, 15, 10, 5, 3, 1];

    /// <summary>
    /// Countdown for a stop or restart in <paramref name="delay"/>: a message when it starts, then reminders at the
    /// warning times of the save and at <see cref="TimedCountdownReminderMinutes"/> that fit into the delay. Restarts
    /// use the warning text of the save where it has one for that time.
    /// </summary>
    private Sequence BuildTimedCountdown(ServerProfile profile, TimeSpan delay, bool restart)
    {
        var template = restart ? _setup.Config.NotificationTimedRestart : _setup.Config.NotificationTimedStop;
        string MessageAt(TimeSpan before) => template.Replace("{minutes}",
            Math.Max(1, (int)Math.Ceiling(before.TotalMinutes)).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var saveWarnings = profile.RestartWarnings.Where(x => x.Before > TimeSpan.Zero && x.Before <= delay).ToList();
        var times = saveWarnings.Select(x => x.Before)
            .Concat(TimedCountdownReminderMinutes.Select(x => TimeSpan.FromMinutes(x)).Where(x => x < delay))
            .Append(delay)
            .Distinct();
        var warnings = times.Select(before => restart && saveWarnings.FirstOrDefault(x => x.Before == before) is { } own
                ? own
                : new RestartWarning(before, MessageAt(before)))
            .ToList();

        var sequence = new Sequence();
        // The user chose the time: the countdown runs to the end even when nobody is online.
        sequence.Actions.Add(new SequenceActionRestartCountdown(warnings, earlyWhenEmpty: false));
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
            await DiscordWebHooks.Notify(profile, message, context);
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
        if (_serverProcesses.TryGetValue(instance, out var watcher))
        {
            _logger.LogInformation($"Kill server {instance}");
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
            await TickStage(FindExistingServers, nameof(FindExistingServers));

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
        return _serverNeedUpdate;
    }

    public async Task<bool> PerformCatapultUpdates()
    {
        if (_setup.Config.AutoUpdateStatus == AutoUpdateStatus.Never) return true;
        // The game client does not block here: server files are not shared and UpdateMods checks the mod files.
        if (IsAnyServerRunning()) return true;
        if (!await UpdateMods()) return false;
        if (!await UpdateServers()) return false;
        return true;
    }

    public async Task<bool> VerifyFiles()
    {
        if (IsAnyServerRunning() || IsClientRunning()) return false;
        _logger.LogInformation(@"Verifying files, clearing caches");
        _steam.ClearSteamCache();
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
        // A running server keeps its mod versions until it restarts: updating now would hide that it needs one.
        if (IsAnyServerRunning())
        {
            _logger.LogInformation("Mods are not updated while a server is running, they are updated when it restarts");
            return true;
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
        // The game client does not use the server files.
        if (IsAnyServerRunning()) return true;
        await WaitForSteamIdle();

        try
        {
            _logger.LogInformation("Updating servers");
            await ScriptRunner.Run(_setup.Config.ScriptBeforeUpdate, string.Empty, _logger, "Before update");
            try
            {
                await _steam.UpdateServerInstances();
            }
            finally
            {
                await ScriptRunner.Run(_setup.Config.ScriptAfterUpdate, string.Empty, _logger, "After update");
            }
            await CheckServerUpdate();
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
            if (lists.Count == 0) return true;
            
            // Fresh details: with the 15 minute cache a new mod version was only seen up to 15 minutes later.
            var details = await _steam.RequestModDetails(lists.GetModsFromList().ToList(), fresh: true);
            if (details.Count == 0) return true;

            _modNeedUpdate.Clear();
            _modNeedUpdate.AddRange(details
                .Where(d => d.Status.Status != UGCStatus.UpToDate));
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

    private async Task RunSequence(int instance, Sequence sequence, Func<Task> mainAction, bool isCountdown = false)
    {
        if (sequence.Actions.Count == 0) return;
        await CancelServerSequence(instance);
        if (isCountdown) _countdownInstances.Add(instance);

        var reason = _lastRestartReason;
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
            _logger.LogInformation("Sequence of instance {Instance} cancelled", instance);
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
        }
    }

    private Task PerformAutomaticRestarts()
    {
        foreach (var instance in _serverProcesses.Values)
        {
            if(!instance.State.IsRunning() || instance.State.IsStopping()) continue;
            if(_serverSequences.ContainsKey(instance.Infos.Instance)) continue;
            
            var profileUri = _setup.Config.GetInstanceProfile(instance.Infos.Instance);
            var serverPRef = _appFiles.Server.Resolve(profileUri);
            var serverProfile = serverPRef.Get();
            
            if(!serverProfile.AutoRestart) continue;
            
            var minUptime = serverProfile.AutoRestartMinUptime.TotalMinutes < 10
                ? TimeSpan.FromMinutes(10)
                : serverProfile.AutoRestartMinUptime;
            if((DateTime.UtcNow - instance.StartUtc ) < minUptime) continue;
            
            if(StartDateCountToday(instance.Infos.Instance) >= serverProfile.AutoRestartMaxPerDay
               && serverProfile.AutoRestartMaxPerDay > 0) continue;

            var time = DateTime.Now.TimeOfDay;
            if(serverProfile.AutoRestartDailyTime
               .All(x => time < x || time > x + TimeSpan.FromMinutes(5))) continue;

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
        if ((DateTime.UtcNow - _lastUpdateCheckTime) >= frequency)
        {
            if (!await CheckModUpdates()) return;
            if (!await CheckServerUpdate()) return;
            _lastUpdateCheckTime = DateTime.UtcNow;
        }

        if (_setup.Config.AutoUpdateStatus != AutoUpdateStatus.CheckForUpdates) return;

        // The Enhanced game client only reads a workshop file while extracting it (it runs on copies), Legacy keeps
        // the files open: mod updates wait only while an outdated file is really in use. Server files are not shared.
        var modsInUse = _modNeedUpdate.Count > 0 && IsClientRunning()
                        && !CanReplaceModFiles(_modNeedUpdate.Select(x => x.PublishedFileId));
        // Mods are only updated while no server runs: restarting one of several servers would bring it back with the
        // old mods and the next check would restart it again.
        var otherServersRunning = _serverProcesses.Count > 1;
        if (_modListNeedUpdate.Count > 0 && (modsInUse || otherServersRunning))
        {
            if (!_updateWaitLogged)
                _logger.LogInformation(modsInUse
                    ? "Mod updates wait until the game client is closed (it uses the outdated mod files)"
                    : "Mod updates wait until only one server is running (mods are updated while no server runs)");
            _updateWaitLogged = true;
        }
        else
            _updateWaitLogged = false;

        foreach (var process in _serverProcesses.Values.ToList())
        {
            if(!process.State.IsRunning() || process.State.IsStopping()) continue;
            if(_serverSequences.ContainsKey(process.Infos.Instance)) continue;

            if (_serverNeedUpdate)
            {
                _lastRestartReason = _setup.GetReasonServerUpdate();
                RestartServer(process.Infos.Instance);
                continue;
            }

            if (modsInUse || otherServersRunning) continue;
            var modListRef = _appFiles.ResolveModList(_setup.Config.GetInstanceModlist(process.Infos.Instance));
            if (!_modListNeedUpdate.Contains(modListRef)) continue;
            var mods = modListRef.GetModsFromList().ToList();
            var outdated = _modNeedUpdate.Where(x => mods.Contains(x.PublishedFileId)).ToList();
            // An update that failed is not retried for the same mods, or every check would restart the server again.
            if (outdated.Count == 0 || outdated.All(x => _failedModUpdates.Contains(x.PublishedFileId))) continue;

            // Players hear which mods changed first, then the restart warnings of the save count down (as for restarts).
            var announcement = _setup.Config.NotificationModUpdateDetected
                .Replace("{modList}", string.Join(", ", outdated.Select(x => x.Title)));
            await BroadcastMessage(process.Infos.Instance, announcement, discord: false);
            _lastRestartReason = _setup.GetReasonModUpdate(outdated);
            RestartServer(process.Infos.Instance);
        }
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
        if (_steam.GetInstalledInstances() < _setup.Config.ServerInstanceCount)
        {
            _serverNeedUpdate = true;
            return true;
        }
        
        try
        {
            _serverNeedUpdate = await _steam.GetSteamBuildId() != _steam.GetInstanceBuildId(0);
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

        var modlistFile = Path.GetTempFileName();
        await File.WriteAllLinesAsync(modlistFile, _setup.GetModsPath(modList));
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

        var modfileFile = Path.GetTempFileName();
        await File.WriteAllLinesAsync(modfileFile, _setup.GetModsPath(modlist));
        
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
            if(_serverProcesses.ContainsKey(process.Instance)) continue;
            var serverInfos = await _setup.GetInfosFromIni(process.Instance);
            var builder = _processFactory.Create()
                .SetStartDate(process.Start)
                .SetProcess(process.Process)
                .SetServerInfos(serverInfos)
                .SetLogFile(process.GameLogs);
            if (serverInfos.RConPort > 0)
                builder.UseRCon();
            var serverProcess = await builder.BuildServer();
            serverProcess.StateChanged += OnServerStateChanged;
            _serverProcesses.TryAdd(process.Instance, serverProcess);
            OnStateChanged();
        }
    }

    private async void OnServerStateChanged(object? sender, ProcessState e)
    {
        try
        {
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
                    _logger.LogInformation("Server {Instance} online (PID {Pid}, build {Build})",
                        process.Infos.Instance, process.PId, SafeBuildId(process.Infos.Instance));
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
            
            var exitCodeText = "n/a";
            if (server.Value.Process.HasExitedSafe(server.Value.PId))
            {
                server.Value.Process.TryRefresh();
                try
                {
                    exitCodeText = server.Value.Process.ExitCode.ToString();
                }
                catch (Exception)
                {
                    // Attached via GetProcessById — exit code unavailable
                }
            }

            _logger.LogInformation(
                "Server {instance} stopped (state={State}, exitCode={ExitCode})",
                server.Key,
                server.Value.State,
                exitCodeText);
            _serverProcesses.Remove(server.Key);
            server.Value.StateChanged -= OnServerStateChanged;
            OnStateChanged();
            await RunShutdownAutomation(server.Key);
            try
            {
                var name = _appFiles.Server.Resolve(_setup.Config.GetInstanceProfile(server.Key));
                if (server.Value.RequestRestart)
                    CatapultServerInBackground(server.Key);
                else if (server.Value.State == ProcessState.CRASHED && _appFiles.Server.Get(name).RestartWhenDown)
                {
                    if (AllowCrashRestart(server.Key))
                        CatapultServerInBackground(server.Key);
                    else
                        await NotifyCrashLoop(server.Key, _appFiles.Server.Get(name));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restart server {Instance}", server.Key);
            }
            finally
            {
                server.Value.Dispose();
            }
        }
    }

    private bool IsClientProfileLocked(ClientProfileRef profileRef)
    {
        if (_conanClientProcess == null) return false;
        var junction = Path.GetFullPath(GetCurrentClientJunction());
        var profilePath = Path.GetFullPath(_appFiles.Client.GetDirectory(profileRef));
        return string.Equals(junction, profilePath, StringComparison.Ordinal);
    }

    private bool IsServerProfileLocked(ServerProfileRef profileRef)
    {
        var profilePath = Path.GetFullPath(_appFiles.Server.GetDirectory(profileRef));
        foreach (var s in _serverProcesses.Values)
        {
            var instance = s.Infos.Instance;
            var junction = Path.GetFullPath(GetCurrentServerJunction(instance));
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
    }
}
