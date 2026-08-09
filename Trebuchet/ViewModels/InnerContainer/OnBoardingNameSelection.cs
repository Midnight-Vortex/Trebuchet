using ReactiveUI;

namespace Trebuchet.ViewModels.InnerContainer;

public class OnBoardingNameSelection(string title, string description)
    : ValidatedInputDialogue<string, OnBoardingNameSelection>(title, description)
{
    protected override string ProcessValue(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private string _placeholderText = string.Empty;

    public string PlaceholderText
    {
        get => _placeholderText;
        set => this.RaiseAndSetIfChanged(ref _placeholderText, value);
    }
}