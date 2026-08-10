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
        
        await EnsureServerSavedJunction(instance, profile.ProfileFolder);

        await _setup.WriteIni(profile, instance);
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
            if (!_appFiles.Server.TryResolve(uri, out var reference) 
                || reference.Get().StoppingSequence.Actions.Count == 0
                || _serverSequences.ContainsKey(instance))
            {
                if (reference is not null && !_serverSequences.ContainsKey(instance))
                {
                    await DiscordWebHooks.Notify(reference.Get(), _setup.GetServerShutdownNotification(_lastRestartReason));
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
            if (!_appFiles.Server.TryResolve(uri, out var reference) 
                || reference.Get().StoppingSequence.Actions.Count == 0
                || _serverSequences.ContainsKey(instance))
            {
                if (reference is not null && !_serverSequences.ContainsKey(instance))
                {
                    await DiscordWebHooks.Notify(reference.Get(), _setup.GetServerShutdownNotification(_lastRestartReason));
                    _lastRestartReason = string.Empty;
                }
                await watcher.RestartAsync();
                return;
            }

            await RestartServerWithSequence(instance, reference.Get());
        }
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
            await CancelServerSequence(instance);
            await watcher.KillAsync();
        }
    }

    public async Task Tick()
    {
        if (!_hasCatapulted && _setup.Catapult)
        {
            _hasCatapulted = true;
            for (int i = 0; i < _setup.Config.ServerInstanceCount; i++)
                if(!_serverProcesses.ContainsKey(i))
                    await CatapultServer(i);
        } else if (!_hasCatapulted)
        {
            _hasCatapulted = true;
            for (int i = 0; i < _setup.Config.ServerInstanceCount; i++)
            {
                if(_serverProcesses.ContainsKey(i)) continue;
                var profileUri = _setup.Config.GetInstanceProfile(i);
                var serverPRef = _appFiles.Server.Resolve(profileUri);
                var serverProfile = serverPRef.Get();
                if(serverProfile.RestartWhenDown)
                    await CatapultServer(i);
            }
        }

        await CleanStoppedProcesses();

        // Process discovery is relatively expensive (WMI); refresh every tick when missing, else every other tick.
        _tickCounter++;
        var needClientScan = _conanClientProcess is null || (_tickCounter % 2) == 0;
        var needServerScan = _serverProcesses.Count < _setup.Config.ServerInstanceCount || (_tickCounter % 2) == 0;
        if (needClientScan)
            await FindExistingClient();
        if (needServerScan)
            await FindExistingServers();

        await PerformPeriodicUpdateCheck();
        await PerformAutomaticRestarts();

        var refreshTasks = new List<Task>();
        if (_conanClientProcess is not null)
            refreshTasks.Add(_conanClientProcess.RefreshAsync());
        foreach (var process in _serverProcesses.Values)
        {
            var name = _appFiles.Server.Resolve(_setup.Config.GetInstanceProfile(process.Infos.Instance));
            if (_appFiles.Server.Exists(name))
            {
                var profile = _appFiles.Server.Get(name);
                process.KillZombies = profile.KillZombies;
                process.ZombieCheckSeconds = profile.ZombieCheckSeconds;
            }
            refreshTasks.Add(process.RefreshAsync());
        }
        if (refreshTasks.Count > 0)
            await Task.WhenAll(refreshTasks);
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
        if (IsAnyServerRunning() || IsClientRunning()) return true;
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
        if (IsAnyServerRunning() || IsClientRunning()) return true;
        
        try
        {
            using(_logger.BeginScope(("mods", mods)))
                _logger.LogInformation("Updating mods");
            await _steam.UpdateMods(mods);
            await CheckModUpdates();
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
        if (IsAnyServerRunning() || IsClientRunning()) return true;

        try
        {
            _logger.LogInformation("Updating servers");
            await _steam.UpdateServerInstances();
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
            
            var details = await _steam.RequestModDetails(lists.GetModsFromList().ToList());
            if (details.Count == 0) return true;
            
            _modNeedUpdate.Clear();;
            _modNeedUpdate.AddRange(details
                .Where(d => d.Status.Status != UGCStatus.UpToDate));
            _modListNeedUpdate.AddRange(lists.Where(x => x.GetModsFromList().Intersect(_modNeedUpdate.Select(y => y.PublishedFileId)).Any()));
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

    private async Task RunSequence(int instance, Sequence sequence, Func<Task> mainAction)
    {
        if (sequence.Actions.Count == 0) return;
        await CancelServerSequence(instance);

        var args = new SequenceArgs()
        {
            BackupManager = _backupManager,
            Instance = instance,
            Launcher = this,
            Logger = _logger,
            Reason = _lastRestartReason,
            MainAction = mainAction
        };
        _lastRestartReason = string.Empty;
        var runner = new SequenceRunner(sequence, args, this);
        _serverSequences[instance] = runner;

        try
        {
            await runner.ExecuteSequence();
            _serverSequences.Remove(instance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute sequence");
            Report(new SequenceProgress(instance, 0, 0));
        }
    }

    private async Task CancelServerSequence(int instance)
    {
        if (_serverSequences.TryGetValue(instance, out var oldSequence))
        {
            await oldSequence.Cts.CancelAsync();
            _serverSequences.Remove(instance);
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
        if (IsClientRunning()) return; // can't auto-update if any client is running
        
        foreach (var process in _serverProcesses.Values)
        {
            if(!process.State.IsRunning() || process.State.IsStopping()) continue;
            if(_serverSequences.ContainsKey(process.Infos.Instance)) continue;
            
            if (_serverNeedUpdate)
            {
                _lastRestartReason = _setup.GetReasonServerUpdate();
                RestartServer(process.Infos.Instance);
                continue;
            }
                    
            var modListRef = _appFiles.ResolveModList(_setup.Config.GetInstanceModlist(process.Infos.Instance));
            if (_modListNeedUpdate.Contains(modListRef))
            {
                var mods = modListRef.GetModsFromList();
                _lastRestartReason
                    = _setup.GetReasonModUpdate(_modNeedUpdate.Where(x => mods.Contains(x.PublishedFileId)));
                RestartServer(process.Infos.Instance);
            }
        }
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
        var args = profile.GetClientArgs(modlistFile, autoConnect);

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
        process.PriorityClass = GetPriority(priority);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            process.ProcessorAffinity = (IntPtr)Tools.Clamp2CPUThreads(threadAffinity);
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
                    await DiscordWebHooks.Notify(reference.Get(), _setup.GetCrashNotification(process.Infos.Title));
                    return;
                case ProcessState.ONLINE:
                    await DiscordWebHooks.Notify(reference.Get(), _setup.GetOnlineNotification(process.Infos.Title));
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
            if (server.Value.Process.HasExited)
            {
                try { server.Value.Process.Refresh(); } catch { /* best effort */ }
                try
                {
                    exitCodeText = server.Value.Process.ExitCode.ToString();
                }
                catch (InvalidOperationException)
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
            OnStateChanged();
            var name = _appFiles.Server.Resolve(_setup.Config.GetInstanceProfile(server.Key));
            if ((server.Value.State == ProcessState.CRASHED && _appFiles.Server.Get(name).RestartWhenDown) 
                || server.Value.RequestRestart)
            {
                await CatapultServer(server.Key);
            }
            server.Value.Dispose();
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
        if (_osSpecific.IsSymbolicLink(path))
            return _osSpecific.GetSymbolicLinkTarget(path);
        return string.Empty;
    }

    private async Task EnsureServerSavedJunction(int instance, string profileFolder)
    {
        var savedPath = Path.Combine(_setup.GetInstancePath(instance), Constants.FolderGameSave);
        var profileFolderFull = Path.GetFullPath(profileFolder);

        if (_osSpecific.IsSymbolicLink(savedPath))
        {
            var currentTarget = Path.GetFullPath(_osSpecific.GetSymbolicLinkTarget(savedPath));
            if (!string.Equals(currentTarget, profileFolderFull, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Removing server Saved junction at {savedPath} pointing to {target}",
                    savedPath,
                    currentTarget);
                _osSpecific.RemoveSymbolicLink(savedPath);
            }
        }
        else if (Directory.Exists(savedPath))
        {
            _logger.LogInformation(
                "Migrating real Saved directory at {savedPath} into profile {profileFolder}",
                savedPath,
                profileFolder);
            var savedInfo = new DirectoryInfo(savedPath);
            var childJunctions = savedInfo.GetDirectories()
                .Where(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .ToList();

            Directory.CreateDirectory(profileFolder);

            foreach (var file in savedInfo.GetFiles())
                file.CopyTo(Path.Combine(profileFolder, file.Name), true);

            foreach (var child in savedInfo.GetDirectories())
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                await Tools.DeepCopyAsync(child.FullName, Path.Combine(profileFolder, child.Name), CancellationToken.None);
            }

            foreach (var junction in childJunctions)
            {
                var target = Path.GetFullPath(_osSpecific.GetSymbolicLinkTarget(junction.FullName));
                var destDir = Path.Combine(profileFolder, junction.Name);
                if (!IsPathUnderDirectory(target, profileFolderFull))
                {
                    _logger.LogInformation(
                        "Copying junction target {target} into profile {destDir}",
                        target,
                        destDir);
                    await Tools.DeepCopyAsync(target, destDir, CancellationToken.None);
                }

                _osSpecific.RemoveSymbolicLink(junction.FullName);
            }

            Directory.Delete(savedPath, true);
        }

        SetupJunction(savedPath, profileFolder);
    }

    private static bool IsPathUnderDirectory(string path, string parentDirectory)
    {
        var fullPath = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var fullParent = Path.GetFullPath(parentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(fullPath, fullParent, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = fullParent + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void SetupJunction(string junction, string targetPath)
    {
        _logger.LogInformation("Setup new junction {junction} > {target}", junction, targetPath);
        Directory.CreateDirectory(targetPath);
        _osSpecific.MakeSymbolicLink(junction, targetPath);
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}