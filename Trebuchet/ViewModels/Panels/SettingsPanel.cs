using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using DynamicData.Binding;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using tot_lib;
using tot_lib.OsSpecific;
using Trebuchet.Assets;
using Trebuchet.Services;
using Trebuchet.Services.Language;
using Trebuchet.Utils;
using Trebuchet.ViewModels.InnerContainer;
using Trebuchet.ViewModels.SettingFields;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels.Panels;

public class SettingsPanel : ReactiveObject, IRefreshablePanel, IRefreshingPanel, IBottomPanel, IStartingPanel
{


    public SettingsPanel(
        IOsPlatformSpecific osPlatformSpecific,
        AppSetup setup, 
        Operations operations,
        ILanguageManager langManager,
        DialogueBox box,
        ILogger<SettingsPanel> logger,
        TaskBlocker blocker,
        UIConfig uiConfig)
    {
        _osPlatformSpecific = osPlatformSpecific;
        _setup = setup;
        _uiConfig = uiConfig;
        _operations = operations;
        _langManager = langManager;
        _box = box;
        _logger = logger;
        _blocker = blocker;

        AvailableLocales.AddRange(langManager.AllLanguages);
        _selectedLanguage = langManager.AllLanguages.Where(x => x.Code == _uiConfig.UICulture)
            .FirstOrDefault(langManager.DefaultLanguage);

        SaveConfig = ReactiveCommand.Create(() => _setup.Config.SaveFile());
        SaveUiConfig = ReactiveCommand.Create(() => _uiConfig.SaveFile());
        ChangeLanguage = ReactiveCommand.CreateFromTask<LanguageModel?>(OnLanguageChanged);
        ChangePlateformTheme = ReactiveCommand.Create(OnPlateformThemeChanged);
        RestartProcess = ReactiveCommand.CreateFromTask(OnRestartProcess);
        
        this.WhenValueChanged<SettingsPanel, LanguageModel>(x => x.SelectedLanguage, false, () => _langManager.DefaultLanguage)
            .InvokeCommand(ChangeLanguage);

        _foldedMenu = uiConfig.FoldedMenu;
        ToggleFoldedMenu = ReactiveCommand.CreateFromTask(OnToggleFoldedMenu);
    }

    private readonly IOsPlatformSpecific _osPlatformSpecific;
    private bool _fieldsBuilt;
    private readonly AppSetup _setup;
    private readonly UIConfig _uiConfig;
    private readonly Operations _operations;
    private readonly ILanguageManager _langManager;
    private readonly DialogueBox _box;
    private readonly ILogger<SettingsPanel> _logger;
    private readonly TaskBlocker _blocker;
    private LanguageModel _selectedLanguage;
    private bool _canBeOpened = true;
    private bool _foldedMenu;

    public ReactiveCommand<Unit,Unit> SaveConfig { get; }
    public ReactiveCommand<Unit,Unit> SaveUiConfig { get; }
    public ReactiveCommand<Unit, Unit> ChangePlateformTheme { get; }
    public ReactiveCommand<LanguageModel?,Unit> ChangeLanguage { get; }
    public ReactiveCommand<Unit,Unit> RestartProcess { get; }
    public ReactiveCommand<Unit,Unit> ToggleFoldedMenu { get; }
    public List<FieldElement> Fields { get; } = [];
    public ObservableCollectionExtended<LanguageModel> AvailableLocales { get; } = [];

    public bool FoldedMenu
    {
        get => _foldedMenu;
        set => this.RaiseAndSetIfChanged(ref _foldedMenu, value);
    }

    public LanguageModel SelectedLanguage
    {
        get => _selectedLanguage;
        set => this.RaiseAndSetIfChanged(ref _selectedLanguage, value);
    }

    public string Icon => @"mdi-cog";
    public string Label => Resources.PanelSettings;

    public bool CanBeOpened
    {
        get => _canBeOpened;
        set => this.RaiseAndSetIfChanged(ref _canBeOpened, value);
    }

