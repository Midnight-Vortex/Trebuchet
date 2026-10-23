using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;

namespace Trebuchet.ViewModels.DiscordBot;

/// <summary>One ID of a list. A locked row (the super admin) can not be removed.</summary>
public sealed class BotIdRow(ulong id, bool isLocked, string label)
{
    public ulong Id { get; } = id;
    public string Text { get; } = id.ToString(CultureInfo.InvariantCulture);
    public bool IsLocked { get; } = isLocked;
    public bool CanRemove => !IsLocked;
    /// <summary>Shown next to a locked row ("Super admin").</summary>
    public string Label { get; } = label;
    public bool HasLabel => !string.IsNullOrEmpty(Label);
}

/// <summary>
/// Add/remove editor of Discord IDs (allowed users or roles). Every ID is checked by the editor
/// (<see cref="DiscordBotSettingsEditor.ParseId"/>); a pasted token is cleared from the box at once.
/// </summary>
public sealed class BotIdListViewModel : ReactiveObject
{
    private readonly Func<string, BotIdInputResult> _add;
    private readonly Func<ulong, bool> _remove;
    private readonly Func<IEnumerable<BotIdRow>> _rows;
    private readonly Action _changed;
    private string _input = string.Empty;
    private string _error = string.Empty;
    private bool _isEnabled = true;
    private bool _isEmpty = true;

    public BotIdListViewModel(string title, string description, Func<string, BotIdInputResult> add,
        Func<ulong, bool> remove, Func<IEnumerable<BotIdRow>> rows, Action changed)
    {
        Title = title;
        Description = description;
        _add = add;
        _remove = remove;
        _rows = rows;
        _changed = changed;
        Add = ReactiveCommand.Create(OnAdd, this.WhenAnyValue(x => x.Input, x => x.IsEnabled,
            (input, enabled) => enabled && !string.IsNullOrWhiteSpace(input)));
        Remove = ReactiveCommand.Create<BotIdRow>(OnRemove, this.WhenAnyValue(x => x.IsEnabled));
        Refresh();
    }

    public string Title { get; }
    public string Description { get; }
    public ObservableCollection<BotIdRow> Items { get; } = [];
    public ReactiveCommand<Unit, Unit> Add { get; }
    public ReactiveCommand<BotIdRow, Unit> Remove { get; }

    public string Input
    {
        get => _input;
        set => this.RaiseAndSetIfChanged(ref _input, value ?? string.Empty);
    }

    public string Error
    {
        get => _error;
        private set
        {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => this.RaiseAndSetIfChanged(ref _isEmpty, value);
    }

    /// <summary>Shows the IDs of the editor again (after a revert, a save or a new super admin).</summary>
    public void Refresh()
    {
        Items.Clear();
        foreach (var row in _rows())
            Items.Add(row);
        IsEmpty = Items.Count == 0;
    }

    public void ClearInput()
    {
        Input = string.Empty;
        Error = string.Empty;
    }

    private void OnAdd()
    {
        // The command is disabled then; this also holds when it is called directly.
        if (!IsEnabled) return;
        var result = _add(Input);
        switch (result)
        {
            case BotIdInputResult.Added:
                ClearInput();
                Refresh();
                _changed();
                break;
            case BotIdInputResult.LooksLikeToken:
                // Out of the box at once: it is shown in plain text there.
                Input = string.Empty;
                Error = DiscordBotTexts.IdInput(result);
                break;
            default:
                Error = DiscordBotTexts.IdInput(result);
                break;
        }
    }

    private void OnRemove(BotIdRow? row)
    {
        if (row is null || row.IsLocked || !IsEnabled) return;
        if (!_remove(row.Id)) return;
        Error = string.Empty;
        Refresh();
        _changed();
    }
}
