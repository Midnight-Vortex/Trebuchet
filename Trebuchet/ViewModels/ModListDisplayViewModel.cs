using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Text.RegularExpressions;
using DynamicData.Binding;
using ReactiveUI;
using Trebuchet.Assets;

namespace Trebuchet.ViewModels;

/// <summary>A view of a mod list. Only the unfiltered load-order view exposes the editable source.</summary>
public sealed class ModListDisplayViewModel : ReactiveObject
{
    private readonly ObservableCollectionExtended<IModFile> _source;
    private IEnumerable<IModFile> _items;
    private string _searchPattern = string.Empty;
    private int _sortIndex;
    private int _directionIndex;
    private bool _isReadOnly;
    private string _searchError = string.Empty;
    private int _visibleCount;
    private string? _regexPattern;
    private Regex? _regex;

    public ModListDisplayViewModel(ObservableCollectionExtended<IModFile> source)
    {
        _source = source;
        _items = source;
        _source.CollectionChanged += (_, _) => Refresh();
        ResetView = ReactiveCommand.Create(() =>
        {
            _searchPattern = string.Empty;
            _sortIndex = 0;
            _directionIndex = 0;
            this.RaisePropertyChanged(nameof(SearchPattern));
            this.RaisePropertyChanged(nameof(SortIndex));
            this.RaisePropertyChanged(nameof(DirectionIndex));
            Refresh();
        });
        Refresh();
    }

    // Index 0 always represents the actual load order; descending never reverses it.
    public IReadOnlyList<string> SortOptions { get; } =
        [Resources.ModViewLoadOrder, Resources.ModViewUpdated, Resources.ModViewName, Resources.ModViewSize];
    public IReadOnlyList<string> DirectionOptions { get; } = [Resources.ModViewAscending, Resources.ModViewDescending];
    public ReactiveCommand<Unit, Unit> ResetView { get; }
    public IEnumerable<IModFile> Items => _items;
    public bool IsViewActive => SortIndex != 0 || SearchPattern.Length > 0;
    public bool CanReorder => !IsReadOnly && !IsViewActive;
    public bool CanChooseDirection => SortIndex != 0;
    public string SearchError => _searchError;
    public bool HasSearchError => SearchError.Length > 0;
    public bool HasNoMatches => _visibleCount == 0 && !HasSearchError;
    public string ResultCount => string.Format(Resources.ModViewCount, _visibleCount, _source.Count);

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            this.RaiseAndSetIfChanged(ref _isReadOnly, value);
            this.RaisePropertyChanged(nameof(CanReorder));
        }
    }

    public string SearchPattern
    {
        get => _searchPattern;
        set
        {
            value ??= string.Empty;
            if (_searchPattern == value) return;
            this.RaiseAndSetIfChanged(ref _searchPattern, value);
            Refresh();
        }
    }

    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (value < 0 || value >= SortOptions.Count || value == _sortIndex) return;
            this.RaiseAndSetIfChanged(ref _sortIndex, value);
            Refresh();
        }
    }

    public int DirectionIndex
    {
        get => _directionIndex;
        set
        {
            if (value < 0 || value >= DirectionOptions.Count || value == _directionIndex) return;
            this.RaiseAndSetIfChanged(ref _directionIndex, value);
            Refresh();
        }
    }

    private void Refresh()
    {
        _searchError = string.Empty;
        try
        {
            IEnumerable<IModFile> results = _source;
            if (SearchPattern.Length > 0)
            {
                if (_regex is null || _regexPattern != SearchPattern)
                {
                    _regex = new Regex(SearchPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100));
                    _regexPattern = SearchPattern;
                }
                var regex = _regex;
                var watch = Stopwatch.StartNew();
                var matches = new List<IModFile>();
                foreach (var mod in _source)
                {
                    if (watch.ElapsedMilliseconds > 200) throw new RegexMatchTimeoutException();
                    if (regex.IsMatch(mod.Title) || regex.IsMatch(mod.Export()) || regex.IsMatch(mod.FilePath))
                        matches.Add(mod);
                }
                results = matches;
            }

            var descending = DirectionIndex == 1;
            results = SortIndex switch
            {
                1 => descending
                    ? results.OrderBy(x => !x.UpdatedAtUtc.HasValue).ThenByDescending(x => x.UpdatedAtUtc)
                    : results.OrderBy(x => !x.UpdatedAtUtc.HasValue).ThenBy(x => x.UpdatedAtUtc),
                2 => descending
                    ? results.OrderByDescending(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                    : results.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase),
                3 => descending ? results.OrderByDescending(x => x.FileSize) : results.OrderBy(x => x.FileSize),
                _ => results
            };
            // Never hand the drag behavior a mutable sorted/filtered collection.
            _items = IsViewActive ? Array.AsReadOnly(results.ToArray()) : _source;
            _visibleCount = IsViewActive ? _items.Count() : _source.Count;
        }
        catch (ArgumentException)
        {
            _searchError = Resources.ModViewInvalidRegex;
            _items = Array.Empty<IModFile>();
            _visibleCount = 0;
        }
        catch (RegexMatchTimeoutException)
        {
            _searchError = Resources.ModViewRegexTimeout;
            _items = Array.Empty<IModFile>();
            _visibleCount = 0;
        }

        this.RaisePropertyChanged(nameof(Items));
        this.RaisePropertyChanged(nameof(IsViewActive));
        this.RaisePropertyChanged(nameof(CanReorder));
        this.RaisePropertyChanged(nameof(CanChooseDirection));
        this.RaisePropertyChanged(nameof(SearchError));
        this.RaisePropertyChanged(nameof(HasSearchError));
        this.RaisePropertyChanged(nameof(HasNoMatches));
        this.RaisePropertyChanged(nameof(ResultCount));
    }
}
