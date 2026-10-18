using System;
using System.Reactive;
using ReactiveUI;

namespace Trebuchet.ViewModels.InnerContainer;

public class ValidatedInputDialogue<T, TD> : TitledDialogue<TD> where TD : ValidatedInputDialogue<T, TD>
{
    protected Func<T?, Validation> Validation;
    private T? _value;
    private string _errorMessage = string.Empty;
    private bool _isValid;

    protected ValidatedInputDialogue(string title, string description) : base(title, description)
    {
        Validation = (_) => Trebuchet.Validation.Valid;
        ConfirmCommand = ReactiveCommand.Create(Close);
        CancelCommand = ReactiveCommand.Create(() =>
        {
            _value = default;
            Cancelled = true;
            Close();
        });

        this.WhenAnyValueUnsafe(x => x.Value)
            .Subscribe((v) =>
            {
                var result = Validation.Invoke(v);
                IsValid = result.IsValid;
                ErrorMessage = result.ErrorMessage;
            });
    }

    public ReactiveCommand<Unit, Unit> ConfirmCommand { get; }

    public T? Value
    {
        get => _value;
        set => this.RaiseAndSetIfChanged(ref _value, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        protected set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public bool IsValid
    {
        get => _isValid;
        protected set => this.RaiseAndSetIfChanged(ref _isValid, value);
    }
    
    public bool Cancelled { get; private set; }

    public ValidatedInputDialogue<T, TD> SetValidation(Func<T?, Validation> validation)
    {
        Validation = validation;
        var result = Validation.Invoke(Value);
        IsValid = result.IsValid;
        ErrorMessage = result.ErrorMessage;
        return this;
    }

    protected virtual T? ProcessValue(T? value)
    {
        return value;
    }
}