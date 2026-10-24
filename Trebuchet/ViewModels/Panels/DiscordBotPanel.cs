using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Humanizer;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Trebuchet.Assets;
using Trebuchet.Services;
using Trebuchet.ViewModels.DiscordBot;
using Trebuchet.ViewModels.InnerContainer;
using Trebuchet.ViewModels.SettingFields;
using Trebuchet.Windows;
using TrebuchetLib.DiscordBot;
using TrebuchetLib.Services;
using Resources = Trebuchet.Assets.Resources;

namespace Trebuchet.ViewModels.Panels;

/// <summary>
/// The "Discord Bot" menu entry: start/stop, security settings, token, commands, audit log and setup help.
/// Edits go to a copy (<see cref="DiscordBotSettingsEditor"/>) and reach the settings only through "Save", which
/// writes nothing that is not valid. The token is write-only (<see cref="SecretField"/>). Nothing here changes
/// permissions through Discord, and nothing here ever shows, logs or keeps the token.
/// </summary>
public sealed class DiscordBotPanel : ReactiveObject, IBottomPanel, ITickingPanel, IRefreshablePanel, IDisplablePanel, IStartingPanel
{
    public const string SetupGuideUrl = @"https://github.com/Midnight-Vortex/Trebuchet/blob/master/docs/INSTALLME.md";
    private const string Dash = @"-";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private readonly DiscordBotHost _host;
    private readonly AppSetup _setup;
    private readonly DiscordBotSettingsFile _settings;
    private readonly BotAuditLog _audit;
    private readonly DialogueBox _box;
    private readonly ILogger<DiscordBotPanel> _logger;
    private readonly DiscordBotSettingsEditor _editor = new();
    private readonly List<IRefreshableField> _fields = [];
    private IDiscordBotService? _subscribed;
    private bool _showIssues;
    private bool _statusErrorLogged;

    private bool _isDirty;
    private string _stateText = string.Empty;
    private bool _isConnected;
    private bool _isTransitioning;
    private bool _isFailed;
    private string _problemText = string.Empty;
    private string _botName = Dash;
    private string _guildName = Dash;
    private string _latencyText = Dash;
    private string _connectedSinceText = Dash;
    private bool _canStart;
    private bool _canStop;
    private bool _canReconnect;
    private bool _hasServiceError;
    private bool _autoStartSkipped;
    private string _superAdminText = string.Empty;
    private bool _isEditingSuperAdmin;
    private string _superAdminInput = string.Empty;
    private string _superAdminError = string.Empty;
    private string _adminChannelText = string.Empty;
    private bool _isAdminChannelRestricted;
    private bool _isChecking;
    private string _tokenHint = string.Empty;

