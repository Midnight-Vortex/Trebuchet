using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using tot_lib;
using Trebuchet.Assets;
using Trebuchet.Services;
using Trebuchet.ViewModels.InnerContainer;
using Trebuchet.Windows;
using TrebuchetLib;
using TrebuchetLib.Services;
using TrebuchetLib.Services.Importer;

namespace Trebuchet.ViewModels.Panels
{
    public class ModListPanel : ReactiveObject, IRefreshablePanel, IDisplablePanel, IRefreshingPanel
    {
        public ModListPanel(
            ModListViewModel modList,
            AppFiles appFiles,
            AppSetup setup,
            UIConfig uiConfig, 
            TaskBlocker blocker,
            DialogueBox box,
            ModlistImporter importer,
            WorkshopSearchViewModel workshop,
            ILogger<ModListPanel> logger)
        {
            ModList = modList;
            ModList.ModListChanged += OnModListChanged;
            
            _appFiles = appFiles;
            _setup = setup;
            _uiConfig = uiConfig;
            _box = box;
            _importer = importer;
            _workshop = workshop;
            _logger = logger;
            _workshop.ModAdded += (_,mod) => ModList.Add(mod);
            
            var startingFile = _appFiles.Mods.Resolve(_uiConfig.CurrentModlistProfile);
            FileMenu = new FileMenuViewModel<ModListProfile, ModListProfileRef>(Resources.PanelMods, appFiles.Mods, box, logger);
            FileMenu.KeepConfigInSync(setup, logger);
            FileMenu.FileSelected += OnFileSelected;
            FileMenu.Selected = startingFile;
            
            _profile = _appFiles.Mods.Get(startingFile);

            Workshop = ReactiveCommand.Create(OnExploreWorkshop);
            EditAsText = ReactiveCommand.CreateFromTask(OnEditModListAsText);
            RefreshList = ReactiveCommand.CreateFromTask(() => ModList.SetList(_profile.Modlist, true));
            CreateFromMismatch = ReactiveCommand.CreateFromTask(OnCreateFromMismatch);
            DropFiles = ReactiveCommand.Create<List<IStorageItem>?>(OnDropFiles);

            var canDownloadMods = blocker.WhenAnyValue(x => x.CanDownloadMods);
            Update = ReactiveCommand.CreateFromTask(async () =>
            {
                await ModList.UpdateMods();
                await OnRequestRefresh();
            }, canDownloadMods);
        }
        private readonly AppFiles _appFiles;
        private readonly AppSetup _setup;
        private readonly UIConfig _uiConfig;
        private readonly DialogueBox _box;
        private readonly ModlistImporter _importer;
        private readonly WorkshopSearchViewModel _workshop;
        private readonly ILogger<ModListPanel> _logger;
        private bool _needRefresh;
        private ModListProfile _profile;
        private WorkshopSearch? _searchWindow;
        private bool _canBeOpened = true;

        public ReactiveCommand<Unit, Unit> Workshop { get; }
        public ReactiveCommand<Unit, Unit> EditAsText { get; }
        public ReactiveCommand<Unit, Unit> Update { get; }
        public ReactiveCommand<Unit, Unit> RefreshList { get; }
        /// <summary>Makes a new mod list from the last mod mismatch in the log of the game client.</summary>
        public ReactiveCommand<Unit, Unit> CreateFromMismatch { get; }
        public ReactiveCommand<List<IStorageItem>?, Unit> DropFiles { get; }
        
        public FileMenuViewModel<ModListProfile, ModListProfileRef> FileMenu { get; }
        
        public ModListViewModel ModList { get; }

        public string Icon => @"mdi-toy-brick";
        public string Label => Resources.PanelMods;

        public bool CanBeOpened
        {
            get => _canBeOpened;
            set => this.RaiseAndSetIfChanged(ref _canBeOpened, value);
        }

        public event AsyncEventHandler? RequestRefresh;
        
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
        }

        private void OnDropFiles(List<IStorageItem>? files)
        {
            if (files is null) return;
            ModList.AddRange(files
                .Where(x => x.Path.IsFile && System.IO.Path.GetExtension(x.Path.LocalPath) == @".pak")
                .Select(x => x.Path.LocalPath)
            );
        }
        
        private Task OnModListChanged(object? sender, EventArgs args)
        {
            _profile.Modlist = ModList.List.Select(x => x.Export()).ToList();
            _profile.SaveFile();
            return Task.CompletedTask;
        }
        
