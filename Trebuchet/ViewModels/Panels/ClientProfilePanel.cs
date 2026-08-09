using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Humanizer;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Trebuchet.Assets;
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
            CanBeOpened = Tools.IsClientInstallValid(_setup) && _setup.Config.ManageClient;

            ClientConnectionList = clientConnectionList;
            ClientConnectionList.ConnectionListChanged += OnConnectionListChanged;

            var startingProfile = _appFiles.Client.Resolve(_uiConfig.CurrentClientProfile);
            _profile = _appFiles.Client.Get(startingProfile);
            
            FileMenu = new FileMenuViewModel<ClientProfile, ClientProfileRef>(Resources.PanelGameSaves, appFiles.Client, box, _logger);
            FileMenu.FileSelected += OnFileSelected;
            FileMenu.Selected = startingProfile;

            SaveProfile = ReactiveCommand.Create(() => _profile.SaveFile());
            
            BuildFields();
        }

        private readonly AppSetup _setup;
        private readonly AppFiles _appFiles;
        private readonly ILogger<ClientProfilePanel> _logger;
        private readonly UIConfig _uiConfig;
        private ClientProfile _profile;
        private string _profileSize = string.Empty;
        private bool _canBeOpened;

        public string Icon => @"mdi-controller";
        public string Label => Resources.PanelGameSaves;
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
            _logger.LogDebug(@"Refresh panel");
            CanBeOpened = Tools.IsClientInstallValid(_setup) && _setup.Config.ManageClient;
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

        private void BuildFields()
        {
            Fields.Add(new TitleField().SetTitle(Resources.CatGeneral));
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(Resources.SettingBackgroundSound)
                .SetDescription(Resources.SettingBackgroundSoundText)
                .SetSetter((v) => _profile.BackgroundSound = v)
                .SetGetter(() => _profile.BackgroundSound)
                .SetDefault(() => ClientProfile.BackgroundSoundDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(Resources.SettingIntroVid)
                .SetDescription(Resources.SettingIntroVidText)
                .SetGetter(() => _profile.RemoveIntroVideo)
                .SetSetter((v) => _profile.RemoveIntroVideo = v)
                .SetDefault(() => ClientProfile.RemoveIntroVideoDefault)
            );
            Fields.Add(new TitleField().SetTitle(Resources.CatProcessPerformance));
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(Resources.SettingUseAllCore)
                .SetDescription(Resources.SettingUseAllCoreText)
                .SetGetter(() => _profile.UseAllCores)
                .SetSetter((v) => _profile.UseAllCores = v)
                .SetDefault(() => ClientProfile.UseAllCoresDefault)
            );
            Fields.Add(new ComboBoxField()
                .WhenFieldChanged(SaveProfile)
                .SetDescription(Resources.SettingProcessPrioText)
                .SetTitle(Resources.SettingProcessPrio)
                .AddOption(Resources.SettingProcessPrioNormal)
                .AddOption(Resources.SettingProcessPrioAboveNormal)
                .AddOption(Resources.SettingProcessPrioHigh)
                .AddOption(Resources.SettingProcessPrioRealtime)
                .SetGetter(() => _profile.ProcessPriority)
                .SetSetter((v) => _profile.ProcessPriority = v)
                .SetDefault(() => ClientProfile.ProcessPriorityDefault)
            );
            Fields.Add(new CpuAffinityField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(Resources.SettingCpuAffinity)
                .SetDescription(Resources.SettingCpuAffinityText)
                .SetSetter((v) => _profile.CPUThreadAffinity = v)
                .SetGetter(() => _profile.CPUThreadAffinity)
                .SetDefault(CpuAffinityField.DefaultValue)
            );
            Fields.Add(new TitleField().SetTitle(Resources.CatGraphics));
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(Resources.SettingUltraAniso)
                .SetDescription(Resources.SettingUltraAnisoText)
                .SetGetter(() => _profile.UltraAnisotropy)
                .SetSetter((v) => _profile.UltraAnisotropy = v)
                .SetDefault(() => ClientProfile.UltraAnisotropyDefault));
            Fields.Add(new IntSliderField(0, 4000, 100)
                .WhenFieldChanged(SaveProfile)
                .SetTitle(Resources.SettingTexPool)
                .SetDescription(Resources.SettingTexPoolText)
                .SetGetter(() => _profile.AddedTexturePool)
                .SetSetter((v) => _profile.AddedTexturePool = v)
                .SetDefault(() => ClientProfile.AddedTexturePoolDefault)
            );
            Fields.Add(new TitleField().SetTitle(Resources.CatMiscellaneous));
            if(_setup.Experiment)
                Fields.Add(new ToggleField()
                    .WhenFieldChanged(SaveProfile)
                    .SetExperiment()
                    .SetTitle(Resources.SettingAsyncScene)
                    .SetDescription(Resources.SettingAsyncSceneText)
                    .SetGetter(() => _profile.EnableAsyncScene)
                    .SetSetter((v) => _profile.EnableAsyncScene = v)
                    .SetDefault(() => ClientProfile.EnableAsyncSceneDefault)
                );
            if(_setup.Experiment)
                Fields.Add(new IntSliderField(10000, 100000, 1000)
                    .WhenFieldChanged(SaveProfile)
                    .SetExperiment()
                    .SetTitle(Resources.SettingInternetSpeed)
                    .SetDescription(Resources.SettingInternetSpeedText)
                    .SetGetter(() => _profile.ConfiguredInternetSpeed)
                    .SetSetter((v) => _profile.ConfiguredInternetSpeed = v)
                    .SetDefault(() => ClientProfile.ConfiguredInternetSpeedDefault)
                );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(Resources.SettingLog)
                .SetDescription(Resources.SettingLogText)
                .SetGetter(() => _profile.Log)
                .SetSetter((v) => _profile.Log = v)
                .SetDefault(() => ClientProfile.LogDefault)
            );
            Fields.Add(new ToggleField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(Resources.SettingAdminServerList)
                .SetDescription(Resources.SettingAdminServerListText)
                .SetGetter(() => _profile.TotAdminDoNotLoadServerList)
                .SetSetter((v) => _profile.TotAdminDoNotLoadServerList = v)
                .SetDefault(() => ClientProfile.TotAdminDoNotLoadServerListDefault)
            );
            Fields.Add(new MultiLineTextField()
                .WhenFieldChanged(SaveProfile)
                .SetTitle(Resources.SettingLogFilter)
                .SetDescription(Resources.SettingLogFilterText)
                .SetGetter(() => string.Join(Environment.NewLine, _profile.LogFilters))
                .SetSetter((v) => _profile.LogFilters = v.Trim().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList())
                .SetDefault(() => string.Join(Environment.NewLine, ClientProfile.LogFiltersDefault))
            );
        }


    }
}