using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
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
            Value.Add(CreateRow(TimeSpan.Zero));
            ValueChanged.Execute(Value).Subscribe();

        });

        Remove = ReactiveCommand.Create<TimeSpanViewModel>((vm) =>
        {
            Value.Remove(vm);
            ValueChanged.Execute(Value).Subscribe();
        });
    }
    
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

    /// <summary>
    /// Every row saves its own changes. Rows are created anew by each refresh (Update) and reset, so the wiring
    /// belongs here: wiring only the first rows lost every edit of an existing time after the first refresh.
    /// </summary>
    private TimeSpanViewModel CreateRow(TimeSpan time)
    {
        var vm = new TimeSpanViewModel(time);
        vm.WhenAnyValue(x => x.TimeSpan).Skip(1).InvokeCommand(Modify);
        return vm;
    }

    protected override bool AreValuesEqual(ObservableCollectionExtended<TimeSpanViewModel> valueA, ObservableCollectionExtended<TimeSpanViewModel> valueB)
    {
        return valueA.Select(x => x.TimeSpan).SequenceEqual(valueB.Select(x => x.TimeSpan));
    }

    private ObservableCollectionExtended<TimeSpanViewModel> InternalGetter()
    {
        if (_internalGetter == null)
            throw new Exception(@"Internal getter not set");
        return new ObservableCollectionExtended<TimeSpanViewModel>(_internalGetter.Invoke().Select(CreateRow));
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
        return new ObservableCollectionExtended<TimeSpanViewModel>(_internalDefaultBuilder.Invoke().Select(CreateRow));
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
