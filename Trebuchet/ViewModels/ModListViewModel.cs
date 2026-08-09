using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DynamicData.Binding;
using Humanizer;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using tot_lib;
using AppResources = Trebuchet.Assets.Resources;
using Trebuchet.Services;
using Trebuchet.ViewModels.InnerContainer;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels;

public class ModListViewModel : ReactiveObject
{
    public ModListViewModel(
        Operations operations,
        ILogger<ModListViewModel> logger, 
        ModFileFactory modFileFactory,
        Steam steam,
        AppSetup setup,
        IProgressCallback<DepotDownloader.Progress> progress,
        DialogueBox dialogueBox)
    {
        _logger = logger;
        _modFileFactory = modFileFactory;
        _steam = steam;
        _setup = setup;
        _dialogueBox = dialogueBox;

        List.CollectionChanged += OnListChanged;
        progress.ProgressChanged += OnProgressChanged;
        operations.ModFileChanged += OnModFileChanged;
    }

    private readonly ILogger _logger;
    private readonly ModFileFactory _modFileFactory;
    private readonly Steam _steam;
    private readonly AppSetup _setup;
    private readonly DialogueBox _dialogueBox;
    private string _size = string.Empty;
    private bool _isReadOnly;
    private bool _isLoading;

    public event AsyncEventHandler? ModListChanged;
    
    public ObservableCollectionExtended<IModFile> List { get; } = [];

    public string Size
    {
        get => _size;
        set => this.RaiseAndSetIfChanged(ref _size, value);
    }

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    internal async Task SetReadOnly()
    {
        if (_isReadOnly) return;
        IsReadOnly = true;
        await SetList(List.Select(x => x.Export()), false);
    }

    internal Task UpdateMods()
    {
        return UpdateMods(List.OfType<IPublishedModFile>().Select(x => x.PublishedId).ToList());
    }
    
    internal async Task UpdateMods(List<ulong> mods)
    {
        _logger.LogInformation(@"Updating mods");
        try
        {
            await _steam.UpdateMods(mods);
            using(List.SuspendNotifications())
                await QueryFromWorkshop(List, true);
        }
        catch(OperationCanceledException){}
        catch (Exception tex)
        {
            _logger.LogError(tex, @"Failed");
            await _dialogueBox.OpenErrorAsync(tex.Message);
        }
    }
    
    internal async Task SetList(IEnumerable<string> modList, bool force)
    {
        using (List.SuspendNotifications())
        {
            List.Clear();
            if(!IsReadOnly)
                List.AddRange(modList
                    .Select(x => _modFileFactory
                        .Create(x)
                        .SetActions(RemoveModFile, UpdateModFile)
                        .Build()
                    )
                );
            else
                List.AddRange(modList
                    .Select(x => _modFileFactory
                        .Create(x)
                        .SetActions(UpdateModFile)
                        .Build()
                    )
                );
            await QueryFromWorkshop(List, force);
        }
        Size = CalculateModListSize().Bytes().Humanize();
    }
    
    public async Task Add(WorkshopSearchResult mod)
    {
        if (IsReadOnly) return;
        if (List.Any(x => x is IPublishedModFile pub && pub.PublishedId == mod.PublishedFileId)) return;

        IsLoading = true;
        try
        {
            // Search protobuf may omit tags; details API is authoritative for edition checks.
            var details = await _steam.RequestModDetails([mod.PublishedFileId]);
            var published = details.FirstOrDefault();
            var tags = published?.Tags is { Count: > 0 } detailTags ? detailTags : mod.Tags;
            var appId = published?.ConsumerAppId ?? mod.AppId;
            var title = published?.Title ?? mod.Title;

            if (!Constants.IsWorkshopModCompatible(_setup.Edition, tags, appId, title))
            {
                await _dialogueBox.OpenErrorAsync(string.Format(
                    AppResources.ErrorModWrongEdition,
                    title,
                    Constants.GetEditionDisplayName(_setup.Edition)));
                return;
            }

            _logger.LogInformation(@"Adding mod {mod} from workshop", mod.PublishedFileId);
            List.Add(_modFileFactory.Create(mod.PublishedFileId.ToString())
                .SetActions(RemoveModFile, UpdateModFile).Build()
            );
            await QueryFromWorkshop(List, false);
            Size = CalculateModListSize().Bytes().Humanize();
        }
        finally
        {
            IsLoading = false;
        }
    }


