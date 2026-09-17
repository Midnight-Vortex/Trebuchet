using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DynamicData.Binding;
using ReactiveUI;
using tot_lib;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels;

public sealed class WorkshopSearchViewModel : ReactiveObject
{
    public WorkshopSearchViewModel(Steam steam, AppSetup setup)
    {
        _steam = steam;
        _setup = setup;
        SearchFirstPage = ReactiveCommand.CreateFromTask(() =>
        {
            Page = 1;
            return Search(_searchTerm, 1);
        });

        NextPage = ReactiveCommand.Create<Unit>((_) =>
        {
            Page++;
        }, this.WhenAnyValue(x => x.Page, x => x.MaxPage, (p, m) => p < m));
        
        PreviousPage = ReactiveCommand.Create<Unit>((_) =>
        {
            Page--;
        }, this.WhenAnyValue(x => x.Page, (p) => p > 1));

        this.WhenValueChanged<WorkshopSearchViewModel, uint>(x => x.Page, false, () => 1)
            .InvokeCommand(ReactiveCommand.CreateFromTask<uint>(Search));
    }

    private string _searchTerm = string.Empty;
    private readonly Steam _steam;
    private readonly AppSetup _setup;
    private bool _isLoading;
    private int _maxPage = 1;
    private uint _page = 1;

    public event AsyncEventHandler<WorkshopSearchResult>? ModAdded;
    public event AsyncEventHandler? PageLoaded;
    public ObservableCollectionExtended<WorkshopSearchResult> SearchResults { get; } = [];
    
    public ReactiveCommand<Unit,Unit> SearchFirstPage { get; }
    public ReactiveCommand<Unit,Unit> NextPage { get; }
    public ReactiveCommand<Unit,Unit> PreviousPage { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public int MaxPage
    {
        get => _maxPage;
        set => this.RaiseAndSetIfChanged(ref _maxPage, value);
    }

    public uint Page
    {
        get => _page;
        set => this.RaiseAndSetIfChanged(ref _page, value);
    }

    public string SearchTerm
    {
        get => _searchTerm;
        set => this.RaiseAndSetIfChanged(ref _searchTerm, value);
    }

    private Task Search(uint page) => Search(SearchTerm, page);

    private async Task Search(string searchTerm, uint page)
    {
        IsLoading = true;

        var appId = Constants.AppIDLiveClient;
        var requiredTag = Constants.GetWorkshopRequiredTag(_setup.Edition);
        var excludedTag = Constants.GetWorkshopExcludedTag(_setup.Edition);
        var response = await _steam.QueryWorkshopSearch(appId, searchTerm, 20, page, requiredTag, excludedTag);
        if (response is null)
        {
            IsLoading = false;
            return;
        }
        MaxPage = Math.Max((int)Math.Ceiling(response.total / 20.0), 1);
        SearchResults.Clear();
        foreach (var file in response.publishedfiledetails)
        {
            var searchResult = new WorkshopSearchResult(file);
            searchResult.ModAdded += OnModAdded;
            SearchResults.Add(searchResult);
        }

        PageLoaded?.Invoke(this, EventArgs.Empty);
        IsLoading = false;
    }

    private void OnModAdded(object? sender, WorkshopSearchResult result)
    {
        ModAdded?.Invoke(this, result);
    }
}
