using ReactiveUI;
using TrebuchetLib.Sequences;

namespace Trebuchet.ViewModels.Sequences;

[SequenceAction(typeof(SequenceActionBroadcast))]
public class SequenceActionBroadcastViewModel(SequenceActionBroadcast action) :
    SequenceActionViewModel<SequenceActionBroadcastViewModel, SequenceActionBroadcast>(action)
{
    private string _message = action.Message;
    private bool _sendRCon = action.SendRCon;
    private bool _sendDiscord = action.SendDiscord;
    private bool _cancelOnFailure = action.CancelOnFailure;

    public string Message
    {
        get => _message;
        set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public bool SendRCon
    {
        get => _sendRCon;
        set => this.RaiseAndSetIfChanged(ref _sendRCon, value);
    }

    public bool SendDiscord
    {
        get => _sendDiscord;
        set => this.RaiseAndSetIfChanged(ref _sendDiscord, value);
    }

    public bool CancelOnFailure
    {
        get => _cancelOnFailure;
        set => this.RaiseAndSetIfChanged(ref _cancelOnFailure, value);
    }

    protected override void OnActionChanged()
    {
        Action.Message = Message;
        Action.SendRCon = SendRCon;
        Action.SendDiscord = SendDiscord;
        Action.CancelOnFailure = CancelOnFailure;

        base.OnActionChanged();
    }
}