    public void Add(string file)
    {
        if (IsReadOnly) return;
        _logger.LogInformation(@"Adding mod {file}", file);
        List.Add(_modFileFactory.Create(file).SetActions(RemoveModFile, UpdateModFile).Build());
        Size = CalculateModListSize().Bytes().Humanize();
    }

    public void AddRange(IEnumerable<string> files)
    {
        if (IsReadOnly) return;
        using (List.SuspendNotifications())
        {
            foreach (var file in files)
            {
                _logger.LogInformation(@"Adding mod {file}", file);
                List.Add(_modFileFactory
                    .Create(file)
                    .SetActions(RemoveModFile, UpdateModFile)
                    .Build());
            }
        }
        Size = CalculateModListSize().Bytes().Humanize();
    }

    public Task RemoveModFile(IModFile mod)
    {
        _logger.LogInformation(@"Remove mod {mod}", mod.Export());
        List.Remove(mod);
        Size = CalculateModListSize().Bytes().Humanize();
        return Task.CompletedTask;
    }
    
    private Task UpdateModFile(IPublishedModFile mod)
    {
        return UpdateMods([mod.PublishedId]);
    }
    
    private void OnProgressChanged(object? sender, DepotDownloader.Progress e)
    {
        if (!e.IsFile) return;

        foreach (var file in List)
        {
            if(file is IPublishedModFile pub && pub.PublishedId == e.PublishedId)
                file.Progress.Report(e);
        }
    }
    
    private async void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        try
        {
            Size = CalculateModListSize().Bytes().Humanize();
            await OnModListChanged();
        }
        catch(Exception ex)
        {
            _logger.LogError(ex, @"Failed");
            await _dialogueBox.OpenErrorAsync(ex.Message);
        }
    }
    
    private long CalculateModListSize()
    {
        return List.Count == 0 ? 0 : List.Select(x => x.FileSize).Aggregate((a, b) => a+b);
    }
    
    private void OnModFileChanged(object? sender, FileSystemEventArgs e)
    {
        var fullPath = e.FullPath;
        var watch = new Stopwatch();
        watch.Start();
        if (!ModListUtil.TryParseDirectory2ModId(fullPath, out var id)) return;
        for (var i = 0; i < List.Count; i++)
        {
            var modFile = List[i];
            if (modFile is not IPublishedModFile published || published.PublishedId != id) continue;
            var mod = published.PublishedId.ToString();
            if (_setup.TryGetModPath(mod, out var path))
                mod = path;
            if(!IsReadOnly)
                List[i] = _modFileFactory.Create(modFile, mod)
                    .SetActions(RemoveModFile, UpdateModFile)
                    .Build();
            else
                List[i] = _modFileFactory.Create(modFile, mod)
                    .SetActions(UpdateModFile)
                    .Build();
        }
        watch.Stop();
        using(_logger.BeginScope((@"fullPath", fullPath)))
            _logger.LogDebug(@$"Update time {watch.ElapsedMilliseconds}ms");
    }
    
    public async Task QueryFromWorkshop(IList<IModFile> files, bool force)
    {
        var published = files.OfType<IPublishedModFile>().Select(x => x.PublishedId).ToList();
        IsLoading = true;
        if(force)
            _steam.ClearModDetailsCache();
        var details = await _steam.RequestModDetails(published);
        for (var i = 0; i < files.Count; i++)
        {
            var current = files[i];
            if (current is not IPublishedModFile pub) continue;
            var workshop = details.FirstOrDefault(d => d.PublishedFileId == pub.PublishedId);
            if (workshop is null) continue;
            if (workshop.CreatorAppId != 0)
                files[i] = _modFileFactory.Create(workshop, workshop.Status)
                    .SetActions(RemoveModFile, UpdateModFile)
                    .Build();
            else
                files[i] = _modFileFactory.CreateUnknown(pub.FilePath, pub.PublishedId)
                    .SetActions(RemoveModFile, UpdateModFile)
                    .Build();
        }

        IsLoading = false;
    }

    private async Task OnModListChanged()
    {
        if(ModListChanged is not null)
            await ModListChanged.Invoke(this, EventArgs.Empty);
    }
}