    public DiscordBotPanel(
        DiscordBotHost host,
        AppSetup setup,
        DiscordBotSettingsFile settings,
        BotTokenStore tokens,
        BotWebhooks webhooks,
        BotAuditLog audit,
        DialogueBox box,
        ILogger<DiscordBotPanel> logger)
    {
        _host = host;
        _setup = setup;
        _settings = settings;
        _audit = audit;
        _box = box;
        _logger = logger;
        var loadError = settings.LoadError;
        try
        {
            _editor.Load(settings.Config);
        }
        catch (Exception ex)
        {
            // Settings that can not even be shown must not stop Trebuchet: the bot stays off and nothing is saved.
            logger.LogError(ex, @"The Discord bot settings could not be shown");
            loadError ??= ex.GetBaseException().Message;
        }
        CanEdit = loadError is null;
        LoadError = loadError is { } error
            ? string.Format(Resources.DiscordBotLoadError, error, settings.Path)
            : string.Empty;

        var defaults = new DiscordBotConfig();
        AutoStartField = Track(new ToggleField()
            .SetTitle(Resources.DiscordBotAutoStart)
            .SetDescription(Resources.DiscordBotAutoStartText)
            .SetGetter(() => _editor.AutoStart)
            .SetSetter(v => Edit(() => _editor.AutoStart = v))
            .SetDefault(() => defaults.AutoStart)
            .SetEnabled(CanEdit));

        Users = new BotIdListViewModel(Resources.DiscordBotUsers, Resources.DiscordBotUsersText,
            text => _editor.TryAddUser(text), id => _editor.RemoveUser(id), UserRows, OnEdited) { IsEnabled = CanEdit };
        Roles = new BotIdListViewModel(Resources.DiscordBotRoles, Resources.DiscordBotRolesText,
            text => _editor.TryAddRole(text), id => _editor.RemoveRole(id),
            () => _editor.RoleIds.Select(x => new BotIdRow(x, false, string.Empty)), OnEdited) { IsEnabled = CanEdit };

        AuditChannelField = Track(new TextField()
            .SetTitle(Resources.DiscordBotAuditChannel)
            .SetDescription(Resources.DiscordBotAuditChannelText)
            .SetGetter(() => _editor.AuditChannelIdText)
            .SetSetter(v => Edit(() => { if (!_editor.SetAuditChannelIdText(v)) OnTokenTypedIntoIdField(AuditChannelField!); }))
            // Its reset button undoes an unsaved change; it never clears a saved ID.
            .SetDefault(() => SavedId(_settings.Config.AuditChannelId))
            .SetEnabled(CanEdit));

        // The audit log under its own name and picture (through a webhook), and the community log for the players.
        AuditWebhookField = WebhookField(webhooks.Audit, Resources.DiscordBotAuditWebhook, Resources.DiscordBotAuditWebhookText);
        AuditNameField = TextSetting(Resources.DiscordBotAuditName, Resources.DiscordBotAuditNameText,
            () => _editor.AuditName, v => _editor.AuditName = v, defaults.AuditName);
        AuditAvatarField = TextSetting(Resources.DiscordBotAuditAvatar, Resources.DiscordBotAuditAvatarText,
            () => _editor.AuditAvatarUrl, v => _editor.AuditAvatarUrl = v, defaults.AuditAvatarUrl);
        CommunityEnabledField = Track(new ToggleField()
            .SetTitle(Resources.DiscordBotCommunityEnabled)
            .SetDescription(Resources.DiscordBotCommunityEnabledText)
            .SetGetter(() => _editor.CommunityEnabled)
            .SetSetter(v => Edit(() => _editor.CommunityEnabled = v))
            .SetDefault(() => defaults.CommunityEnabled)
            .SetEnabled(CanEdit));
        CommunityWebhookField = WebhookField(webhooks.Community, Resources.DiscordBotCommunityWebhook, Resources.DiscordBotCommunityWebhookText);
        CommunityNameField = TextSetting(Resources.DiscordBotCommunityName, Resources.DiscordBotCommunityNameText,
            () => _editor.CommunityName, v => _editor.CommunityName = v, defaults.CommunityName);
        CommunityAvatarField = TextSetting(Resources.DiscordBotCommunityAvatar, Resources.DiscordBotCommunityAvatarText,
            () => _editor.CommunityAvatarUrl, v => _editor.CommunityAvatarUrl = v, defaults.CommunityAvatarUrl);
        BotIdentityField = Track(new ToggleField()
            .SetTitle(Resources.DiscordBotBotIdentity)
            .SetDescription(Resources.DiscordBotBotIdentityText)
            .SetGetter(() => _editor.BotTakesCommunityIdentity)
            .SetSetter(v => Edit(() => _editor.BotTakesCommunityIdentity = v))
            .SetDefault(() => defaults.BotTakesCommunityIdentity)
            .SetEnabled(CanEdit));
        CommunityLeadField = Limit(Resources.DiscordBotCommunityLead, Resources.DiscordBotCommunityLeadText, DiscordBotLimits.CommunityLead,
            () => _editor.CommunityLeadMinutes, v => _editor.CommunityLeadMinutes = v, defaults.CommunityLeadMinutes);

        LimitFields =
        [
            Limit(Resources.DiscordBotCooldown, Resources.DiscordBotCooldownText, DiscordBotLimits.Cooldown,
                () => _editor.CooldownSeconds, v => _editor.CooldownSeconds = v, defaults.CooldownSeconds),
            Limit(Resources.DiscordBotCriticalLimit, Resources.DiscordBotCriticalLimitText, DiscordBotLimits.CriticalLimit,
                () => _editor.CriticalLimit, v => _editor.CriticalLimit = v, defaults.CriticalLimit),
            Limit(Resources.DiscordBotCriticalWindow, string.Empty, DiscordBotLimits.CriticalWindow,
                () => _editor.CriticalWindowMinutes, v => _editor.CriticalWindowMinutes = v, defaults.CriticalWindowMinutes),
            Limit(Resources.DiscordBotLockoutAttempts, Resources.DiscordBotLockoutAttemptsText, DiscordBotLimits.LockoutAttempts,
                () => _editor.LockoutAttempts, v => _editor.LockoutAttempts = v, defaults.LockoutAttempts),
            Limit(Resources.DiscordBotLockoutMinutes, string.Empty, DiscordBotLimits.LockoutMinutes,
                () => _editor.LockoutMinutes, v => _editor.LockoutMinutes = v, defaults.LockoutMinutes),
            Limit(Resources.DiscordBotMaxPause, Resources.DiscordBotMaxPauseText, DiscordBotLimits.MaxPause,
                () => _editor.MaxPauseMinutes, v => _editor.MaxPauseMinutes = v, defaults.MaxPauseMinutes),
            Limit(Resources.DiscordBotActionDelay, Resources.DiscordBotActionDelayText, DiscordBotLimits.ActionDelay,
                () => _editor.ActionDelayMinutes, v => _editor.ActionDelayMinutes = v, defaults.ActionDelayMinutes),
            Track(new TextField()
                .SetTitle(Resources.DiscordBotPostponeChoices)
                .SetDescription(Resources.DiscordBotPostponeChoicesText)
                .SetGetter(() => _editor.PostponeChoicesText)
                .SetSetter(v => Edit(() => _editor.PostponeChoicesText = v ?? string.Empty))
                .SetDefault(() => string.Join(@", ", defaults.PostponeChoices))
                .SetEnabled(CanEdit))
        ];

        TokenField = new SecretField(new BotTokenSecretStore(tokens))
            .SetTitle(Resources.DiscordBotToken)
            .SetDescription(Resources.DiscordBotTokenText)
            .SetTexts(Resources.DiscordBotTokenSaved,
                string.Format(Resources.DiscordBotTokenFromEnvironment, BotTokenStore.EnvironmentVariable),
                string.Format(Resources.DiscordBotTokenNeedsEnvironment, BotTokenStore.EnvironmentVariable),
                Resources.DiscordBotTokenInvalid, Resources.DiscordBotTokenSaveFailed, Resources.DiscordBotTokenWatermark);
        TokenField.ConfirmRemove = () => ConfirmAsync(Resources.DiscordBotTokenRemoveTitle, Resources.DiscordBotTokenRemoveText);
        TokenField.Saved += OnTokenSaved;
        TokenField.Removed += OnTokenRemoved;
        TokenField.SaveFailed += OnTokenSaveFailed;

        GuildField = Track(new TextField()
            .SetTitle(Resources.DiscordBotGuild)
            .SetDescription(Resources.DiscordBotGuildText)
            .SetGetter(() => _editor.GuildIdText)
            .SetSetter(v => Edit(() => { if (!_editor.SetGuildIdText(v)) OnTokenTypedIntoIdField(GuildField!); }))
            .SetDefault(() => SavedId(_settings.Config.GuildId))
            .SetEnabled(CanEdit));

        LanguageField = Track(new ComboBoxField()
            .SetTitle(Resources.DiscordBotLanguage)
            .SetDescription(Resources.DiscordBotLanguageText)
            .AddOption(@"Deutsch")
            .AddOption(@"English")
            .SetGetter(() => string.Equals(_editor.Language, @"en", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .SetSetter(v => Edit(() => _editor.Language = v == 1 ? @"en" : @"de"))
            .SetDefault(() => string.Equals(defaults.Language, @"en", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .SetEnabled(CanEdit));

        // The super admin always may use every command, so the commands of that level have no switch: its note says so.
        CommandGroups = Enum.GetValues<BotLevel>()
            .Select(level => new BotCommandGroup(level, Enum.GetValues<BotCommand>()
                .Where(command => BotCommands.LevelOf(command) == level && level != BotLevel.SuperAdmin)
                .Select(CommandField)
                .ToList()))
            .Where(group => group.Fields.Count > 0 || group.Level == BotLevel.SuperAdmin)
            .ToList();


        SaveCommand = ReactiveCommand.CreateFromTask(OnSave,
            this.WhenAnyValue(x => x.IsDirty, dirty => dirty && CanEdit));
        RevertCommand = ReactiveCommand.CreateFromTask(OnRevert, this.WhenAnyValue(x => x.IsDirty));
        StartCommand = ReactiveCommand.CreateFromTask(OnStart, this.WhenAnyValue(x => x.CanStart));
        StopCommand = ReactiveCommand.CreateFromTask(OnStop, this.WhenAnyValue(x => x.CanStop));
        ReconnectCommand = ReactiveCommand.CreateFromTask(OnReconnect, this.WhenAnyValue(x => x.CanReconnect));
        ChangeSuperAdminCommand = ReactiveCommand.Create(OnChangeSuperAdmin);
        ApplySuperAdminCommand = ReactiveCommand.CreateFromTask(OnApplySuperAdmin);
        CancelSuperAdminCommand = ReactiveCommand.Create(OnCancelSuperAdmin);
        ClearAdminChannelCommand = ReactiveCommand.CreateFromTask(OnClearAdminChannel,
            this.WhenAnyValue(x => x.IsAdminChannelRestricted, restricted => restricted && CanEdit));
        CheckSecurityCommand = ReactiveCommand.CreateFromTask(OnCheckSecurity);
        OpenAuditFolderCommand = ReactiveCommand.CreateFromTask(OnOpenAuditFolder);
        OpenSetupHelpCommand = ReactiveCommand.CreateFromTask(OnOpenSetupHelp);

        _audit.EntryWritten += OnAuditEntryWritten;
        ShowEditorValues();
        ReloadAudit();
    }

    public string Icon => @"mdi-robot";
    public string Label => Resources.DiscordBotPanel;
    public bool CanBeOpened => true;

    /// <summary>False when the settings file could not be read: nothing can be saved or started.</summary>
    public bool CanEdit { get; }
    public string LoadError { get; }
    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    // --- toolbar
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> RevertCommand { get; }
    public ObservableCollection<string> Issues { get; } = [];
    public bool HasIssues => Issues.Count > 0;

    public bool IsDirty
    {
        get => _isDirty;
        private set => this.RaiseAndSetIfChanged(ref _isDirty, value);
    }

    // --- status
    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> ReconnectCommand { get; }
    public ToggleField AutoStartField { get; }
    public ObservableCollection<string> ServiceIssues { get; } = [];
    public bool HasServiceIssues => ServiceIssues.Count > 0;

    public string StateText
    {
        get => _stateText;
        private set => this.RaiseAndSetIfChanged(ref _stateText, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    /// <summary>Connecting or stopping.</summary>
    public bool IsTransitioning
    {
        get => _isTransitioning;
        private set => this.RaiseAndSetIfChanged(ref _isTransitioning, value);
    }

    public bool IsFailed
    {
        get => _isFailed;
        private set => this.RaiseAndSetIfChanged(ref _isFailed, value);
    }

    public string ProblemText
    {
        get => _problemText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _problemText, value);
            this.RaisePropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => !string.IsNullOrEmpty(ProblemText);

    public string BotName
    {
        get => _botName;
        private set => this.RaiseAndSetIfChanged(ref _botName, value);
    }

    public string GuildName
    {
        get => _guildName;
        private set => this.RaiseAndSetIfChanged(ref _guildName, value);
    }

    public string LatencyText
    {
        get => _latencyText;
        private set => this.RaiseAndSetIfChanged(ref _latencyText, value);
    }

    public string ConnectedSinceText
    {
        get => _connectedSinceText;
        private set => this.RaiseAndSetIfChanged(ref _connectedSinceText, value);
    }

    public bool CanStart
    {
        get => _canStart;
        private set => this.RaiseAndSetIfChanged(ref _canStart, value);
    }

    public bool CanStop
    {
        get => _canStop;
        private set => this.RaiseAndSetIfChanged(ref _canStop, value);
    }

    public bool CanReconnect
    {
        get => _canReconnect;
        private set => this.RaiseAndSetIfChanged(ref _canReconnect, value);
    }

    /// <summary>The bot could not be created: everything else of Trebuchet works, the panel says so.</summary>
    public bool HasServiceError
    {
        get => _hasServiceError;
        private set => this.RaiseAndSetIfChanged(ref _hasServiceError, value);
    }

    public bool AutoStartSkipped
    {
        get => _autoStartSkipped;
        private set => this.RaiseAndSetIfChanged(ref _autoStartSkipped, value);
    }

    // --- security
    public BotIdListViewModel Users { get; }
    public BotIdListViewModel Roles { get; }
    public TextField AuditChannelField { get; }
    public IReadOnlyList<FieldElement> LimitFields { get; }
    public ReactiveCommand<Unit, Unit> ChangeSuperAdminCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplySuperAdminCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelSuperAdminCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAdminChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckSecurityCommand { get; }
    public ObservableCollection<SecurityCheckRow> CheckRows { get; } = [];

    public string SuperAdminText
    {
        get => _superAdminText;
        private set => this.RaiseAndSetIfChanged(ref _superAdminText, value);
    }

    public bool IsEditingSuperAdmin
    {
        get => _isEditingSuperAdmin;
        private set => this.RaiseAndSetIfChanged(ref _isEditingSuperAdmin, value);
    }

    public string SuperAdminInput
    {
        get => _superAdminInput;
        set => this.RaiseAndSetIfChanged(ref _superAdminInput, value ?? string.Empty);
    }

    public string SuperAdminError
    {
        get => _superAdminError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _superAdminError, value);
            this.RaisePropertyChanged(nameof(HasSuperAdminError));
        }
    }

    public bool HasSuperAdminError => !string.IsNullOrEmpty(SuperAdminError);

    public string AdminChannelText
    {
        get => _adminChannelText;
        private set => this.RaiseAndSetIfChanged(ref _adminChannelText, value);
    }

    /// <summary>The bot answers in the admin channel only (and that is not dropped yet).</summary>
    public bool IsAdminChannelRestricted
    {
        get => _isAdminChannelRestricted;
        private set => this.RaiseAndSetIfChanged(ref _isAdminChannelRestricted, value);
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set => this.RaiseAndSetIfChanged(ref _isChecking, value);
    }

    // --- audit log identity and community log
    public SecretField AuditWebhookField { get; }
    public TextField AuditNameField { get; }
    public TextField AuditAvatarField { get; }
    public ToggleField CommunityEnabledField { get; }
    public ToggleField BotIdentityField { get; }
    public SecretField CommunityWebhookField { get; }
    public TextField CommunityNameField { get; }
    public TextField CommunityAvatarField { get; }
    public IntField CommunityLeadField { get; }

    // --- connection
    public SecretField TokenField { get; }
    public TextField GuildField { get; }
    public ComboBoxField LanguageField { get; }

    public string TokenHint
    {
        get => _tokenHint;
        private set
        {
            this.RaiseAndSetIfChanged(ref _tokenHint, value);
            this.RaisePropertyChanged(nameof(HasTokenHint));
        }
    }

    public bool HasTokenHint => !string.IsNullOrEmpty(TokenHint);

    // --- servers
    /// <summary>One row per server instance: managed by the bot or not, and the name the bot uses.</summary>
    public ObservableCollection<BotInstanceRow> InstanceRows { get; } = [];
    public bool HasNoInstances => InstanceRows.Count == 0;

    // --- commands, audit, setup
    public IReadOnlyList<BotCommandGroup> CommandGroups { get; }
    public ObservableCollection<AuditEntryRow> AuditEntries { get; } = [];
    public bool HasAuditEntries => AuditEntries.Count > 0;
    public ReactiveCommand<Unit, Unit> OpenAuditFolderCommand { get; }
    /// <summary>Opens the step by step setup guide in its own window (one at a time).</summary>
    public ReactiveCommand<Unit, Unit> OpenSetupHelpCommand { get; }

    public Task<bool> StartPanel()
    {
        // Never awaited: Discord being slow or down must not hold up the start of Trebuchet. Failures show here.
        _ = _host.AutoStartAsync();
        return Task.FromResult(true);
    }

    public Task TickPanel()
    {
        // Never throws: failing ticks count towards the crash restart of the whole window.
        RefreshStatusSafe();
        return Task.CompletedTask;
    }

    public Task RefreshPanel()
    {
        RefreshStatusSafe();
        return Task.CompletedTask;
    }

    public Task DisplayPanel()
    {
        try
        {
            // The settings are read when shown, unless there are edits that are not saved yet.
            if (_editor.IsDirty) _editor.RefreshAdminChannels(_settings.Config);
            else _editor.Load(_settings.Config);
            ShowEditorValues();
            TokenField.Refresh();
            AuditWebhookField.Refresh();
            CommunityWebhookField.Refresh();
            ReloadAudit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Showing the Discord bot settings failed");
        }
        RefreshStatusSafe();
        return Task.CompletedTask;
    }

    private T Track<T>(T field) where T : IRefreshableField
    {
        _fields.Add(field);
        return field;
    }

    private IntField Limit(string title, string description, BotLimitRange range, Func<int> get, Action<int> set, int defaultValue) =>
        Track(new IntField(range.Min, range.Max)
            .SetTitle(title)
            .SetDescription(description)
            .SetGetter(get)
            .SetSetter(v => Edit(() => set(v)))
            .SetDefault(() => defaultValue)
            .SetEnabled(CanEdit));

    private TextField TextSetting(string title, string description, Func<string> get, Action<string> set, string defaultValue) =>
        Track(new TextField()
            .SetTitle(title)
            .SetDescription(description)
            .SetGetter(get)
            .SetSetter(v => Edit(() => set(v ?? string.Empty)))
            .SetDefault(() => defaultValue)
            .SetEnabled(CanEdit));

    // Like the token: write-only, stored protected, saved at once (not part of the unsaved edits).
    private SecretField WebhookField(BotWebhookStore store, string title, string description)
    {
        var field = new SecretField(new BotWebhookSecretStore(store))
            .SetTitle(title)
            .SetDescription(description)
            .SetTexts(Resources.DiscordBotWebhookSaved, Resources.DiscordBotWebhookSaved, Resources.DiscordBotWebhookNeedsWindows,
                Resources.DiscordBotWebhookInvalid, Resources.DiscordBotWebhookSaveFailed, Resources.DiscordBotWebhookWatermark);
        field.ConfirmRemove = () => ConfirmAsync(Resources.DiscordBotWebhookRemoveTitle, Resources.DiscordBotWebhookRemoveText);
        field.Saved += (_, _) => _logger.LogInformation(@"Discord bot webhook saved");
        field.Removed += (_, _) => _logger.LogInformation(@"Discord bot webhook removed");
        field.SaveFailed += (_, e) => _logger.LogError(@"Storing a Discord bot webhook failed: {Type}", e.Error.GetType().Name);
        return field;
    }

    private ToggleField CommandField(BotCommand command) =>
        Track(new ToggleField()
            .SetTitle(DiscordBotTexts.Command(command))
            .SetDescription(DiscordBotTexts.CommandDescription(command))
            .SetGetter(() => _editor.IsCommandEnabled(command))
            .SetSetter(v => Edit(() => _editor.SetCommandEnabled(command, v)))
            .SetDefault(() => BotCommands.EnabledByDefault.Contains(command))
            .SetEnabled(CanEdit));

    private IEnumerable<BotIdRow> UserRows()
    {
        // The super admin heads the list and can not be removed (DiscordBotConfig.RemoveUser refuses it too).
        if (_editor.SuperAdminUserId != 0)
            yield return new BotIdRow(_editor.SuperAdminUserId, true, Resources.DiscordBotSuperAdmin);
        foreach (var id in _editor.UserIds.Where(x => x != _editor.SuperAdminUserId))
            yield return new BotIdRow(id, false, string.Empty);
    }

    private void Edit(Action change)
    {
        if (!CanEdit) return;
        change();
        OnEdited();
    }

    private void OnEdited()
    {
        try
        {
            IsDirty = _editor.IsDirty;
            ShowAdminChannel();
            if (_showIssues) ShowIssues(_editor.Validate(_settings.Config).Issues);
        }
        catch (Exception ex)
        {
            // Called from the setters of the fields: an error here must not break the input. Save checks again.
            _logger.LogError(ex, @"Checking the Discord bot settings failed");
        }
    }

    /// <summary>Shows the values of the editor in every field and list.</summary>
    private void ShowEditorValues()
    {
        foreach (var field in _fields)
            field.Update.Execute().Subscribe();
        Users.Refresh();
        Roles.Refresh();
        ShowInstances();
        SuperAdminText = _editor.SuperAdminUserId == 0
            ? Resources.DiscordBotSuperAdminNotSet
            : _editor.SuperAdminUserId.ToString(CultureInfo.InvariantCulture);
        ShowAdminChannel();
        IsDirty = _editor.IsDirty;
    }

    // The rows follow the number of server instances of Trebuchet. Rebuilt only when the editor shows its values, never
    // while typing: the text box keeps its focus.
    private void ShowInstances()
    {
        var count = Math.Clamp(_setup.Config.ServerInstanceCount, 0, BotInputRules.MaxInstance + 1);
        InstanceRows.Clear();
        for (var instance = 0; instance < count; instance++)
            InstanceRows.Add(new BotInstanceRow(instance, _editor, count, CanEdit, OnEdited));
        this.RaisePropertyChanged(nameof(HasNoInstances));
    }

    private void ShowAdminChannel()
    {
        var channels = _editor.AdminChannelIds;
        IsAdminChannelRestricted = channels.Count > 0 && !_editor.ClearAdminChannelPending;
        AdminChannelText = channels.Count == 0 ? Resources.DiscordBotAdminChannelAll
            : _editor.ClearAdminChannelPending ? Resources.DiscordBotAdminChannelAllPending
            : string.Join(@", ", channels.Select(x => x.ToString(CultureInfo.InvariantCulture)));
    }

    private void ShowIssues(IEnumerable<BotConfigIssue> issues)
    {
        Issues.Clear();
        foreach (var issue in issues)
            Issues.Add(DiscordBotTexts.Issue(issue));
        this.RaisePropertyChanged(nameof(HasIssues));
    }

    private async Task OnSave()
    {
        try
        {
            if (!CanEdit) return;
            var issues = _editor.Save(_settings.Config);
            if (issues.Count > 0)
            {
                // Keep the list up to date with every further edit until it is saved.
                _showIssues = true;
                ShowIssues(issues);
                return;
            }
            _showIssues = false;
            ShowIssues([]);
            ShowEditorValues();
            _logger.LogInformation(@"Discord bot settings saved ({Users} users, {Roles} roles)",
                _editor.UserIds.Count, _editor.RoleIds.Count);
            await _host.ApplySettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Saving the Discord bot settings failed");
            await ShowErrorAsync(ex.Message);
        }
        RefreshStatusSafe();
    }

    private async Task OnRevert()
    {
        try
        {
            _editor.Load(_settings.Config);
            _showIssues = false;
            ShowIssues([]);
            Users.ClearInput();
            Roles.ClearInput();
            OnCancelSuperAdmin();
            ShowEditorValues();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Reverting the Discord bot settings failed");
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task OnStart()
    {
        try
        {
            _logger.LogInformation(@"Starting the Discord bot");
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Starting the Discord bot failed");
            await ShowErrorAsync(ex.Message);
        }
        RefreshStatusSafe();
    }

    private async Task OnStop()
    {
        try
        {
            _logger.LogInformation(@"Stopping the Discord bot");
            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Stopping the Discord bot failed");
            await ShowErrorAsync(ex.Message);
        }
        RefreshStatusSafe();
    }

    private async Task OnReconnect()
    {
        try
        {
            _logger.LogInformation(@"Reconnecting the Discord bot");
            TokenHint = string.Empty;
            await _host.ReconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Reconnecting the Discord bot failed");
            await ShowErrorAsync(ex.Message);
        }
        RefreshStatusSafe();
    }

    private void OnChangeSuperAdmin()
    {
        SuperAdminInput = string.Empty;
        SuperAdminError = string.Empty;
        IsEditingSuperAdmin = CanEdit;
    }

    private void OnCancelSuperAdmin()
    {
        SuperAdminInput = string.Empty;
        SuperAdminError = string.Empty;
        IsEditingSuperAdmin = false;
    }

    private async Task OnApplySuperAdmin()
    {
        try
        {
            if (!CanEdit) return;
            var result = _editor.CheckSuperAdmin(SuperAdminInput, out var id);
            if (result == BotIdInputResult.Duplicate)
            {
                OnCancelSuperAdmin();
                return;
            }
            if (result != BotIdInputResult.Added)
            {
                // A pasted token is shown in plain text in the box: out at once.
                if (result == BotIdInputResult.LooksLikeToken) SuperAdminInput = string.Empty;
                SuperAdminError = DiscordBotTexts.IdInput(result);
                return;
            }

            var current = _editor.SuperAdminUserId;
            if (current != 0 && !await ConfirmAsync(Resources.DiscordBotSuperAdminConfirmTitle,
                    string.Format(Resources.DiscordBotSuperAdminConfirmText,
                        id.ToString(CultureInfo.InvariantCulture), current.ToString(CultureInfo.InvariantCulture))))
                return;

            _editor.SetSuperAdmin(id);
            OnCancelSuperAdmin();
            ShowEditorValues();
            OnEdited();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Changing the super admin failed");
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task OnClearAdminChannel()
    {
        try
        {
            if (!CanEdit) return;
            if (!await ConfirmAsync(Resources.DiscordBotAdminChannelClearTitle, Resources.DiscordBotAdminChannelClearText))
                return;
            _editor.ClearAdminChannel();
            OnEdited();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Allowing all channels failed");
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task OnCheckSecurity()
    {
        CheckRows.Clear();
        IsChecking = true;
        try
        {
            using var timeout = new CancellationTokenSource(CheckTimeout);
            var items = await _host.CheckSecurityAsync(timeout.Token);
            foreach (var item in items)
                CheckRows.Add(new SecurityCheckRow(item));
        }
        catch (OperationCanceledException)
        {
            await ShowErrorAsync(Resources.DiscordBotCheckTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"The security check of the Discord bot failed");
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task OnOpenAuditFolder()
    {
        try
        {
            Directory.CreateDirectory(_audit.Directory);
            Process.Start(new ProcessStartInfo(_audit.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Opening the folder of the audit log failed");
            await ShowErrorAsync(ex.Message);
        }
    }

    private DiscordBotHelpWindow? _helpWindow;

    private async Task OnOpenSetupHelp()
    {
        try
        {
            if (_helpWindow is { } open)
            {
                // Already open: bring it to the front instead of opening a second one.
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }
            var window = new DiscordBotHelpWindow { DataContext = new DiscordBotHelpViewModel(OnOpenSetupGuide) };
            window.Closed += (_, _) => _helpWindow = null;
            _helpWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _helpWindow = null;
            _logger.LogError(ex, @"Opening the setup help failed");
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task OnOpenSetupGuide()
    {
        try
        {
            tot_lib.Utils.OpenWeb(SetupGuideUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Opening the setup guide failed");
            await ShowErrorAsync(ex.Message);
        }
    }

    /// <summary>The token was pasted into an ID field: it is not kept, and the box is emptied right away.</summary>
    private void OnTokenTypedIntoIdField(IRefreshableField field)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                field.Update.Execute().Subscribe();
                await ShowErrorAsync(Resources.DiscordBotIdLooksLikeToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, @"Clearing the field failed");
            }
        });
    }

    private void OnTokenSaved(object? sender, EventArgs e)
    {
        _logger.LogInformation(@"Discord bot token saved");
        var state = _host.CreatedService?.State;
        TokenHint = state is DiscordBotState.Connected or DiscordBotState.Starting or DiscordBotState.Error
            ? Resources.DiscordBotTokenReconnect
            : string.Empty;
    }

    private void OnTokenRemoved(object? sender, EventArgs e)
    {
        _logger.LogInformation(@"Discord bot token removed");
        TokenHint = string.Empty;
    }

    private void OnTokenSaveFailed(object? sender, SecretSaveFailedEventArgs e)
    {
        // Only the type and the cleaned message: the exception never carries the token, but nothing is left to chance.
        _logger.LogError(@"Storing the Discord bot token failed: {Type}: {Message}",
            e.Error.GetType().Name, BotLogRedactor.Redact(e.Error.Message));
    }

    private void OnAuditEntryWritten(object? sender, BotAuditEntry entry)
    {
        // Written on a thread of the bot.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                AddAuditEntry(entry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, @"Showing an audit entry failed");
            }
        });
    }

    private void ReloadAudit()
    {
        AuditEntries.Clear();
        foreach (var entry in _audit.Recent.Reverse())
            AuditEntries.Add(new AuditEntryRow(entry));
        this.RaisePropertyChanged(nameof(HasAuditEntries));
    }

    private void AddAuditEntry(BotAuditEntry entry)
    {
        if (AuditEntries.Any(x => ReferenceEquals(x.Entry, entry))) return;
        AuditEntries.Insert(0, new AuditEntryRow(entry));
        while (AuditEntries.Count > 200)
            AuditEntries.RemoveAt(AuditEntries.Count - 1);
        this.RaisePropertyChanged(nameof(HasAuditEntries));
    }

    private void OnServiceStateChanged(object? sender, EventArgs e)
    {
        // Raised on any thread.
        Dispatcher.UIThread.Post(RefreshStatusSafe);
    }

    private void RefreshStatusSafe()
    {
        try
        {
            RefreshStatus();
            _statusErrorLogged = false;
        }
        catch (Exception ex)
        {
            if (_statusErrorLogged) return;
            _statusErrorLogged = true;
            _logger.LogError(ex, @"Reading the state of the Discord bot failed");
        }
    }

    private void RefreshStatus()
    {
        var service = _host.IsShutDown ? null : _host.Service;
        HasServiceError = _host.CreationFailed;
        if (service is not null && !ReferenceEquals(service, _subscribed))
        {
            service.StateChanged += OnServiceStateChanged;
            _subscribed = service;
        }

        // The bot may have set the admin channel from Discord.
        _editor.RefreshAdminChannels(_settings.Config);
        ShowAdminChannel();

        var state = service?.State ?? DiscordBotState.Stopped;
        var problem = service?.Problem ?? DiscordBotProblem.None;
        StateText = DiscordBotTexts.State(state);
        IsConnected = state == DiscordBotState.Connected;
        IsTransitioning = state is DiscordBotState.Starting or DiscordBotState.Stopping;
        IsFailed = state == DiscordBotState.Error || problem != DiscordBotProblem.None;
        ProblemText = DiscordBotTexts.Problem(problem);

        var issues = problem == DiscordBotProblem.InvalidSettings && service is not null
            ? service.Issues.Select(DiscordBotTexts.Issue).ToList()
            : [];
        if (!issues.SequenceEqual(ServiceIssues))
        {
            ServiceIssues.Clear();
            foreach (var issue in issues) ServiceIssues.Add(issue);
            this.RaisePropertyChanged(nameof(HasServiceIssues));
        }

        BotName = Display(service?.BotName);
        GuildName = Display(service?.GuildName);
        var latency = service?.LatencyMs ?? -1;
        LatencyText = IsConnected && latency >= 0
            ? string.Format(CultureInfo.CurrentCulture, Resources.DiscordBotLatencyValue, latency)
            : Dash;
        ConnectedSinceText = IsConnected && service?.ConnectedSinceUtc is { } since ? FormatSince(since) : Dash;

        var available = service is not null && _host.IsAvailable;
        CanStart = available && state is DiscordBotState.Stopped or DiscordBotState.Error;
        CanStop = available && state is DiscordBotState.Starting or DiscordBotState.Connected or DiscordBotState.Error;
        CanReconnect = available && state is DiscordBotState.Connected or DiscordBotState.Error;
        AutoStartSkipped = _host.AutoStartSkipped && state != DiscordBotState.Connected;
    }

    private static string FormatSince(DateTime utc)
    {
        var since = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var running = DateTime.UtcNow - since;
        var local = since.ToLocalTime().ToString(@"g", CultureInfo.CurrentCulture);
        return running > TimeSpan.Zero ? $@"{local} ({running.Humanize(2)})" : local;
    }

    private static string SavedId(ulong id) => id == 0 ? string.Empty : id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Names come from Discord: one clean line.</summary>
    private static string Display(string? text)
    {
        var clean = BotInputRules.SanitizeForLog(text);
        return clean.Length == 0 ? Dash : clean;
    }

    private async Task<bool> ConfirmAsync(string title, string text)
    {
        var confirm = new OnBoardingConfirmation(title, text);
        await _box.OpenAsync(confirm);
        return confirm.Result;
    }

    private async Task ShowErrorAsync(string message)
    {
        try
        {
            await _box.OpenErrorAsync(BotLogRedactor.Redact(message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Could not show the error message");
        }
    }
}
