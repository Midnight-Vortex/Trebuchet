using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData.Binding;
using ReactiveUI;

namespace Trebuchet.ViewModels.SettingFields;

public class TimeOfDayListField : Field<TimeOfDayListField, ObservableCollectionExtended<TimeSpanViewModel>>
{
    public TimeOfDayListField(bool useSeconds) : base([])
    {
        UseSeconds = useSeconds;

        Modify = ReactiveCommand.Create<TimeSpan>((_) =>
        {
            ValueChanged.Execute(Value).Subscribe();
        });

        Add = ReactiveCommand.Create(() =>
        {
            var row = new TimeSpanViewModel(TimeSpan.Zero);
            WireRow(row);
            Value.Add(row);
            ValueChanged.Execute(Value).Subscribe();

        });

        Remove = ReactiveCommand.Create<TimeSpanViewModel>((vm) =>
        {
            Value.Remove(vm);
            ValueChanged.Execute(Value).Subscribe();
        });

        // Every shown row saves its edits. Rows are created anew by each refresh (Update) and reset: wiring only the
        // first rows lost every edit of an existing time after the first refresh. The subscriptions of replaced rows
        // are disposed, the command would otherwise keep every row ever created alive.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Value))
                WireRows();
        };
    }

    private CompositeDisposable _rowSubscriptions = new();
    
    private Func<List<TimeSpan>>? _internalGetter;
    private Func<List<TimeSpan>>? _internalDefaultBuilder;
    private Action<List<TimeSpan>>? _internalSetter;
    
    public bool UseSeconds { get; }
    
    public ReactiveCommand<Unit,Unit> Add { get; }
    public ReactiveCommand<TimeSpanViewModel, Unit> Remove { get; }
    public ReactiveCommand<TimeSpan,Unit> Modify { get; }
    
    public TimeOfDayListField SetGetter(Func<List<TimeSpan>> getter)
    {
        _internalGetter = getter;
        Getter = InternalGetter;
        Value = Getter.Invoke();
        return this;
    }
    
    public TimeOfDayListField SetSetter(Action<List<TimeSpan>> setter)
    {
        _internalSetter = setter;
        Setter = InternalSetter;
        return this;
    }
    
    public TimeOfDayListField SetDefault(Func<List<TimeSpan>> defGenerator)
    {
        _internalDefaultBuilder = defGenerator;
        DefaultBuilder = InternalDefaultBuilder;
        return this;
    }

    private void WireRows()
    {
        _rowSubscriptions.Dispose();
        _rowSubscriptions = new CompositeDisposable();
        foreach (var row in Value)
            WireRow(row);
    }

    private void WireRow(TimeSpanViewModel row)
    {
        _rowSubscriptions.Add(row.WhenAnyValue(x => x.TimeSpan).Skip(1).InvokeCommand(Modify));
    }

    protected override bool AreValuesEqual(ObservableCollectionExtended<TimeSpanViewModel> valueA, ObservableCollectionExtended<TimeSpanViewModel> valueB)
    {
        return valueA.Select(x => x.TimeSpan).SequenceEqual(valueB.Select(x => x.TimeSpan));
    }

    private ObservableCollectionExtended<TimeSpanViewModel> InternalGetter()
    {
        if (_internalGetter == null)
            throw new Exception(@"Internal getter not set");
        return new ObservableCollectionExtended<TimeSpanViewModel>(_internalGetter.Invoke()
            .Select(x => new TimeSpanViewModel(x)));
    }

    private void InternalSetter(ObservableCollectionExtended<TimeSpanViewModel> value)
    {
        if (_internalSetter == null)
            throw new Exception(@"Internal setter not set");
        _internalSetter.Invoke(value.Select(x => x.TimeSpan).ToList());
    }

    private ObservableCollectionExtended<TimeSpanViewModel> InternalDefaultBuilder()
    {
        if (_internalDefaultBuilder == null)
            throw new Exception(@"Internal default builder not set");
        return new ObservableCollectionExtended<TimeSpanViewModel>(_internalDefaultBuilder.Invoke()
            .Select(x => new TimeSpanViewModel(x)));
    }
}

public class TimeSpanViewModel : ReactiveObject
{
    public TimeSpanViewModel(TimeSpan timeSpan)
    {
        _timeSpan = timeSpan;
    }
    
    private TimeSpan _timeSpan;

    public TimeSpan TimeSpan
    {
        get => _timeSpan;
        set => this.RaiseAndSetIfChanged(ref _timeSpan, value);
    }
}