    public event AsyncEventHandler? RequestRefresh;

    public Task RefreshPanel()
    {
        EnsureFields();
        foreach (var f in Fields.OfType<ClientInstallationField>())
            f.SyncFromConfig();
        foreach (var f in Fields.OfType<ServerInstallationField>())
            f.SyncFromConfig();
        return Task.CompletedTask;
    }
    
    public async Task<bool> StartPanel()
    {
        if (!string.IsNullOrEmpty(_uiConfig.UICulture)) return true;
        var choice = new OnBoardingLanguage(Resources.OnBoardingLanguageChange, string.Empty, 
            _langManager.AllLanguages.ToList(), _langManager.DefaultLanguage);
        await _box.OpenAsync(choice);
        if(choice.Value is null) throw new OperationCanceledException(@"OnBoarding was cancelled");
        using(_logger.BeginScope((@"Language", choice.Value.Code)))
            _logger.LogInformation(@"Changing language");
        _uiConfig.UICulture = choice.Value.Code;
        _uiConfig.SaveFile();
        App.RestartProcess();
        return false;
    }

    private async Task OnLanguageChanged(LanguageModel? model)
    {
        if (model is null) return;
        _uiConfig.UICulture = model.Code;
        _langManager.SetLanguage(model.Code);
        _uiConfig.SaveFile();
        if (_blocker.CanLaunch)
        {
            var message = new OnBoardingMessage(Resources.OnBoardingLanguageChange, Resources.OnBoardingLanguageChangeMessage);
            await _box.OpenAsync(message);
        }
        else
        {
            var confirm = new OnBoardingConfirmation(Resources.OnBoardingLanguageChange,
                Resources.OnBoardingLanguageChangeConfirm);
            await _box.OpenAsync(confirm);
            if(confirm.Result)
                App.RestartProcess();
        }
    }

    private async Task OnToggleFoldedMenu()
    {
        _uiConfig.FoldedMenu = !_uiConfig.FoldedMenu;
        _uiConfig.SaveFile();

        FoldedMenu = _uiConfig.FoldedMenu;
        await OnRequestRefresh();
    }

    private async Task OnRestartProcess()
    {
        if (_blocker.CanLaunch)
        {
            var message = new OnBoardingMessage(Resources.OnBoardingRestartProcess, Resources.OnBoardingRestartProcessSubMessage);
            await _box.OpenAsync(message);
        }
        else
        {
            var confirm = new OnBoardingConfirmation(Resources.OnBoardingRestartProcess,
                Resources.OnBoardingRestartProcessSub);
            await _box.OpenAsync(confirm);
            if(confirm.Result)
                App.RestartProcess();
        }
    }

    private void OnPlateformThemeChanged()
    {
        Utils.Utils.ApplyPlateformTheme((PlateformTheme)_uiConfig.PlateformTheme);
    }

    private void EnsureFields()
    {
        if (_fieldsBuilt) return;
        _fieldsBuilt = true;
        BuildFields();
    }

