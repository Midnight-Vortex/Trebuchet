using System;
using System.Linq;
using System.Reactive;
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

        var startingFile = files.Sync.Resolve(uiConfig.CurrentSyncProfile);
        FileMenu = new FileMenuViewModel<SyncProfile, SyncProfileRef>(Resources.PanelSync, files.Sync, dialogueBox, logger);
        FileMenu.FileSelected += OnFileSelected;
        FileMenu.Selected = startingFile;
        
        _profile = files.Sync.Get(startingFile);
        
        Sync = ReactiveCommand.CreateFromTask(OnSync);
        SyncEdit = ReactiveCommand.CreateFromTask(OnSyncEdit);
        RefreshList = ReactiveCommand.CreateFromTask(() => ModList.SetList(_profile.Modlist, true));

        var canDownloadMods = blocker.WhenAnyValue(x => x.CanDownloadMods);
        Update = ReactiveCommand.CreateFromTask(async () =>
        {
            await ModList.UpdateMods();
            await OnRequestRefresh();
        }, canDownloadMods);

    }
    
    private readonly ILogger<SyncPanel> _logger;
    private readonly DialogueBox _dialogueBox;
    private readonly AppFiles _files;
    private readonly UIConfig _uiConfig;
    private SyncProfile _profile;
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
        await ModList.SetList(_profile.Modlist, false);
        ClientConnectionList.SetList(_profile.ClientConnections);
    }

    private Task OnFileChanged() => OnFileSelected(this, FileMenu.Selected);
    private async Task OnFileSelected(object? sender, SyncProfileRef profile)
    {
        _logger.LogDebug(@"Swap to sync {sync}", profile);
        _uiConfig.CurrentSyncProfile = profile.Uri.OriginalString;
        _uiConfig.SaveFile();
        _profile = _files.Sync.Get(profile);
        await ModList.SetReadOnly();
        await ModList.SetList(_profile.Modlist, false);
    }
    
    private async Task OnSync()
    {
        _logger.LogInformation(@"Sync modList");

        if (string.IsNullOrEmpty(_profile.SyncURL))
            await OnSyncEdit();

        try
        {
            await _files.Sync.Sync(FileMenu.Selected);
            await OnFileChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, @"Failed");
            await _dialogueBox.OpenErrorAsync(Resources.InvalidURL);
        }
    }

    private async Task OnSyncEdit()
    {
        var editor = new OnBoardingNameSelection(Resources.Sync, Resources.SyncText);
        editor.Value = _profile.SyncURL;
        editor.PlaceholderText = @"https://";
        await _dialogueBox.OpenAsync(editor);
        if (editor.Value is null) return;
        _profile.SyncURL = editor.Value;
        _profile.SaveFile();
    }
    
    private async Task OnRequestRefresh()
    {
        if(RequestRefresh is not null)
            await RequestRefresh.Invoke(this, EventArgs.Empty);
    }
}