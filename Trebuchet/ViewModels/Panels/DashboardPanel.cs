using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using tot_lib;
using Trebuchet.Assets;
using Trebuchet.Services;
using Trebuchet.ViewModels.InnerContainer;
using TrebuchetLib;
using TrebuchetLib.OsSpecific;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels.Panels
{
    public class DashboardPanel : ReactiveObject, ITickingPanel, IRefreshablePanel, IDisplablePanel, IRefreshingPanel, IBottomPanel
    {
        public DashboardPanel(
            ITrebuchetOsSpecific tOsSpecific,
            AppSetup setup, 
            UIConfig uiConfig, 
            AppFiles appFiles, 
            Launcher launcher, 
            DialogueBox box,
            TaskBlocker blocker,
            Operations operations,
            ILogger<DashboardPanel> logger) 
        {
            _tOsSpecific = tOsSpecific;
            _setup = setup;
            _uiConfig = uiConfig;
            _appFiles = appFiles;
            _launcher = launcher;
            _box = box;
            _blocker = blocker;
            _operations = operations;
            _logger = logger;
            CanBeOpened = Tools.IsClientInstallValid(_setup.Config, _setup.Edition) || Tools.IsServerInstallValid(_setup.Config);

            launcher.SequenceProgressChanged += OnSequenceProgressChanged;

            var canDownloadServer = blocker.WhenAnyValue(x => x.CanDownloadServer);
            var canDownloadMods = blocker.WhenAnyValue(x => x.CanDownloadMods);
            CloseAllCommand = ReactiveCommand.CreateFromTask(OnCloseAll);
            KillAllCommand = ReactiveCommand.CreateFromTask(OnKillAll);
            LaunchAllCommand = ReactiveCommand.CreateFromTask(
                canExecute: canDownloadServer, execute: OnLaunchAll);
            UpdateServerCommand = ReactiveCommand.CreateFromTask(
                canExecute: canDownloadServer, execute: UpdateServer);
            UpdateAllModsCommand = ReactiveCommand.CreateFromTask(
                canExecute: canDownloadMods, execute:UpdateMods);
            VerifyFilesCommand = ReactiveCommand.CreateFromTask(
                canExecute: canDownloadMods, execute:OnFileVerification);

            Client = new ClientInstanceDashboard(new ProcessStatsLight(), _blocker, _box);
            ConfigureClient(Client);
            RefreshClientSelection(_setup.Config.SelectedClientProfile, _setup.Config.SelectedClientModlist);
        }

        private readonly ITrebuchetOsSpecific _tOsSpecific;
        private readonly AppSetup _setup;
        private readonly UIConfig _uiConfig;
        private readonly AppFiles _appFiles;
        private readonly Launcher _launcher;
        private readonly DialogueBox _box;
        private readonly TaskBlocker _blocker;
        private readonly Operations _operations;
        private readonly ILogger<DashboardPanel> _logger;
        private bool _canBeOpened;
        private bool _serverUpdateAvailable;
        private bool _anyModUpdate;

        public string Icon => @"mdi-view-dashboard";
        public string Label => Resources.PanelDashboard;

        public bool CanBeOpened
        {
            get => _canBeOpened;
            set => this.RaiseAndSetIfChanged(ref _canBeOpened, value);
        }

        public bool ServerUpdateAvailable
        {
            get => _serverUpdateAvailable;
            set => this.RaiseAndSetIfChanged(ref _serverUpdateAvailable, value);
        }

        public bool AnyModUpdate
        {
            get => _anyModUpdate;
            set => this.RaiseAndSetIfChanged(ref _anyModUpdate, value);
        }

        public bool CanDisplayServers => _setup.Config is { ServerInstanceCount: > 0 };
        public ClientInstanceDashboard Client { get; }
        public ObservableCollection<ServerInstanceDashboard> Instances { get; } = [];
        public ReactiveCommand<Unit,Unit> CloseAllCommand { get; }
        public ReactiveCommand<Unit,Unit> KillAllCommand { get; }
        public ReactiveCommand<Unit,Unit> LaunchAllCommand { get; }
        public ReactiveCommand<Unit,Unit> UpdateAllModsCommand { get; }
        public ReactiveCommand<Unit,Unit> UpdateServerCommand { get; }
        public ReactiveCommand<Unit,Unit> VerifyFilesCommand { get; }
        
        public event AsyncEventHandler? RequestRefresh;


        /// <summary>
        ///     Collect all used mods of all the client and server instances and update them. Will not perform any action if the
        ///     game is running or the main task is blocked.
        /// </summary>
        public async Task UpdateMods()
        {
            _logger.LogInformation(@"Updating mods");
            try
            {
                await _launcher.UpdateMods();
                await OnRequestRefresh();
            }
            catch (Exception tex)
            {
                _logger.LogError(tex, @"Failed");
                await _box.OpenErrorAsync(tex.Message);
            }
        }

        /// <summary>
        ///     Update all server instances. Will not perform any action if the game is running or the main task is blocked.
        /// </summary>
        public async Task UpdateServer()
        {
            _logger.LogInformation(@"Updating servers");
            try
            {
                await _launcher.UpdateServers();
                await RefreshPanel();
            }
            catch (Exception tex)
            {
                _logger.LogError(tex, @"Failed");
                await _box.OpenErrorAsync(tex.Message);
            }
        }
        
        public async Task KillClient()
        {
            if (!Client.ProcessRunning) return;

            if (_uiConfig.DisplayWarningOnKill)
            {
                var confirm = new OnBoardingConfirmation(Resources.Kill, Resources.KillText);
                await _box.OpenAsync(confirm);
                if (!confirm.Result) return;
            }

            _logger.LogWarning(@"Killing client");
            try
            {
                await _launcher.KillClient();
            }
            catch (Exception tex)
            {
                _logger.LogError(tex, @"Failed");
                await _box.OpenErrorAsync(tex.Message);
            }
        }
        
        public async Task LaunchClient(ClientConnectionRef? autoConnect)
        {
            if (Client.SelectedModlist?.ModList is null || Client.SelectedProfile is null) return;
            if (Client.ProcessRunning) return;
            if (!await CheckForSteamClientRunning()) return;

            var data = new Dictionary<string, object>
            {
                { @"autoConnect", autoConnect?.Connection ?? string.Empty },
                { @"isBattleEye", Client.BattleEye }
            };
            using(_logger.BeginScope(data))
                _logger.LogInformation(@"Launching client");
            Client.CanLaunch = false;
            try
            {
                _setup.Config.SelectedClientProfile = Client.SelectedProfile.Uri.OriginalString;
                _setup.Config.SelectedClientModlist = Client.SelectedModlist.ModList.Uri.OriginalString;
                await _launcher.CatapultClient(Client.BattleEye, autoConnect);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, @"Failed");
                await _box.OpenErrorAsync(ex.Message);
                Client.CanLaunch = true;
            }
        }
        
        public async Task CloseServer(int instance)
        {
            var dashboard = GetServerInstance(instance);
            if (!dashboard.ProcessRunning) return;

            var dialogue = new ServerActionDelayDialogue(Resources.ServerStopDelayTitle,
                string.Format(Resources.ServerStopDelayText, instance), [1, 5, 10, 15, 30, 60]);
            await _box.OpenAsync(dialogue);
            if (dialogue.Result is not { } delay) return;

            _logger.LogInformation(@"Closing server {instance} in {delay}", instance, delay);
            try
            {
                await _launcher.CloseServerIn(instance, delay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, @"Failed");
                await _box.OpenErrorAsync(ex.Message);
            }
        }

        public async Task RestartServer(int instance)
        {
            var dashboard = GetServerInstance(instance);
            if (!dashboard.ProcessRunning) return;

            var dialogue = new ServerActionDelayDialogue(Resources.ServerRestartDelayTitle,
                string.Format(Resources.ServerRestartDelayText, instance), [1, 5, 10, 15, 30, 60, 120]);
            await _box.OpenAsync(dialogue);
            if (dialogue.Result is not { } delay) return;

            _logger.LogInformation(@"Restarting server {instance} in {delay}", instance, delay);
            try
            {
                await _launcher.RestartServerIn(instance, delay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, @"Failed");
                await _box.OpenErrorAsync(ex.Message);
            }
        }
        
        /// <summary>
        /// Cancels a running stop or restart countdown and/or pauses the automatic restarts, e.g. during an RP event.
        /// </summary>
        public async Task PauseAutomaticRestarts(int instance)
        {
            var dashboard = GetServerInstance(instance);
            if (!dashboard.ProcessRunning) return;

            var countdown = _launcher.IsCountdownRunning(instance);
            int[] presets = [30, 60, 120, 180, 240, 360];
            var dialogue = countdown
                ? new ServerActionDelayDialogue(Resources.CancelCountdownTitle,
                    string.Format(Resources.CancelCountdownText, instance), presets,
                    nowLabel: Resources.PauseNone, confirmLabel: Resources.PauseStart)
                : new ServerActionDelayDialogue(Resources.PauseRestartsTitle,
                    string.Format(Resources.PauseRestartsText, instance), presets,
                    includeNow: false, confirmLabel: Resources.PauseStart);
            dialogue.CustomMinutes = 90;
            await _box.OpenAsync(dialogue);
            if (dialogue.Result is not { } pause) return;

            _logger.LogInformation(@"Pausing automatic restarts of server {instance} for {pause}", instance, pause);
            try
            {
                await _launcher.PauseAutomaticRestarts(instance, pause);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, @"Failed");
                await _box.OpenErrorAsync(ex.Message);
            }
            dashboard.SetAutomaticRestarts(_launcher.IsCountdownRunning(instance), _launcher.GetAutomaticRestartsPause(instance));
        }

        public Task ResumeAutomaticRestarts(int instance)
        {
            _launcher.ResumeAutomaticRestarts(instance);
            GetServerInstance(instance).SetAutomaticRestarts(_launcher.IsCountdownRunning(instance), null);
            return Task.CompletedTask;
        }

        public async Task CopyConnectionInfo(int instance)
        {
            try
            {
                if (string.IsNullOrEmpty(ExternalIpProvider.Cached))
                    await ExternalIpProvider.GetAsync();
                var text = Utils.Utils.BuildConnectionInfo(_launcher.CreateMacroContext(instance));
                await Utils.Utils.SetClipboardText(text);
                _logger.LogInformation(@"Connection information of instance {instance} copied", instance);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, @"Failed");
                await _box.OpenErrorAsync(ex.Message);
            }
        }

        public async Task KillServer(int instance)
        {
            var dashboard = GetServerInstance(instance);
            if (!dashboard.ProcessRunning) return;

            if (_uiConfig.DisplayWarningOnKill)
            {
                var confirm = new OnBoardingConfirmation(Resources.Kill, Resources.KillText);
                await _box.OpenAsync(confirm);
                if (!confirm.Result) return;
            }

            _logger.LogWarning(@"Killing server {instance}", instance);
            try
            {
                await _launcher.KillServer(dashboard.Instance);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, @"Failed");
                await _box.OpenErrorAsync(ex.Message);
            }
        }
        
        public async Task LaunchServer(int instance)
        {
            var dashboard = GetServerInstance(instance);
            if (dashboard.SelectedModlist is null || dashboard.SelectedProfile is null) return;
            
            if(dashboard.ProcessRunning) return;
            dashboard.CanLaunch = false;

            using(_logger.BeginScope((@"instance", instance)))
                _logger.LogInformation(@"Launching server");
            
            try
            {
                _setup.Config.SetInstanceParameters(dashboard.Instance, 
                    dashboard.SelectedModlist.ModList.Uri.OriginalString,
                    dashboard.SelectedProfile.Uri.OriginalString);
                await _launcher.CatapultServer(dashboard.Instance);
                var process = _launcher.GetServerProcesses()
                    .FirstOrDefault(x => x.Infos.Instance == dashboard.Instance);
                await dashboard.ProcessRefresh(process, _uiConfig.DisplayProcessPerformance);
                // A starting sequence logs its failures instead of throwing and is cancelled by stop/kill: without a
                // server process the start button has to work again, it stayed disabled until Trebuchet restarted.
                if (process is null)
                    dashboard.CanLaunch = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, @"Failed");
                await _box.OpenErrorAsync(ex.Message);
                dashboard.CanLaunch = true;
            }
        }

        public ServerInstanceDashboard GetServerInstance(int instance)
        {
            if(instance < 0 || instance >= Instances.Count)
                throw new Exception(@"Instance out of range");
            return Instances[instance];
        }

        public async Task TickPanel()
        {
            await _launcher.Tick();
            Utils.PowerManagement.KeepAwake(_uiConfig.KeepSystemAwake && _launcher.IsAnyServerRunning());

            if (Client.SelectedModlist is not null)
                Client.UpdateNeeded = _launcher.HasModListUpdates(Client.SelectedModlist.ModList);
            foreach(var server in Instances)
                if (server.SelectedModlist is not null)
                    server.UpdateNeeded = _launcher.HasModListUpdates(server.SelectedModlist.ModList);
            AnyModUpdate = Instances.Any(x => x.UpdateNeeded) || Client.UpdateNeeded;
            ServerUpdateAvailable = _launcher.HasServerUpdate();
            
            var refreshes = new List<Task> { Client.ProcessRefresh(_launcher.GetClientProcess(), _uiConfig.DisplayProcessPerformance) };
            // Every instance is refreshed, also those without a process: a server that stopped or was removed
            // would otherwise keep showing as running.
            var processes = _launcher.GetServerProcesses().ToList();
            for (var i = 0; i < Instances.Count; i++)
            {
                var instance = i;
                refreshes.Add(Instances[i].ProcessRefresh(processes.FirstOrDefault(x => x.Infos.Instance == instance), _uiConfig.DisplayProcessPerformance));
                Instances[i].SetAutomaticRestarts(_launcher.IsCountdownRunning(instance), _launcher.GetAutomaticRestartsPause(instance));
            }
            await Task.WhenAll(refreshes);
        }

        public Task RefreshPanel()
        {
            _logger.LogDebug(@"Refresh panel");
            CanBeOpened = Tools.IsClientInstallValid(_setup.Config, _setup.Edition) || Tools.IsServerInstallValid(_setup.Config);
            Client.CanUseDashboard = Tools.IsClientInstallValid(_setup.Config, _setup.Edition);
            int installedCount = _operations.GetInstalledServerInstanceCount();
            foreach (var instance in Instances)
            {
                instance.CanUseDashboard = _setup.Config.ServerInstanceCount > instance.Instance &&
                                           installedCount > instance.Instance;
            }
            
            return Task.CompletedTask;
        }

        public async Task DisplayPanel()
        {
            _logger.LogDebug(@"Display panel");
            CreateInstancesIfNeeded();
            RefreshClientSelection();
            RefreshServerSelection();
            await RefreshPanel();
        }
        
        private void OnSequenceProgressChanged(object? sender, SequenceProgress e)
        {
            var instance = Instances.FirstOrDefault(x => x.Instance == e.Instance);
            if (instance is null) return;
            instance.SetSequenceProgress(e);
        }

        private void ConfigureClient(ClientInstanceDashboard client)
        {
            client.ModlistSelected += (_, modlist) =>
            {
                _setup.Config.SelectedClientModlist = modlist.Uri.OriginalString;
                _setup.Config.SaveFile();
                return _launcher.CheckModUpdates();
            };
            client.ProfileSelected += (_, profile) =>
            {
                _setup.Config.SelectedClientProfile = profile.Uri.OriginalString;
                _setup.Config.SaveFile();
                return Task.CompletedTask;
            };
            client.KillClicked += (_, _)  => KillClient();
            client.LaunchClicked += (_, autoConnect) => LaunchClient(autoConnect);
            client.UpdateClicked += (_, _) => UpdateMods();
        }

        private void RefreshClientSelection()
        {
            RefreshClientSelection(Client.SelectedProfile, Client.SelectedModlist?.ModList);
            Client.RefreshPanel();
        }

        private void RefreshClientSelection(string profile, string modlist)
        {
            RefreshClientSelection(_appFiles.Client.Resolve(profile), _appFiles.ResolveModList(modlist));
        }
        private void RefreshClientSelection(ClientProfileRef? profile, IPRefWithModList? modlist)
        {
            profile = profile is null ? _appFiles.Client.GetDefault() : _appFiles.Client.Resolve(profile);
            modlist = _appFiles.ResolveModListSelection(modlist);
            
            Client.Modlists = [];
            Client.Modlists.AddRange(_appFiles.Mods.GetList().Select(x => new ModListRefViewModel(x)));
            Client.Modlists.AddRange(_appFiles.Sync.GetList().Select(x => new ModListRefViewModel(x)));
            
            Client.Profiles = _appFiles.Client.GetList().ToList();
            Client.SelectedProfile = profile;
            Client.SelectedModlist = new ModListRefViewModel(modlist);
        }

        private void RefreshServerSelection()
        {
            foreach (var dashboard in Instances)
                RefreshServerSelection(dashboard);
        }

        private void RefreshServerSelection(ServerInstanceDashboard dashboard)
        {
            dashboard.SelectedModlist = new ModListRefViewModel(_appFiles.ResolveModListSelection(dashboard.SelectedModlist?.ModList));
            dashboard.SelectedProfile = dashboard.SelectedProfile is null
                ? _appFiles.Server.GetDefault()
                : _appFiles.Server.Resolve(dashboard.SelectedProfile);
            
            dashboard.Profiles = _appFiles.Server.GetList().ToList();
            dashboard.Modlists = [];
            dashboard.Modlists.AddRange(_appFiles.Mods.GetList().Select(x => new ModListRefViewModel(x)));
            dashboard.Modlists.AddRange(_appFiles.Sync.GetList().Select(x => new ModListRefViewModel(x)));
        }

        private void CreateInstancesIfNeeded()
        {
            if (Instances.Count >= _setup.Config.ServerInstanceCount)
                return;

            for (var i = Instances.Count; i < _setup.Config.ServerInstanceCount; i++)
            {
                var instance = new ServerInstanceDashboard(i, new ProcessStatsLight(), _blocker, _box);
                instance.SelectedModlist = new ModListRefViewModel(_appFiles.ResolveModList(_setup.Config.GetInstanceModlist(i)));
                instance.SelectedProfile = _appFiles.Server.Resolve(_setup.Config.GetInstanceProfile(i));
                
                RegisterServerInstanceEvents(instance);
                Instances.Add(instance);
            }
        }

        private void RegisterServerInstanceEvents(ServerInstanceDashboard instance)
        {
            instance.ModlistSelected += (_, arg) =>
            {
                _setup.Config.SetInstanceModlist(arg.Instance, arg.Selection.Uri.OriginalString);
                _setup.Config.SaveFile();
                return _launcher.CheckModUpdates();
            };
            instance.ProfileSelected += (_, arg) =>
            {
                _setup.Config.SetInstanceProfile(arg.Instance, arg.Selection.Uri.OriginalString);
                _setup.Config.SaveFile();
                return Task.CompletedTask;
            };
            instance.KillClicked += (_, arg) => KillServer(arg);
            instance.LaunchClicked += (_, arg) => LaunchServer(arg);
            instance.CloseClicked += (_, arg) => CloseServer(arg);
            instance.RestartClicked += (_, arg) => RestartServer(arg);
            instance.PauseRestartsClicked += (_, arg) => PauseAutomaticRestarts(arg);
            instance.ResumeRestartsClicked += (_, arg) => ResumeAutomaticRestarts(arg);
            instance.CopyConnectionClicked += (_, arg) => CopyConnectionInfo(arg);
            instance.UpdateClicked += (_, _) => UpdateMods();
        }

        private async Task OnCloseAll()
        {
            foreach (var i in Instances)
                await CloseServer(i.Instance);
        }

        private async Task OnFileVerification()
        {
            var confirm = new OnBoardingConfirmation(Resources.VerifyFiles, Resources.VerifyFilesText);
            await _box.OpenAsync(confirm);
            if (!confirm.Result) return;

            _logger.LogInformation(@"File Verification");
            try
            {
                await _launcher.VerifyFiles();
            }
            catch (OperationCanceledException){}
            catch (Exception tex)
            {
                _logger.LogError(tex, @"Failed");
                await _box.OpenErrorAsync(tex.Message);
            }
        }

        private async Task OnKillAll()
        {
            foreach (var i in Instances)
                await KillServer(i.Instance);
        }

        private async Task OnLaunchAll()
        {
            foreach(var i in Instances)
                await LaunchServer(i.Instance);
        }

        private async Task OnRequestRefresh()
        {
            if (RequestRefresh is not null)
                await RequestRefresh.Invoke(this, EventArgs.Empty);
        }

        private async Task<bool> CheckForSteamClientRunning()
        {
            var process = await _tOsSpecific.GetProcessesWithName(Constants.SteamClientExe);
            if (process.Count > 0) return true;

            await _box.OpenErrorAsync(Resources.OnBoardingSteamClientOffline,
                Resources.OnBoardingSteamClientOfflineSub);
            return false;
        }
    }
}
