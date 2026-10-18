using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Humanizer;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using AppResources = Trebuchet.Assets.Resources;
using Trebuchet.Services;
using Trebuchet.ViewModels.InnerContainer;
using Trebuchet.ViewModels.Sequences;
using Trebuchet.ViewModels.SettingFields;
using Trebuchet.Windows;
using TrebuchetLib;
using TrebuchetLib.Sequences;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels.Panels
{
    public class ServerProfilePanel : ReactiveObject, IRefreshablePanel, IDisplablePanel
    {
        private readonly AppSetup _setup;
        private readonly AppFiles _appFiles;
        private readonly UIConfig _uiConfig;
        private readonly Launcher _launcher;
        private readonly ILogger<ServerProfilePanel> _logger;
        private ServerProfile _profile;
        private string _profileSize = string.Empty;
        private bool _canBeOpened;
        private Dictionary<Sequence, SequenceEditor> _sequenceWindows = [];

        public ServerProfilePanel(
            DialogueBox box,
            AppSetup setup,
            AppFiles appFiles,
            UIConfig uiConfig,
            Launcher launcher,
            ILogger<ServerProfilePanel> logger
            )
        {
            _setup = setup;
            _appFiles = appFiles;
            _uiConfig = uiConfig;
            _launcher = launcher;
            _logger = logger;
            CanBeOpened = Tools.IsServerInstallValid(_setup.Config);

            CreateExampleProfileOnce();
            var startingProfile = _appFiles.Server.Resolve(_uiConfig.CurrentServerProfile);
            _profile = _appFiles.Server.Get(startingProfile);
            UpdateStoredDefaults();
            SaveProfile = ReactiveCommand.Create(() => _profile.SaveFile());
            SaveConfig = ReactiveCommand.Create(() => _setup.Config.SaveFile());

            FileMenu = new FileMenuViewModel<ServerProfile, ServerProfileRef>(AppResources.PanelServerSaves, appFiles.Server, box, _logger);
            FileMenu.FileSelected += OnFileSelected;
            FileMenu.Selected = startingProfile;
        }

        private bool _fieldsBuilt;

        public ObservableCollection<FieldElement> Fields { get; } = [];

        public ReactiveCommand<Unit,Unit> SaveProfile { get; }
        public ReactiveCommand<Unit,Unit> SaveConfig { get; }
        
        public FileMenuViewModel<ServerProfile, ServerProfileRef> FileMenu { get; }

        public string ProfileSize
        {
            get => _profileSize;
            set => this.RaiseAndSetIfChanged(ref _profileSize, value);
        }

        public string Icon => @"mdi-server-network";
        public string Label => AppResources.PanelServerSaves;

        public bool CanBeOpened
        {
            get => _canBeOpened;
            set => this.RaiseAndSetIfChanged(ref _canBeOpened, value);
        }

        public async Task RefreshPanel()
        {
            EnsureFields();
            _logger.LogDebug(@"Refresh panel");
            CanBeOpened = Tools.IsServerInstallValid(_setup.Config);
            if (FileMenu.Selected is not { } selected) return;
            _profile = _appFiles.Server.Get(selected);
            // New profiles start with the English library defaults: show them in the UI language.
            if (LocalizedDefaults.LocalizeProfile(_profile) | LogFilterRules.EnsureRequiredServerFilters(_profile.LogFilters))
                _profile.SaveFile();
            await RefreshProfileSize(selected);
            foreach (var f in Fields.OfType<IRefreshableField>())
                f.Update.Execute().Subscribe();
        }

        public async Task DisplayPanel()
        {
            EnsureFields();
            _logger.LogDebug(@"Display panel");
            if (FileMenu.Selected is { } selected) await RefreshProfileSize(selected);
        }

        private void EditSequence(Sequence sequence)
        {
            if (_sequenceWindows.TryGetValue(sequence, out var window))
            {
                window.Focus();
                return;
            }

            var vm = new SequenceViewModel(sequence);
            vm.SequenceChanged += OnSequenceChanged;
            var win = new SequenceEditor();
            win.Closing += (_, _) =>
            {
                _sequenceWindows.Remove(sequence);
            };
            win.DataContext = vm;
            _sequenceWindows[sequence] = win;
            win.Show();
        }

        private void OnSequenceChanged(object? sender, EventArgs e)
        {
            if (sender is not SequenceViewModel vm) return;
            
            vm.Sequence.Actions.Clear();
            vm.Sequence.Actions.AddRange(vm.GetSequence());
            _profile.SaveFile();
            
            foreach (var f in Fields.OfType<IRefreshableField>())
                f.Update.Execute().Subscribe();
        }

        private Task OnFileSelected(object? sender, ServerProfileRef? profile)
        {
            if (profile is null) return Task.CompletedTask;
            foreach (var win in _sequenceWindows.Values)
                win.Close();
            _uiConfig.CurrentServerProfile = profile.Uri.OriginalString;
            _uiConfig.SaveFile();
            return RefreshPanel();
        }
        
        private async Task RefreshProfileSize(ServerProfileRef profile)
        {
            var path = _appFiles.Server.GetDirectory(profile);
            var size = await Task.Run(() => Tools.DirectorySize(path));
            ProfileSize = size.Bytes().Humanize();
        }

        /// <summary>
        /// Untouched default texts (notifications, restart messages) follow the UI language; custom texts stay.
        /// </summary>
        private async Task<string> SendRConTestMessage()
        {
            if (FileMenu.Selected is not { } selected) return string.Empty;
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var results = await _launcher.SendRConTestMessage(selected.Name, AppResources.RConTestMessage, timeout.Token);
            if (results.Count == 0) return AppResources.RConTestNoServer;
            return string.Join(Environment.NewLine, results.Select(r =>
                !r.RConConnected ? string.Format(AppResources.RConTestNoRCon, r.Instance)
                : r.Error is not null ? string.Format(AppResources.RConTestFailed, r.Instance, r.Command, r.Error.Message)
                : string.Format(AppResources.RConTestSent, r.Instance, r.Command,
                    string.IsNullOrWhiteSpace(r.Response) ? AppResources.RConTestEmptyAnswer : r.Response.Trim())));
        }

        private void CreateExampleProfileOnce()
        {
            if (_uiConfig.ExampleServerProfileCreated) return;
            try
            {
                var created = ExampleServerProfile.CreateIfMissing(_appFiles.Server);
                if (created is not null)
                    _logger.LogInformation(@"Created the example server save {Name}", created.Name);
                _uiConfig.ExampleServerProfileCreated = true;
                _uiConfig.SaveFile();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, @"Could not create the example server save");
            }
        }

        private void UpdateStoredDefaults()
        {
            try
            {
                if (LocalizedDefaults.LocalizeConfig(_setup.Config))
                    _setup.Config.SaveFile();
                foreach (var reference in _appFiles.Server.GetList())
                {
                    var profile = reference.Get();
                    if (LocalizedDefaults.LocalizeProfile(profile) | LogFilterRules.EnsureRequiredServerFilters(profile.LogFilters))
                        profile.SaveFile();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, @"Could not update the stored default texts and log filters");
            }
        }

        private MacroContext CreateMacroContext()
        {
            var selected = FileMenu.Selected;
            for (var i = 0; i < _setup.Config.ServerInstanceCount; i++)
            {
                if (selected is not null
                    && _appFiles.Server.TryResolve(_setup.Config.GetInstanceProfile(i), out var reference)
                    && reference.Equals(selected))
                    return _launcher.CreateMacroContext(i);
            }

            var fallback = _launcher.CreateMacroContext(0);
            return new MacroContext
            {
                Profile = _profile,
                ExternalIp = fallback.ExternalIp,
                EngineVersion = fallback.EngineVersion,
                BranchName = fallback.BranchName,
                ServerRevision = fallback.ServerRevision
            };
        }

        private void UseAsMultiHome(string address)
        {
            _profile.MultiHomeAddress = address;
            _profile.EnableMultiHome = true;
            _profile.SaveFile();
            foreach (var f in Fields.OfType<IRefreshableField>())
                f.Update.Execute().Subscribe();
        }

        private void EnsureFields()
        {
            if (_fieldsBuilt) return;
            _fieldsBuilt = true;
            BuildFields();
        }

        private void BuildFields()
        {
            Fields.Add(new TitleField().SetTitle(AppResources.CatServerSettings));
            Fields.Add(new TextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerName)
                .SetDescription(AppResources.SettingServerNameText)
                .SetGetter(() => _profile.ServerName)
                .SetSetter((v) => _profile.ServerName = v)
                .SetDefault(() => ServerProfile.ServerNameDefault)
            );
            Fields.Add(new PasswordField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerPass)
                .SetDescription(AppResources.SettingServerPassText)
                .SetGetter(() => _profile.ServerPassword)
                .SetSetter((v) => _profile.ServerPassword = v)
                .SetDefault(() => ServerProfile.ServerPasswordDefault)
            );
            Fields.Add(new PasswordField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerAdminPass)
                .SetDescription(AppResources.SettingServerAdminPassText)
                .SetGetter(() => _profile.AdminPassword)
                .SetSetter((v) => _profile.AdminPassword = v)
                .SetDefault(() => ServerProfile.AdminPasswordDefault)
            );
            Fields.Add(new IntField(0,int.MaxValue)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerMaxPlayer)
                .SetDescription(AppResources.SettingServerMaxPlayerText)
                .SetGetter(() => _profile.MaxPlayers)
                .SetSetter((v) => _profile.MaxPlayers = v)
                .SetDefault(() => ServerProfile.MaxPlayersDefault)
            );
            Fields.Add(new ComboBoxField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerRegion)
                .SetDescription(AppResources.SettingServerRegionText)
                .AddOption(AppResources.SettingServerRegionEurope)
                .AddOption(AppResources.SettingServerRegionNorthAmerica)
                .AddOption(AppResources.SettingServerRegionAsia)
                .AddOption(AppResources.SettingServerRegionAustralia)
                .AddOption(AppResources.SettingServerRegionSouthAmerica)
                .AddOption(AppResources.SettingServerRegionJapan)
                .SetGetter(() => _profile.ServerRegion)
                .SetSetter((v) => _profile.ServerRegion = v)
                .SetDefault(() => ServerProfile.ServerRegionDefault)
            );
            Fields.Add(new MapField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerMap)
                .SetDescription(AppResources.SettingServerMapText)
                .SetGetter(() => _profile.Map)
                .SetSetter((v) => _profile.Map = v)
                .SetDefault(() => ServerProfile.MapDefault)
            );
            Fields.Add(new ComboBoxField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerVoiceChat)
                .SetDescription(AppResources.SettingServerVoiceChatText)
                .AddOption(AppResources.VoiceChatUnchanged)
                .AddOption(AppResources.VoiceChatDisabled)
                .AddOption(AppResources.VoiceChatEnabled)
                .SetGetter(() => _profile.VoiceChatMode)
                .SetSetter((v) => _profile.VoiceChatMode = v)
                .SetDefault(() => ServerProfile.VoiceChatModeDefault)
            );
            Fields.Add(new TextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerAdditionalArgs)
                .SetDescription(AppResources.SettingServerAdditionalArgsText)
                .SetGetter(() => _profile.AdditionalArguments)
                .SetSetter((v) => _profile.AdditionalArguments = v)
                .SetDefault(() => ServerProfile.AdditionalArgumentsDefault)
            );
            Fields.Add(new MultiLineTextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingSudoAdminList)
                .SetDescription(AppResources.SettingSudoAdminListText)
                .SetGetter(() => string.Join(Environment.NewLine, _profile.SudoSuperAdmins))
                .SetSetter((v) => _profile.SudoSuperAdmins = v.Split(Environment.NewLine).ToList())
                .SetDefault(() => string.Join(Environment.NewLine, ServerProfile.SudoSuperAdminsDefault))
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingTotAdminPrecision)
                .SetDescription(AppResources.SettingTotAdminPrecisionText)
                .SetGetter(() => _profile.DisableHighPrecisionMoveTool)
                .SetSetter((v) => _profile.DisableHighPrecisionMoveTool = v)
                .SetDefault(() => ServerProfile.DisableHighPrecisionMoveToolDefault)
            );
            Fields.Add(new TitleField().SetTitle(AppResources.CatRestartSettings));
            Fields.Add(new SequenceEditorField(
                AppResources.SettingServerStartingSequenceText, 
                _profile.StartingSequence,
                ReactiveCommand.Create<Sequence>(EditSequence))
                .SetTitle(AppResources.SettingServerStartingSequence)
                );
            Fields.Add(new SequenceEditorField(
                    AppResources.SettingServerStoppingSequenceText, 
                    _profile.StoppingSequence,
                    ReactiveCommand.Create<Sequence>(EditSequence))
                .SetTitle(AppResources.SettingServerStoppingSequence)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerKillZombies)
                .SetDescription(AppResources.SettingServerKillZombiesText)
                .SetGetter(() => _profile.KillZombies)
                .SetSetter((v) => _profile.KillZombies = v)
                .SetDefault(() => ServerProfile.KillZombiesDefault)
            );
            Fields.Add(new IntField(30, int.MaxValue)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerZombieDuration)
                .SetDescription(AppResources.SettingServerZombieDurationText)
                .SetGetter(() => _profile.ZombieCheckSeconds)
                .SetSetter((v) => _profile.ZombieCheckSeconds = v)
                .SetDefault(() => ServerProfile.ZombieCheckSecondsDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerCrashRestart)
                .SetDescription(AppResources.SettingServerCrashRestartText)
                .SetGetter(() => _profile.RestartWhenDown)
                .SetSetter((v) => _profile.RestartWhenDown = v)
                .SetDefault(() => ServerProfile.RestartWhenDownDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerRequiresRestartFile)
                .SetDescription(AppResources.SettingServerRequiresRestartFileText)
                .SetGetter(() => _profile.RequiresRestartFileCheck)
                .SetSetter((v) => _profile.RequiresRestartFileCheck = v)
                .SetDefault(() => ServerProfile.RequiresRestartFileCheckDefault)
            );
            var autoRestart = new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerAutoRestart)
                .SetDescription(AppResources.SettingServerAutoRestartText)
                .SetGetter(() => _profile.AutoRestart)
                .SetSetter((v) => _profile.AutoRestart = v)
                .SetDefault(() => ServerProfile.AutoRestartDefault);
            var autoRestartTimes = new TimeOfDayListField(false)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerAutoRestartDailyTime)
                .SetDescription(AppResources.SettingServerAutoRestartDailyTimeText)
                .SetGetter(() => _profile.AutoRestartDailyTime)
                .SetSetter((v) => _profile.AutoRestartDailyTime = v)
                .SetDefault(() => ServerProfile.AutoRestartDailyTimeDefault);
            var autoRestartMaxPerDay = new IntField(minimum:0,maximum:Int32.MaxValue)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerAutoRestartMaxPerDay)
                .SetDescription(AppResources.SettingServerAutoRestartMaxPerDayText)
                .SetGetter(() => _profile.AutoRestartMaxPerDay)
                .SetSetter((v) => _profile.AutoRestartMaxPerDay = v)
                .SetDefault(() => ServerProfile.AutoRestartMaxPerDayDefault);
            var autoRestartMinUptime = new DurationField(minDuration:TimeSpan.FromMinutes(10),maxDuration:TimeSpan.MaxValue)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerAutoRestartMinUptime)
                .SetDescription(AppResources.SettingServerAutoRestartMinUptimeText)
                .SetGetter(() => _profile.AutoRestartMinUptime)
                .SetSetter((v) => _profile.AutoRestartMinUptime = v)
                .SetDefault(() => ServerProfile.AutoRestartMinUptimeDefault);
            autoRestart.WhenAnyValue(x => x.Value)
                .Subscribe(x =>
                {
                    autoRestartTimes.IsVisible = x;
                    autoRestartMaxPerDay.IsVisible = x;
                    autoRestartMinUptime.IsVisible = x;
                });
            Fields.Add(autoRestart);
            Fields.Add(autoRestartTimes);
            Fields.Add(autoRestartMaxPerDay);
            Fields.Add(autoRestartMinUptime);

            Fields.Add(new TitleField().SetTitle(AppResources.CatRestartWarnings));
            var useWarnings = new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerUseRestartWarnings)
                .SetDescription(AppResources.SettingServerUseRestartWarningsText)
                .SetGetter(() => _profile.UseRestartWarnings)
                .SetSetter((v) => _profile.UseRestartWarnings = v)
                .SetDefault(() => ServerProfile.UseRestartWarningsDefault);
            var warnings = new RestartWarningsField(() => _profile, () => _profile.SaveFile(), () => LocalizedDefaults.RestartWarnings)
                .SetTitle(AppResources.SettingServerRestartWarnings)
                .SetDescription(AppResources.SettingServerRestartWarningsText);
            var earlyRestart = new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerRestartEarlyWhenEmpty)
                .SetDescription(AppResources.SettingServerRestartEarlyWhenEmptyText)
                .SetGetter(() => _profile.RestartEarlyWhenEmpty)
                .SetSetter((v) => _profile.RestartEarlyWhenEmpty = v)
                .SetDefault(() => ServerProfile.RestartEarlyWhenEmptyDefault);
            var messageRestarted = new TextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerMessageRestarted)
                .SetDescription(AppResources.SettingServerMessageRestartedText)
                .SetGetter(() => _profile.MessageRestarted)
                .SetSetter((v) => _profile.MessageRestarted = v)
                .SetDefault(() => LocalizedDefaults.RestartedMessage);
            useWarnings.WhenAnyValue(x => x.Value).Subscribe(x =>
            {
                warnings.IsVisible = x;
                earlyRestart.IsVisible = x;
                messageRestarted.IsVisible = x;
            });
            Fields.Add(useWarnings);
            Fields.Add(warnings);
            Fields.Add(earlyRestart);
            Fields.Add(messageRestarted);
            Fields.Add(new TextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerMessageReady)
                .SetDescription(AppResources.SettingServerMessageReadyText)
                .SetGetter(() => _profile.MessageReady)
                .SetSetter((v) => _profile.MessageReady = v)
                .SetDefault(() => ServerProfile.MessageReadyDefault)
            );
            Fields.Add(new TextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerMessageShutdown)
                .SetDescription(AppResources.SettingServerMessageShutdownText)
                .SetGetter(() => _profile.MessageShutdown)
                .SetSetter((v) => _profile.MessageShutdown = v)
                .SetDefault(() => ServerProfile.MessageShutdownDefault)
            );

            Fields.Add(new TitleField().SetTitle(AppResources.CatBackupAutomation));
            var backup = new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerBackupOnShutdown)
                .SetDescription(AppResources.SettingServerBackupOnShutdownText)
                .SetGetter(() => _profile.BackupOnShutdown)
                .SetSetter((v) => _profile.BackupOnShutdown = v)
                .SetDefault(() => ServerProfile.BackupOnShutdownDefault);
            var backupDirectory = new DirectoryField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerBackupDirectory)
                .SetDescription(AppResources.SettingServerBackupDirectoryText)
                .SetGetter(() => _profile.BackupDirectory)
                .SetSetter((v) => _profile.BackupDirectory = v)
                .SetDefault(() => ServerProfile.BackupDirectoryDefault);
            var backupLimit = new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerBackupLimit)
                .SetDescription(AppResources.SettingServerBackupLimitText)
                .SetGetter(() => _profile.BackupLimitEnabled)
                .SetSetter((v) => _profile.BackupLimitEnabled = v)
                .SetDefault(() => ServerProfile.BackupLimitEnabledDefault);
            var backupDays = new IntField(1, 3650)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerBackupLimitDays)
                .SetDescription(AppResources.SettingServerBackupLimitDaysText)
                .SetGetter(() => _profile.BackupLimitDays)
                .SetSetter((v) => _profile.BackupLimitDays = v)
                .SetDefault(() => ServerProfile.BackupLimitDaysDefault);
            backup.WhenAnyValue(x => x.Value).Subscribe(x =>
            {
                backupDirectory.IsVisible = x;
                backupLimit.IsVisible = x;
                backupDays.IsVisible = x && backupLimit.Value;
            });
            backupLimit.WhenAnyValue(x => x.Value).Subscribe(x => backupDays.IsVisible = x && backup.Value);
            Fields.Add(backup);
            Fields.Add(backupDirectory);
            Fields.Add(backupLimit);
            Fields.Add(backupDays);
            var scriptMode = new ComboBoxField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerScriptMode)
                .SetDescription(AppResources.SettingServerScriptModeText)
                .AddOption(AppResources.ScriptModeNone)
                .AddOption(AppResources.ScriptModeBeforeStart)
                .AddOption(AppResources.ScriptModeShutdownBeforeBackup)
                .AddOption(AppResources.ScriptModeShutdownAfterBackup)
                .SetGetter(() => _profile.ScriptMode)
                .SetSetter((v) => _profile.ScriptMode = v)
                .SetDefault(() => ServerProfile.ScriptModeDefault);
            var scriptPath = new TextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerScriptPath)
                .SetDescription(AppResources.SettingServerScriptPathText)
                .SetGetter(() => _profile.ScriptPath)
                .SetSetter((v) => _profile.ScriptPath = v)
                .SetDefault(() => ServerProfile.ScriptPathDefault);
            var scriptArguments = new TextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerScriptArguments)
                .SetDescription(AppResources.SettingServerScriptArgumentsText)
                .SetGetter(() => _profile.ScriptArguments)
                .SetSetter((v) => _profile.ScriptArguments = v)
                .SetDefault(() => ServerProfile.ScriptArgumentsDefault);
            scriptMode.WhenAnyValue(x => x.Value).Subscribe(x =>
            {
                scriptPath.IsVisible = x != ServerProfile.ScriptModeNone;
                scriptArguments.IsVisible = x != ServerProfile.ScriptModeNone;
            });
            Fields.Add(scriptMode);
            Fields.Add(scriptPath);
            Fields.Add(scriptArguments);
            Fields.Add(new TextField()
                .WhenFieldChanged(SaveConfig)
                .SetTitle(AppResources.SettingScriptBeforeUpdate)
                .SetDescription(AppResources.SettingScriptBeforeUpdateText)
                .SetGetter(() => _setup.Config.ScriptBeforeUpdate)
                .SetSetter((v) => _setup.Config.ScriptBeforeUpdate = v)
                .SetDefault(() => string.Empty)
            );
            Fields.Add(new TextField()
                .WhenFieldChanged(SaveConfig)
                .SetTitle(AppResources.SettingScriptAfterUpdate)
                .SetDescription(AppResources.SettingScriptAfterUpdateText)
                .SetGetter(() => _setup.Config.ScriptAfterUpdate)
                .SetSetter((v) => _setup.Config.ScriptAfterUpdate = v)
                .SetDefault(() => string.Empty)
            );
            
            Fields.Add(new TitleField().SetTitle(AppResources.CatPerformance));
            Fields.Add(new IntField(1, int.MaxValue)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerMaximumTickRate)
                .SetDescription(AppResources.SettingServerMaximumTickRateText)
                .SetGetter(() => _profile.MaximumTickRate)
                .SetSetter((v) => _profile.MaximumTickRate = v)
                .SetDefault(() => ServerProfile.MaximumTickRateDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerUseAllCores)
                .SetDescription(AppResources.SettingServerUseAllCoresText)
                .SetGetter(() => _profile.UseAllCores)
                .SetSetter((v) => _profile.UseAllCores = v)
                .SetDefault(() => ServerProfile.UseAllCoresDefault)
            );
            Fields.Add(new ComboBoxField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerProcessPriority)
                .SetDescription(AppResources.SettingServerProcessPriorityText)
                .AddOption(AppResources.SettingProcessPrioNormal)
                .AddOption(AppResources.SettingProcessPrioAboveNormal)
                .AddOption(AppResources.SettingProcessPrioHigh)
                .AddOption(AppResources.SettingProcessPrioRealtime)
                .AddOption(AppResources.SettingProcessPrioUnchanged)
                .SetGetter(() => _profile.ProcessPriority)
                .SetSetter((v) => _profile.ProcessPriority = v)
                .SetDefault(() => ServerProfile.ProcessPriorityDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerEnforceProcessSettings)
                .SetDescription(AppResources.SettingServerEnforceProcessSettingsText)
                .SetGetter(() => _profile.EnforceProcessSettings)
                .SetSetter((v) => _profile.EnforceProcessSettings = v)
                .SetDefault(() => ServerProfile.EnforceProcessSettingsDefault)
            );
            Fields.Add(new CpuAffinityField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerCPUThreadAffinity)
                .SetDescription(AppResources.SettingServerCPUThreadAffinityText)
                .SetGetter(() => _profile.CPUThreadAffinity)
                .SetSetter((v) => _profile.CPUThreadAffinity = v)
                .SetDefault(() => ServerProfile.CPUThreadAffinityDefault)
            );
            Fields.Add(new TitleField().SetTitle(AppResources.CatPorts));
            var gameClientPort = new IntField(int.MinValue, int.MaxValue)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerGameClientPort)
                .SetDescription(AppResources.SettingServerGameClientPortText)
                .SetGetter(() => _profile.GameClientPort)
                .SetSetter((v) => _profile.GameClientPort = v)
                .SetDefault(() => ServerProfile.GameClientPortDefault);
            Fields.Add(gameClientPort);
            Fields.Add(new IntField(int.MinValue, int.MaxValue)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerRawUDPPort)
                .SetDescription(AppResources.SettingServerRawUDPPortText)
                .SetGetter(() => _profile.GameClientPort+1)
                .SetDefault(() => ServerProfile.GameClientPortDefault+1)
                .SetEnabled(false)
                .UpdateWith(gameClientPort)
            );
            Fields.Add(new IntField(int.MinValue, int.MaxValue)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerSourceQueryPort)
                .SetDescription(AppResources.SettingServerSourceQueryPortText)
                .SetGetter(() => _profile.SourceQueryPort)
                .SetSetter((v) => _profile.SourceQueryPort = v)
                .SetDefault(() => ServerProfile.SourceQueryPortDefault)
            );
            var multiHome = new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerEnableMultiHome)
                .SetDescription(AppResources.SettingServerEnableMultiHomeText)
                .SetGetter(() => _profile.EnableMultiHome)
                .SetSetter((v) => _profile.EnableMultiHome = v)
                .SetDefault(() => ServerProfile.EnableMultiHomeDefault);
            Fields.Add(multiHome);
            var multiHomeAdress = new TextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerMultiHomeAddress)
                .SetDescription(AppResources.SettingServerMultiHomeAddressText)
                .SetGetter(() => _profile.MultiHomeAddress)
                .SetSetter((v) => _profile.MultiHomeAddress = v)
                .SetDefault(() => ServerProfile.MultiHomeAddressDefault);
            Fields.Add(multiHomeAdress);
            var multiHomeHttp = new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerEnableMultiHomeHttp)
                .SetDescription(AppResources.SettingServerEnableMultiHomeHttpText)
                .SetGetter(() => _profile.EnableMultiHomeHttp)
                .SetSetter((v) => _profile.EnableMultiHomeHttp = v)
                .SetDefault(() => ServerProfile.EnableMultiHomeHttpDefault);
            Fields.Add(multiHomeHttp);
            multiHome.WhenAnyValue(x => x.Value).Subscribe(x =>
            {
                multiHomeAdress.IsVisible = x;
                multiHomeHttp.IsVisible = x;
            });
            Fields.Add(new NetworkInfoField(() => _profile, CreateMacroContext, UseAsMultiHome)
                .SetTitle(AppResources.SettingServerNetworkTools)
                .SetDescription(AppResources.SettingServerNetworkToolsText));
            
            Fields.Add(new TitleField().SetTitle(AppResources.CatRCon));
            var rcon = new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerEnableRCon)
                .SetDescription(AppResources.SettingServerEnableRConText)
                .SetGetter(() => _profile.EnableRCon)
                .SetSetter((v) => _profile.EnableRCon = v)
                .SetDefault(() => ServerProfile.EnableRConDefault);
            Fields.Add(rcon);
            var rconPort = new IntField(0, 65535)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerRConPort)
                .SetDescription(AppResources.SettingServerRConPortText)
                .SetGetter(() => _profile.RConPort)
                .SetSetter((v) => _profile.RConPort = v)
                .SetDefault(() => ServerProfile.RConPortDefault);
            var rconPass = new PasswordField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerRConPassword)
                .SetDescription(AppResources.SettingServerRConPasswordText)
                .SetGetter(() => _profile.RConPassword)
                .SetSetter((v) => _profile.RConPassword = v)
                .SetDefault(() => ServerProfile.RConPasswordDefault);
            var rconKarma = new IntField(0, int.MaxValue)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerRConMaxKarma)
                .SetDescription(AppResources.SettingServerRConMaxKarmaText)
                .SetGetter(() => _profile.RConMaxKarma)
                .SetSetter((v) => _profile.RConMaxKarma = v)
                .SetDefault(() => ServerProfile.RConMaxKarmaDefault);
            var rconMethod = new ComboBoxField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerRConBroadcastMethod)
                .SetDescription(AppResources.SettingServerRConBroadcastMethodText)
                .AddOption(@"broadcast")
                .AddOption(@"server (Pippi)")
                .AddOption(@"totpop")
                .SetGetter(() => _profile.RConBroadcastMethod)
                .SetSetter((v) => _profile.RConBroadcastMethod = v)
                .SetDefault(() => ServerProfile.RConBroadcastMethodDefault);
            Fields.Add(rconPort);
            Fields.Add(rconPass);
            Fields.Add(rconKarma);
            var rconTest = new ActionButtonField(AppResources.RConTestSend, @"mdi-send", SendRConTestMessage)
                .SetTitle(AppResources.SettingServerRConTest)
                .SetDescription(AppResources.SettingServerRConTestText);
            Fields.Add(rconMethod);
            Fields.Add(rconTest);
            rcon.WhenAnyValue(x => x.Value)
                .Subscribe(x =>
                {
                    rconPass.IsVisible = x;
                    rconPort.IsVisible = x;
                    rconKarma.IsVisible = x;
                    rconMethod.IsVisible = x;
                    rconTest.IsVisible = x;
                });
                
            Fields.Add(new TitleField().SetTitle(AppResources.CatAntiCheat));
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerEnableVAC)
                .SetDescription(AppResources.SettingServerEnableVACText)
                .SetGetter(() => _profile.EnableVAC)
                .SetSetter((v) => _profile.EnableVAC = v)
                .SetDefault(() => ServerProfile.EnableVACDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerEnableBattleEye)
                .SetDescription(AppResources.SettingServerEnableBattleEyeText)
                .SetGetter(() => _profile.EnableBattleEye)
                .SetSetter((v) => _profile.EnableBattleEye = v)
                .SetDefault(() => ServerProfile.EnableBattleEyeDefault)
            );
            Fields.Add(new TitleField().SetTitle(AppResources.CatDiscord));
            Fields.Add(new MultiLineTextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerDiscordNotificationsWebhook)
                .SetDescription(AppResources.SettingServerDiscordNotificationsWebhookText)
                .SetGetter(() => _profile.DiscordWebHookNotifications)
                .SetSetter((v) => _profile.DiscordWebHookNotifications = v.Trim())
                .SetDefault(() => ServerProfile.DiscordWebHookNotificationsDefault)
                );
            Fields.Add(new TextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerDiscordBotName)
                .SetDescription(AppResources.SettingServerDiscordBotNameText)
                .SetGetter(() => _profile.DiscordBotName)
                .SetSetter((v) => _profile.DiscordBotName = v)
                .SetDefault(() => ServerProfile.DiscordBotNameDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerDiscordTimestamp)
                .SetDescription(AppResources.SettingServerDiscordTimestampText)
                .SetGetter(() => _profile.DiscordTimestamp)
                .SetSetter((v) => _profile.DiscordTimestamp = v)
                .SetDefault(() => ServerProfile.DiscordTimestampDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerNotifyIpChange)
                .SetDescription(AppResources.SettingServerNotifyIpChangeText)
                .SetGetter(() => _profile.NotifyExternalIpChange)
                .SetSetter((v) => _profile.NotifyExternalIpChange = v)
                .SetDefault(() => ServerProfile.NotifyExternalIpChangeDefault)
            );
            Fields.Add(new TitleField().SetTitle(AppResources.CatMiscellaneous));
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerLog)
                .SetDescription(AppResources.SettingServerLogText)
                .SetGetter(() => _profile.Log)
                .SetSetter((v) => _profile.Log = v)
                .SetDefault(() => ServerProfile.LogDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerNoAISpawn)
                .SetDescription(AppResources.SettingServerNoAISpawnText)
                .SetGetter(() => _profile.NoAISpawn)
                .SetSetter((v) => _profile.NoAISpawn = v)
                .SetDefault(() => ServerProfile.NoAISpawnDefault)
            );
            Fields.Add(new MultiLineTextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingServerLogFilters)
                .SetDescription(AppResources.SettingServerLogFiltersText)
                .SetGetter(() => string.Join(Environment.NewLine, _profile.LogFilters))
                .SetSetter((v) => _profile.LogFilters = v.Trim().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList())
                .SetDefault(() => string.Join(Environment.NewLine, ServerProfile.LogFiltersDefault))
            );

            // Notification templates are shared by all servers (moved here from the application settings).
            Fields.Add(new TitleField().SetTitle(AppResources.CatNotificationAllServers));
            var titles = new Dictionary<string, string>
            {
                [nameof(AppResources.DefaultNotificationServerCrash)] = AppResources.SettingNotificationServerCrash,
                [nameof(AppResources.DefaultNotificationServerOnline)] = AppResources.SettingNotificationServerOnline,
                [nameof(AppResources.DefaultNotificationServerStop)] = AppResources.SettingNotificationServerStop,
                [nameof(AppResources.DefaultNotificationServerManualStop)] = AppResources.SettingNotificationServerManualStop,
                [nameof(AppResources.DefaultNotificationServerManualRestart)] = AppResources.SettingNotificationServerManualRestart,
                [nameof(AppResources.DefaultNotificationServerAutomatedRestart)] = AppResources.SettingNotificationServerAutomatedRestart,
                [nameof(AppResources.DefaultNotificationServerModUpdate)] = AppResources.SettingNotificationServerModUpdate,
                [nameof(AppResources.DefaultNotificationServerServerUpdate)] = AppResources.SettingNotificationServerServerUpdate,
                [nameof(AppResources.DefaultNotificationExternalIpChanged)] = AppResources.SettingNotificationExternalIpChanged,
                [nameof(AppResources.DefaultNotificationCrashLoop)] = AppResources.SettingNotificationCrashLoop,
                [nameof(AppResources.DefaultNotificationModUpdateDetected)] = AppResources.SettingNotificationModUpdateDetected,
                [nameof(AppResources.DefaultNotificationTimedRestart)] = AppResources.SettingNotificationTimedRestart,
                [nameof(AppResources.DefaultNotificationTimedStop)] = AppResources.SettingNotificationTimedStop,
            };
            foreach (var (key, libraryDefault, get, set) in LocalizedDefaults.ConfigTemplates)
            {
                Fields.Add(new TextField()
                    .WhenFieldChanged(SaveConfig)
                    .SetTitle(titles.GetValueOrDefault(key, key))
                    .SetGetter(() => get(_setup.Config))
                    .SetSetter(v => set(_setup.Config, v))
                    .SetDefault(() => LocalizedDefaults.Get(key, libraryDefault))
                );
            }
        }
    }
}
