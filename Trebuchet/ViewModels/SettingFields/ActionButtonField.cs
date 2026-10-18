using System;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using Trebuchet.Assets;

namespace Trebuchet.ViewModels.SettingFields;

/// <summary>A button that runs an action and shows its result text below (e.g. sending a test message).</summary>
public class ActionButtonField : DescriptiveElement<ActionButtonField>
{
    private string _status = string.Empty;
    private bool _isVisible = true;

    public ActionButtonField(string buttonLabel, string icon, Func<Task<string>> action)
    {
        ButtonLabel = buttonLabel;
        Icon = icon;
        Command = ReactiveCommand.CreateFromTask(async () =>
        {
            Status = Resources.ActionRunning;
            try
            {
                Status = await action();
            }
            catch (Exception ex)
            {
                Status = ex.Message;
            }
        });
    }

    public string ButtonLabel { get; }
    public string Icon { get; }
    public ReactiveCommand<Unit, Unit> Command { get; }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }
}
