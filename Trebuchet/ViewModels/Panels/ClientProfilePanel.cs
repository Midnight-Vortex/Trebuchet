using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Humanizer;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using AppResources = Trebuchet.Assets.Resources;
using Trebuchet.ViewModels.InnerContainer;
using Trebuchet.ViewModels.SettingFields;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels.Panels
{
    public class ClientProfilePanel : ReactiveObject, IRefreshablePanel
    {
        public ClientProfilePanel(
            DialogueBox box,
            AppSetup setup, 
            AppFiles appFiles, 
            ILogger<ClientProfilePanel> logger,
            ClientConnectionListViewModel clientConnectionList,
            UIConfig uiConfig)
        {
            _setup = setup;
            _appFiles = appFiles;
            _logger = logger;
            _uiConfig = uiConfig;
            CanBeOpened = Tools.IsClientInstallValid(_setup.Config, _setup.Edition) && _setup.Config.ManageClient;

            ClientConnectionList = clientConnectionList;
            ClientConnectionList.ConnectionListChanged += OnConnectionListChanged;

            var startingProfile = _appFiles.Client.Resolve(_uiConfig.CurrentClientProfile);
            _profile = _appFiles.Client.Get(startingProfile);
            
            // SaveProfile must exist before FileMenu.Selected — selection fires FileSelected → RefreshPanel → EnsureFields.
            SaveProfile = ReactiveCommand.Create(() => _profile.SaveFile());

            FileMenu = new FileMenuViewModel<ClientProfile, ClientProfileRef>(AppResources.PanelGameSaves, appFiles.Client, box, _logger);
            FileMenu.FileSelected += OnFileSelected;
            FileMenu.Selected = startingProfile;
        }

        private readonly AppSetup _setup;
        private bool _fieldsBuilt;
        private readonly AppFiles _appFiles;
        private readonly ILogger<ClientProfilePanel> _logger;
        private readonly UIConfig _uiConfig;
        private ClientProfile _profile;
        private string _profileSize = string.Empty;
        private bool _canBeOpened;

        public string Icon => @"mdi-controller";
        public string Label => AppResources.PanelGameSaves;
        public ObservableCollection<FieldElement> Fields { get; } = [];

        public FileMenuViewModel<ClientProfile, ClientProfileRef> FileMenu { get; }

        public ClientConnectionListViewModel ClientConnectionList { get; }
       
        private ReactiveCommand<Unit, Unit> SaveProfile { get; }
        public string ProfileSize
        {
            get => _profileSize;
            protected set => this.RaiseAndSetIfChanged(ref _profileSize, value);
        }

        public bool CanBeOpened
        {
            get => _canBeOpened;
            set => this.RaiseAndSetIfChanged(ref _canBeOpened, value);
        }

        public async Task RefreshPanel()
        {
            EnsureFields();
            _logger.LogDebug(@"Refresh panel");
            CanBeOpened = Tools.IsClientInstallValid(_setup.Config, _setup.Edition) && _setup.Config.ManageClient;
            _profile = _appFiles.Client.Get(FileMenu.Selected);
            foreach (var f in Fields.OfType<IValueField>())
                f.Update.Execute().Subscribe();
            await RefreshProfileSize(FileMenu.Selected);
            ClientConnectionList.SetList(_profile.ClientConnections);
        }
        
        private Task OnConnectionListChanged(object? sender, EventArgs args)
        {
            _profile.ClientConnections = ClientConnectionList.List.Select(x => x.Export()).ToList();
            _profile.SaveFile();
            return Task.CompletedTask;
        }
        
        private Task OnFileSelected(object? sender, ClientProfileRef profile)
        {
            _uiConfig.CurrentClientProfile = profile.Uri.OriginalString;
            _uiConfig.SaveFile();
            return RefreshPanel();
        }

        private async Task RefreshProfileSize(ClientProfileRef profile)
        {
            var path = _appFiles.Client.GetDirectory(profile);
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
            Fields.Add(new TitleField().SetTitle(AppResources.CatGeneral));
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingBackgroundSound)
                .SetDescription(AppResources.SettingBackgroundSoundText)
                .SetSetter((v) => _profile.BackgroundSound = v)
                .SetGetter(() => _profile.BackgroundSound)
                .SetDefault(() => ClientProfile.BackgroundSoundDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingIntroVid)
                .SetDescription(AppResources.SettingIntroVidText)
                .SetGetter(() => _profile.RemoveIntroVideo)
                .SetSetter((v) => _profile.RemoveIntroVideo = v)
                .SetDefault(() => ClientProfile.RemoveIntroVideoDefault)
            );
            Fields.Add(new TitleField().SetTitle(AppResources.CatProcessPerformance));
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingUseAllCore)
                .SetDescription(AppResources.SettingUseAllCoreText)
                .SetGetter(() => _profile.UseAllCores)
                .SetSetter((v) => _profile.UseAllCores = v)
                .SetDefault(() => ClientProfile.UseAllCoresDefault)
            );
            Fields.Add(new ComboBoxField()
                .WhenFieldChanged(SaveProfile)
                .SetDescription(AppResources.SettingProcessPrioText)
                .SetTitle(AppResources.SettingProcessPrio)
                .AddOption(AppResources.SettingProcessPrioNormal)
                .AddOption(AppResources.SettingProcessPrioAboveNormal)
                .AddOption(AppResources.SettingProcessPrioHigh)
                .AddOption(AppResources.SettingProcessPrioRealtime)
                .SetGetter(() => _profile.ProcessPriority)
                .SetSetter((v) => _profile.ProcessPriority = v)
                .SetDefault(() => ClientProfile.ProcessPriorityDefault)
            );
            Fields.Add(new CpuAffinityField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingCpuAffinity)
                .SetDescription(AppResources.SettingCpuAffinityText)
                .SetSetter((v) => _profile.CPUThreadAffinity = v)
                .SetGetter(() => _profile.CPUThreadAffinity)
                .SetDefault(CpuAffinityField.DefaultValue)
            );
            Fields.Add(new TitleField().SetTitle(AppResources.CatGraphics));
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingUltraAniso)
                .SetDescription(AppResources.SettingUltraAnisoText)
                .SetGetter(() => _profile.UltraAnisotropy)
                .SetSetter((v) => _profile.UltraAnisotropy = v)
                .SetDefault(() => ClientProfile.UltraAnisotropyDefault));
            Fields.Add(new IntSliderField(0, 4000, 100)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingTexPool)
                .SetDescription(AppResources.SettingTexPoolText)
                .SetGetter(() => _profile.AddedTexturePool)
                .SetSetter((v) => _profile.AddedTexturePool = v)
                .SetDefault(() => ClientProfile.AddedTexturePoolDefault)
            );
            Fields.Add(new TitleField().SetTitle(AppResources.CatMiscellaneous));
            if(_setup.Experiment)
                Fields.Add(new ToggleField()
                    .WhenFieldChanged(SaveProfile)
                    .SetExperiment()
                    .SetTitle(AppResources.SettingAsyncScene)
                    .SetDescription(AppResources.SettingAsyncSceneText)
                    .SetGetter(() => _profile.EnableAsyncScene)
                    .SetSetter((v) => _profile.EnableAsyncScene = v)
                    .SetDefault(() => ClientProfile.EnableAsyncSceneDefault)
                );
            if(_setup.Experiment)
                Fields.Add(new IntSliderField(10000, 100000, 1000)
                    .WhenFieldChanged(SaveProfile)
                    .SetExperiment()
                    .SetTitle(AppResources.SettingInternetSpeed)
                    .SetDescription(AppResources.SettingInternetSpeedText)
                    .SetGetter(() => _profile.ConfiguredInternetSpeed)
                    .SetSetter((v) => _profile.ConfiguredInternetSpeed = v)
                    .SetDefault(() => ClientProfile.ConfiguredInternetSpeedDefault)
                );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingLog)
                .SetDescription(AppResources.SettingLogText)
                .SetGetter(() => _profile.Log)
                .SetSetter((v) => _profile.Log = v)
                .SetDefault(() => ClientProfile.LogDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingAdminServerList)
                .SetDescription(AppResources.SettingAdminServerListText)
                .SetGetter(() => _profile.TotAdminDoNotLoadServerList)
                .SetSetter((v) => _profile.TotAdminDoNotLoadServerList = v)
                .SetDefault(() => ClientProfile.TotAdminDoNotLoadServerListDefault)
            );
            Fields.Add(new MultiLineTextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(AppResources.SettingLogFilter)
                .SetDescription(AppResources.SettingLogFilterText)
                .SetGetter(() => string.Join(Environment.NewLine, _profile.LogFilters))
                .SetSetter((v) => _profile.LogFilters = v.Trim().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList())
                .SetDefault(() => string.Join(Environment.NewLine, ClientProfile.LogFiltersDefault))
            );
        }


    }
}