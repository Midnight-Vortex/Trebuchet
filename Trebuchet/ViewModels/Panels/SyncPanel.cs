using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SteamWorksWebAPI;
using SteamWorksWebAPI.Interfaces;
using tot_lib;
using Trebuchet.Assets;
using Trebuchet.Services;
using Trebuchet.ViewModels.InnerContainer;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels.Panels;

public class SyncPanel : ReactiveObject, IRefreshablePanel, IDisplablePanel, IRefreshingPanel
{
    public SyncPanel(
        ILogger<SyncPanel> logger,
        DialogueBox dialogueBox,
        AppFiles files,
        UIConfig uiConfig,
        ModListViewModel modList,
        TaskBlocker blocker,
        ClientConnectionListViewModel clientConnectionList
        )
    {
        _logger = logger;
        _dialogueBox = dialogueBox;
        _files = files;
        _uiConfig = uiConfig;

        ModList = modList;
        ClientConnectionList = clientConnectionList;
        ClientConnectionList.SetReadOnly();

        ModList.IsReadOnly = true;
        FileMenu = new FileMenuViewModel<SyncProfile, SyncProfileRef>(Resources.PanelSync, files.Sync, dialogueBox, logger, allowEmpty: true);
        if (files.Sync.TryResolve(uiConfig.CurrentSyncProfile, out var startingFile))
            FileMenu.Selected = startingFile;
        _profile = FileMenu.Selected is { } selected ? files.Sync.Get(selected) : null;
        FileMenu.FileSelected += OnFileSelected;
        _needRefresh = true;
        var canUseProfile = FileMenu.WhenAnyValue(x => x.Selected, x => x.IsLoading,
            (profile, loading) => profile is not null && !loading);
        Sync = ReactiveCommand.CreateFromTask(OnSync, canUseProfile);
        SyncEdit = ReactiveCommand.CreateFromTask(OnSyncEdit, canUseProfile);
        RefreshList = ReactiveCommand.CreateFromTask(() => ModList.SetList(_profile?.Modlist ?? [], true), canUseProfile);

        var canDownloadMods = blocker.WhenAnyValue(x => x.CanDownloadMods);
        Update = ReactiveCommand.CreateFromTask(async () =>
        {
            await ModList.UpdateMods();
            await OnRequestRefresh();
        }, canDownloadMods.CombineLatest(canUseProfile, (download, profile) => download && profile));

    }
    
    private readonly ILogger<SyncPanel> _logger;
    private readonly DialogueBox _dialogueBox;
    private readonly AppFiles _files;
    private readonly UIConfig _uiConfig;
    private SyncProfile? _profile;
    private bool _needRefresh;

    public string Icon => @"mdi-web-sync";
    public string Label => Resources.PanelSync;
    public bool CanBeOpened { get; } = true;
    public FileMenuViewModel<SyncProfile, SyncProfileRef> FileMenu { get; }
    
    public ReactiveCommand<Unit, Unit> Sync { get; }
    public ReactiveCommand<Unit, Unit> SyncEdit { get; }
    public ReactiveCommand<Unit, Unit> Update { get; }
    public ReactiveCommand<Unit, Unit> RefreshList { get; }
    
    public event AsyncEventHandler? RequestRefresh;
    
    public ModListViewModel ModList { get; }
    public ClientConnectionListViewModel ClientConnectionList { get; }
    public bool HasProfile => _profile is not null;
    
    
    public Task RefreshPanel()
    {
        _logger.LogDebug(@"Refresh panel");
        _needRefresh = true;
        return Task.CompletedTask;
    }

    public async Task DisplayPanel()
    {
        _logger.LogDebug(@"Display panel");
        if (!_needRefresh) return;
        _needRefresh = false;
        ClientConnectionList.SetList(_profile?.ClientConnections ?? []);
        await ModList.SetList(_profile?.Modlist ?? [], false);
    }

    private Task OnFileChanged() => OnFileSelected(this, FileMenu.Selected);
    private async Task OnFileSelected(object? sender, SyncProfileRef? profile)
    {
        _logger.LogDebug(@"Swap to sync {sync}", profile);
        _uiConfig.CurrentSyncProfile = profile?.Uri.OriginalString ?? string.Empty;
        _uiConfig.SaveFile();
        _profile = profile is null ? null : _files.Sync.Get(profile);
        this.RaisePropertyChanged(nameof(HasProfile));
        ClientConnectionList.SetList(_profile?.ClientConnections ?? []);
        await ModList.SetList(_profile?.Modlist ?? [], false);
    }
    
    private async Task OnSync()
    {
        _logger.LogInformation(@"Sync modList");

        if (_profile is not { } profile || FileMenu.Selected is not { } reference) return;
        FileMenu.IsLoading = true;
        try
        {
            if (string.IsNullOrWhiteSpace(profile.SyncURL) && !await EditSyncUrl(profile)) return;
            await _files.Sync.Sync(reference);
            await OnFileChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, @"Failed");
            await _dialogueBox.OpenErrorAsync(Resources.InvalidURL);
        }
        finally { FileMenu.IsLoading = false; }
    }

    private async Task OnSyncEdit()
    {
        if (_profile is not { } profile) return;
        FileMenu.IsLoading = true;
        try { await EditSyncUrl(profile); }
        finally { FileMenu.IsLoading = false; }
    }

    private async Task<bool> EditSyncUrl(SyncProfile profile)
    {
        var editor = new OnBoardingNameSelection(Resources.Sync, Resources.SyncText);
        editor.Value = profile.SyncURL;
        editor.PlaceholderText = @"https://";
        await _dialogueBox.OpenAsync(editor);
        if (string.IsNullOrWhiteSpace(editor.Value)) return false;
        profile.SyncURL = editor.Value;
        profile.SaveFile();
        return true;
    }
    
    private async Task OnRequestRefresh()
    {
        if(RequestRefresh is not null)
            await RequestRefresh.Invoke(this, EventArgs.Empty);
    }
}
