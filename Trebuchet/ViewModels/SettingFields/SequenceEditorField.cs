using System;
using System.Reactive;
using System.Windows.Input;
using ReactiveUI;
using tot_lib.CommandLine;
using TrebuchetLib.Sequences;

namespace Trebuchet.ViewModels.SettingFields;

public class SequenceEditorField : DescriptiveElement<SequenceEditorField>, IRefreshableField
{
    /// <param name="sequence">
    /// Read on every refresh: the panel switches between server saves, a captured sequence kept editing the first save.
    /// </param>
    public SequenceEditorField(string labelTemplate, Func<Sequence> sequence, ReactiveCommand<Sequence,Unit> command)
    {
        _sequence = sequence();
        Command = command;
        _count = _sequence.Actions.Count;
        _label = string.Format(labelTemplate, _count);
        Update = ReactiveCommand.Create(() =>
        {
            Sequence = sequence();
            Count = Sequence.Actions.Count;
            Label = string.Format(labelTemplate, Count);
        });
    }
    
    private string _label;
    private int _count;
    private Sequence _sequence;

    public ReactiveCommand<Unit, Unit> Update { get; }

    public Sequence Sequence
    {
        get => _sequence;
        private set => this.RaiseAndSetIfChanged(ref _sequence, value);
    }
    public ReactiveCommand<Sequence,Unit> Command { get; }
    public int Count
    {
        get => _count;
        set => this.RaiseAndSetIfChanged(ref _count, value);
    }

    public string Label
    {
        get => _label;
        set => this.RaiseAndSetIfChanged(ref _label, value);
    }
}