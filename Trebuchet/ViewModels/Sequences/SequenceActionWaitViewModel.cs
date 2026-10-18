using System;
using ReactiveUI;
using TrebuchetLib.Sequences;

namespace Trebuchet.ViewModels.Sequences;

[SequenceAction(typeof(SequenceActionWait))]
public class SequenceActionWaitViewModel : SequenceActionViewModel<SequenceActionWaitViewModel, SequenceActionWait>
{
    public SequenceActionWaitViewModel(SequenceActionWait action) : base(action)
    {
        _waitTime = action.WaitTime;
        _skipIfNoPlayers = action.SkipIfNoPlayers;
    }

    private TimeSpan _waitTime;
    private bool _skipIfNoPlayers;

    public TimeSpan WaitTime
    {
        get => _waitTime;
        set => this.RaiseAndSetIfChanged(ref _waitTime, value);
    }

    public bool SkipIfNoPlayers
    {
        get => _skipIfNoPlayers;
        set => this.RaiseAndSetIfChanged(ref _skipIfNoPlayers, value);
    }

    protected override void OnActionChanged()
    {
        Action.WaitTime = WaitTime;
        Action.SkipIfNoPlayers = SkipIfNoPlayers;
        
        base.OnActionChanged();
    }
}