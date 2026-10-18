using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using TrebuchetLib;

namespace Trebuchet.ViewModels.SettingFields;

/// <summary>
/// DSL "Warning Messages": list of (time before restart, message) edited directly on the server profile.
/// </summary>
public class RestartWarningsField : DescriptiveElement<RestartWarningsField>, IRefreshableField
{
    public RestartWarningsField(Func<ServerProfile> profile, Action save, Func<List<RestartWarning>> defaults)
    {
        _profile = profile;
        _save = save;
        _defaults = defaults;

        Add = ReactiveCommand.Create(() =>
        {
            var last = Warnings.Count == 0 ? TimeSpan.FromMinutes(10) : Warnings.Min(x => x.Before);
            var before = last > TimeSpan.FromMinutes(1) ? last - TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(30);
            AddRow(new RestartWarning(before, string.Empty));
            Commit();
        });
        Remove = ReactiveCommand.Create<RestartWarningViewModel>(row =>
        {
            Warnings.Remove(row);
            Commit();
        });
        ResetDefaults = ReactiveCommand.Create(() =>
        {
            Load(_defaults());
            Commit();
        });
        Update = ReactiveCommand.Create(() => Load(_profile().RestartWarnings));

        Load(_profile().RestartWarnings);
    }

    private readonly Func<ServerProfile> _profile;
    private readonly Action _save;
    private readonly Func<List<RestartWarning>> _defaults;
    private bool _isVisible = true;
    private bool _loading;

    public ObservableCollection<RestartWarningViewModel> Warnings { get; } = [];

    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    public ReactiveCommand<Unit, Unit> Add { get; }
    public ReactiveCommand<RestartWarningViewModel, Unit> Remove { get; }
    public ReactiveCommand<Unit, Unit> ResetDefaults { get; }
    public ReactiveCommand<Unit, Unit> Update { get; }

    private void Load(System.Collections.Generic.IEnumerable<RestartWarning> warnings)
    {
        _loading = true;
        Warnings.Clear();
        foreach (var warning in warnings)
            AddRow(warning);
        _loading = false;
    }

    private void AddRow(RestartWarning warning)
    {
        var row = new RestartWarningViewModel(warning.Before, warning.Message);
        row.PropertyChanged += (_, _) => Commit();
        Warnings.Add(row);
    }

    private void Commit()
    {
        if (_loading) return;
        _profile().RestartWarnings = Warnings
            .Select(x => new RestartWarning(x.Before, x.Message))
            .ToList();
        _save();
    }
}

public class RestartWarningViewModel(TimeSpan before, string message) : ReactiveObject
{
    private TimeSpan _before = before;
    private string _message = message;

    public TimeSpan Before
    {
        get => _before;
        set => this.RaiseAndSetIfChanged(ref _before, value);
    }

    public string Message
    {
        get => _message;
        set => this.RaiseAndSetIfChanged(ref _message, value);
    }
}