    private void BuildFields()
    {
        Fields.Add(new TitleField().SetTitle(Resources.OnBoardingUsageChoice));
        Fields.Add(new ClientInstallationField(_operations, _setup)
            .WhenFieldChanged(ReactiveCommand.CreateFromTask(OnRequestRefresh))
            .SetTitle(Resources.SettingClientInstallation)
            .SetDescription(Resources.SettingClientInstallationText)
        );
        Fields.Add(new ServerInstallationField(_operations, _setup)
            .WhenFieldChanged(ReactiveCommand.CreateFromTask(OnRequestRefresh))
            .SetTitle(Resources.SettingServerInstanceCount)
            .SetDescription(Resources.SettingServerInstanceCountText)
        );
        Fields.Add(new AppDataDirectoryField(_operations, _setup)
            .SetTitle(Resources.OnBoardingDataDirectory)
            .SetDescription(Resources.OnBoardingDataDirectorySub)
        );
        Fields.Add(new TitleField().SetTitle(Resources.CatMiscellaneous));

        var appName = AppConstants.GetAutoStartName(_setup.Edition);
        var content = Utils.Utils.GetAutoStartValue(_setup.Edition);
        if(content is not null)
            Fields.Add(new ToggleField()
                .SetTitle(Resources.SettingRunOnLogon)
                .SetDescription(Resources.SettingRunOnLogonText)
                .SetGetter(() => _osPlatformSpecific.HasLogonRun(appName))
                .SetSetter((v) =>
                    {
                        if (v)
                            _osPlatformSpecific.SetLogonRun(appName, content);
                        else
                            _osPlatformSpecific.RemoveLogonRun(appName);
                    })
                .SetDefault(() => false)
            );
        Fields.Add(new ToggleField()
            .WhenFieldChanged(SaveUiConfig)
            .SetTitle(Resources.SettingDisplayWarningOnKill)
            .SetDescription(Resources.SettingDisplayWarningOnKillText)
            .SetGetter(() => _uiConfig.DisplayWarningOnKill)
            .SetSetter((v) => _uiConfig.DisplayWarningOnKill = v)
            .SetDefault(() => UIConfig.DisplayWarningOnKillDefault)
        );
        Fields.Add(new ComboBoxField()
            .WhenFieldChanged(ChangePlateformTheme)
            .WhenFieldChanged(SaveUiConfig)
            .SetTitle(Resources.SettingPlateformTheme)
            .SetDescription(Resources.SettingPlateformThemeText)
            .AddOption(Resources.SettingPlateformThemeDefault)
            .AddOption(Resources.SettingPlateformThemeDark)
            .AddOption(Resources.SettingPlateformThemeLight)
            .SetGetter(() => _uiConfig.PlateformTheme)
            .SetSetter((v) => _uiConfig.PlateformTheme = v)
            .SetDefault(() => UIConfig.PlatformThemeDefault)
        );
        Fields.Add(new ToggleField()
            .WhenFieldChanged(SaveUiConfig)
            .WhenFieldChanged(RestartProcess)
            .SetTitle(Resources.SettingExperiments)
            .SetDescription(Resources.SettingExperimentsText)
            .SetGetter(() => _uiConfig.Experiments)
            .SetSetter((v) => _uiConfig.Experiments = v)
            .SetDefault(() => UIConfig.ExperimentsDefault)
            );
        Fields.Add(new ToggleField()
            .WhenFieldChanged(SaveUiConfig)
            .WhenFieldChanged(RestartProcess)
            .SetTitle(Resources.SettingDebugMode)
            .SetDescription(Resources.SettingDebugModeText)
            .SetGetter(() => _uiConfig.DebugMode)
            .SetSetter((v) => _uiConfig.DebugMode = v)
            .SetDefault(() => UIConfig.DebugModeDefault)
            );
        Fields.Add(new TitleField().SetTitle(Resources.CatUpdates));
        Fields.Add(new ComboBoxField()
            .WhenFieldChanged(SaveConfig)
            .SetTitle(Resources.SettingAutoUpdateStatus)
            .SetDescription(Resources.SettingAutoUpdateStatusText)
            .AddOption(Resources.SettingAutoUpdateStatusNever)
            .AddOption(Resources.SettingAutoUpdateStatusLaunchOnly)
            .AddOption(Resources.SettingAutoUpdateStatusCheck)
            .SetGetter(() => _setup.Config.AutoUpdateStatus)
            .SetSetter((v) => _setup.Config.AutoUpdateStatus = v)
            .SetDefault(() => Config.AutoUpdateStatusDefault)
        );
        Fields.Add(new DurationField(TimeSpan.FromMinutes(5), TimeSpan.MaxValue)
            .WhenFieldChanged(SaveConfig)
            .SetTitle(Resources.SettingUpdateCheckFrequency)
            .SetGetter(() => _setup.Config.UpdateCheckFrequency)
            .SetSetter((v) => _setup.Config.UpdateCheckFrequency = v)
            .SetDefault(() => Config.UpdateCheckFrequencyDefault)
        );
        Fields.Add(new ToggleField()
            .WhenFieldChanged(SaveConfig)
            .SetTitle(Resources.SettingVerifyAll)
            .SetDescription(Resources.SettingVerifyAllText)
            .SetGetter(() => _setup.Config.VerifyAll)
            .SetSetter((v) => _setup.Config.VerifyAll = v)
            .SetDefault(() => Config.VerifyAllDefault)
        );
        Fields.Add(new IntSliderField(1, 16, 1)
            .WhenFieldChanged(SaveConfig)
            .SetTitle(Resources.SettingMaxDownloads)
            .SetDescription(Resources.SettingMaxDownloadsText)
            .SetGetter(() => _setup.Config.MaxDownloads)
            .SetSetter((v) => _setup.Config.MaxDownloads = v)
            .SetDefault(() => Config.MaxDownloadsDefault)
        );
        
        Fields.Add(new TitleField().SetTitle(Resources.CatNotification));
        Fields.Add(new TextField()
            .WhenFieldChanged(SaveConfig)
            .SetTitle(Resources.SettingNotificationServerCrash)
            .SetGetter(() => _setup.Config.NotificationServerCrash)
            .SetSetter((v) => _setup.Config.NotificationServerCrash = v)
            .SetDefault(() => Config.NotificationServerCrashDefault)
            );
        Fields.Add(new TextField()
            .WhenFieldChanged(SaveConfig)
            .SetTitle(Resources.SettingNotificationServerOnline)
            .SetGetter(() => _setup.Config.NotificationServerOnline)
            .SetSetter((v) => _setup.Config.NotificationServerOnline = v)
            .SetDefault(() => Config.NotificationServerOnlineDefault)
        );
        Fields.Add(new TextField()
            .WhenFieldChanged(SaveConfig)
            .SetTitle(Resources.SettingNotificationServerStop)
            .SetGetter(() => _setup.Config.NotificationServerStop)
            .SetSetter((v) => _setup.Config.NotificationServerStop = v)
            .SetDefault(() => Config.NotificationServerStopDefault)
        );
        Fields.Add(new TextField()
            .WhenFieldChanged(SaveConfig)
            .SetTitle(Resources.SettingNotificationServerManualStop)
            .SetGetter(() => _setup.Config.NotificationServerManualStop)
            .SetSetter((v) => _setup.Config.NotificationServerManualStop = v)
            .SetDefault(() => Config.NotificationServerManualStopDefault)
        );
        Fields.Add(new TextField()
            .WhenFieldChanged(SaveConfig)
            .SetTitle(Resources.SettingNotificationServerAutomatedRestart)
            .SetGetter(() => _setup.Config.NotificationServerAutomatedRestart)
            .SetSetter((v) => _setup.Config.NotificationServerAutomatedRestart = v)
            .SetDefault(() => Config.NotificationServerAutomatedRestartDefault)
        );
        Fields.Add(new TextField()
            .WhenFieldChanged(SaveConfig)
            .SetTitle(Resources.SettingNotificationServerModUpdate)
            .SetGetter(() => _setup.Config.NotificationServerModUpdate)
            .SetSetter((v) => _setup.Config.NotificationServerModUpdate = v)
            .SetDefault(() => Config.NotificationServerModUpdateDefault)
        );
        Fields.Add(new TextField()
            .WhenFieldChanged(SaveConfig)
            .SetTitle(Resources.SettingNotificationServerServerUpdate)
            .SetGetter(() => _setup.Config.NotificationServerServerUpdate)
            .SetSetter((v) => _setup.Config.NotificationServerServerUpdate = v)
            .SetDefault(() => Config.NotificationServerServerUpdateDefault)
        );
    }

    private async Task OnRequestRefresh()
    {
        if (RequestRefresh is not null)
            await RequestRefresh.Invoke(this, EventArgs.Empty);
    }
}