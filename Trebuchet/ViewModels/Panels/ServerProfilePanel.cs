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
            ILogger<ServerProfilePanel> logger
            )
        {
            _setup = setup;
            _appFiles = appFiles;
            _uiConfig = uiConfig;
            _logger = logger;
            CanBeOpened = Tools.IsServerInstallValid(_setup.Config);

            var startingProfile = _appFiles.Server.Resolve(_uiConfig.CurrentServerProfile);
            _profile = _appFiles.Server.Get(startingProfile);
            SaveProfile = ReactiveCommand.Create(() => _profile.SaveFile());

            FileMenu = new FileMenuViewModel<ServerProfile, ServerProfileRef>(AppResources.PanelServerSaves, appFiles.Server, box, _logger);
            FileMenu.FileSelected += OnFileSelected;
            FileMenu.Selected = startingProfile;
        }

        private bool _fieldsBuilt;

        public ObservableCollection<FieldElement> Fields { get; } = [];

        public ReactiveCommand<Unit,Unit> SaveProfile { get; }
        
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
            _profile = _appFiles.Server.Get(FileMenu.Selected);
            await RefreshProfileSize(FileMenu.Selected);
            foreach (var f in Fields.OfType<IRefreshableField>())
                f.Update.Execute().Subscribe();
        }

        public async Task DisplayPanel()
        {
            EnsureFields();
            _logger.LogDebug(@"Display panel");
            await RefreshProfileSize(FileMenu.Selected);
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

        private Task OnFileSelected(object? sender, ServerProfileRef profile)
        {
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
                .SetGetter(() => _profile.ProcessPriority)
                .SetSetter((v) => _profile.ProcessPriority = v)
                .SetDefault(() => ServerProfile.ProcessPriorityDefault)
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
            multiHome.WhenAnyValue(x => x.Value).Subscribe(x => multiHomeAdress.IsVisible = x);
            
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
            Fields.Add(rconPort);
            Fields.Add(rconPass);
            Fields.Add(rconKarma);
            rcon.WhenAnyValue(x => x.Value)
                .Subscribe(x =>
                {
                    rconPass.IsVisible = x;
                    rconPort.IsVisible = x;
                    rconKarma.IsVisible = x;
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
            Fields.Add(new TitleField().SetTitle(AppResources.CatMiscellaneous));
            Fields.Add(new TextField()
                .SetTitle(AppResources.SettingServerDiscordNotificationsWebhook)
                .SetDescription(AppResources.SettingServerDiscordNotificationsWebhookText)
                .SetGetter(() => _profile.DiscordWebHookNotifications)
                .SetSetter((v) => _profile.DiscordWebHookNotifications = v)
                .SetDefault(() => ServerProfile.DiscordWebHookNotificationsDefault)
                );
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
        }
    }
}