        private async Task OnFileSelected(object? sender, ModListProfileRef? profile)
        {
            if (profile is null) return;
            _logger.LogDebug(@"Swap to mod list {modList}", profile);
            _uiConfig.CurrentModlistProfile = profile.Uri.OriginalString;
            _uiConfig.SaveFile();
            _profile = _appFiles.Mods.Get(profile);
            await ModList.SetList(_profile.Modlist, false);
        }

        private void OnExploreWorkshop()
        {
            if (_searchWindow != null) return;
            _searchWindow = new ()
            {
                DataContext = _workshop
            };
            _searchWindow.Closing += OnSearchClosing;
            _searchWindow.Show();
        }
     
        private async Task OnEditModListAsText()
        {
            var modList = _setup.GetModsPath(_profile.Modlist, false);
            var editor = new OnBoardingModlistImport(string.Join(Environment.NewLine, modList));
            
            while (true)
            {
                await _box.OpenAsync(editor);
                if (editor.Value is null) return;
                try
                {
                    var parsed = _importer.Import(editor.Value, ImportFormats.Txt);
                    await ModList.SetList(parsed.Modlist, false);
                    return;
                }
                catch(Exception ex)
                {
                    await _box.OpenErrorAsync(ex.Message);
                }
            }
        }

        /// <summary>
        /// A server refuses a client whose mods differ with the list of all its mods (workshop ids, in its order), and the client
        /// writes that list to its log. The game cannot fix the mods Trebuchet manages, so the list becomes a new Trebuchet mod list.
        /// </summary>
        private async Task OnCreateFromMismatch()
        {
            var clientFolder = _setup.GetClientFolder();
            if (string.IsNullOrWhiteSpace(clientFolder) || !Directory.Exists(clientFolder))
            {
                await _box.OpenErrorAsync(Resources.ModMismatchTitle, Resources.ModMismatchNoClient);
                return;
            }

            string logFolder = Path.Combine(clientFolder, Constants.FolderGameSave, Constants.FolderGameSaveLog);
            ModMismatchReport? report;
            try
            {
                report = await Task.Run(() =>
                {
                    logFolder = ModMismatchLog.GetClientLogFolder(clientFolder);
                    return ModMismatchLog.FindNewest(logFolder);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, @"Failed to read the client logs in {folder}", logFolder);
                await _box.OpenErrorAsync(Resources.ModMismatchTitle, ex.Message);
                return;
            }

            var modlist = report?.GetModlist() ?? [];
            if (report is null || modlist.Count == 0)
            {
                _logger.LogInformation(@"No mod mismatch with workshop mods in the client logs in {folder}", logFolder);
                await _box.OpenAsync(new OnBoardingMessage(Resources.ModMismatchTitle,
                    string.Format(Resources.ModMismatchNotFound, logFolder)));
                return;
            }

            var server = string.IsNullOrWhiteSpace(report.Server) ? Resources.ModMismatchUnknownServer : report.Server;
            var time = report.TimeUtc?.ToLocalTime().ToString(@"yyyy-MM-dd HH:mm") ?? @"?";
            var description = string.Format(Resources.ModMismatchFound, server, time, modlist.Count,
                Path.GetFileName(report.LogFile), Resources.Update);
            var withoutId = report.GetEntriesWithoutId().ToList();
            if (withoutId.Count > 0)
                description += Environment.NewLine + string.Format(Resources.ModMismatchNoId, withoutId.Count, string.Join(@", ", withoutId));
            description += Environment.NewLine + Resources.ModMismatchName;

            var suggestion = string.Format(Resources.ModMismatchListName,
                string.IsNullOrWhiteSpace(report.Server) ? time : report.Server);
            var reference = await FileMenu.CreateWith(Resources.ModMismatchTitle, description, suggestion,
                profile => profile.Modlist = modlist);
            if (reference is not null)
                _logger.LogInformation(@"Mod list {name} made from the mod mismatch of {server} in {file} ({count} mods)",
                    reference.Name, report.Server, report.LogFile, modlist.Count);
        }

        private void OnSearchClosing(object? sender, CancelEventArgs e)
        {
            if (_searchWindow == null) return;
            _searchWindow.Closing -= OnSearchClosing;
            _searchWindow = null;
        }

        private async Task OnRequestRefresh()
        {
            if(RequestRefresh is not null)
                await RequestRefresh.Invoke(this, EventArgs.Empty);
        }
    }
}
