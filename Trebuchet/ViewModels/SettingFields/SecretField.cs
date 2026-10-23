using System;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;

namespace Trebuchet.ViewModels.SettingFields;

/// <summary>Where a <see cref="SecretField"/> keeps its secret. Nothing here hands the secret back.</summary>
public interface ISecretStore
{
    /// <summary>A secret is available (stored or from the environment).</summary>
    bool HasSecret { get; }
    /// <summary>The secret comes from an environment variable and is not managed by the field.</summary>
    bool FromEnvironment { get; }
    /// <summary>False when the secret can not be stored on this system (only the environment works).</summary>
    bool CanSave { get; }
    /// <exception cref="ArgumentException">Not a valid secret.</exception>
    /// <exception cref="PlatformNotSupportedException">No safe storage on this system.</exception>
    void Save(string secret);
    void Delete();
}

/// <summary>Why saving a secret failed; the panel logs it. The secret itself is never part of it.</summary>
public sealed class SecretSaveFailedEventArgs(Exception error) : EventArgs
{
    public Exception Error { get; } = error;
}

/// <summary>
/// A write-only secret (the bot token). The text box masks the input and hands it over only when it is saved: the
/// text is never kept in a property, never shown again and never read back from the store. Once saved, the field only
/// says so and offers to replace or remove it.
/// </summary>
public sealed class SecretField : Field<SecretField>, IValueField
{
    private readonly ISecretStore _store;
    private bool _isSet;
    private bool _fromEnvironment;
    private bool _canSave;
    private bool _isEditing;
    private string _error = string.Empty;
    private string _note = string.Empty;

    public SecretField(ISecretStore store)
    {
        _store = store;
        // The value of the field is never shown, so there is nothing to reset to.
        Reset = ReactiveCommand.Create(() => { }, System.Reactive.Linq.Observable.Return(false));
        Update = ReactiveCommand.Create(Refresh);
        Submit = ReactiveCommand.Create<string?>(OnSubmit);
        StartReplace = ReactiveCommand.Create(() =>
        {
            Error = string.Empty;
            IsEditing = true;
        });
        CancelReplace = ReactiveCommand.Create(() =>
        {
            Error = string.Empty;
            IsEditing = !IsSet;
        });
        Remove = ReactiveCommand.CreateFromTask(OnRemove);
        Refresh();
    }

    /// <summary>Asks before the secret is removed. Null: no question.</summary>
    public Func<Task<bool>>? ConfirmRemove { get; set; }

    public string SavedText { get; set; } = string.Empty;
    public string EnvironmentText { get; set; } = string.Empty;
    public string NoStorageText { get; set; } = string.Empty;
    public string InvalidText { get; set; } = string.Empty;
    public string FailedText { get; set; } = string.Empty;
    public string Watermark { get; set; } = string.Empty;

    public event EventHandler? Saved;
    public event EventHandler? Removed;
    public event EventHandler<SecretSaveFailedEventArgs>? SaveFailed;

    public override ReactiveCommand<Unit, Unit> Reset { get; }
    public ReactiveCommand<Unit, Unit> Update { get; }
    /// <summary>Takes the typed text as parameter: it goes to the store and nowhere else.</summary>
    public ReactiveCommand<string?, Unit> Submit { get; }
    public ReactiveCommand<Unit, Unit> StartReplace { get; }
    public ReactiveCommand<Unit, Unit> CancelReplace { get; }
    public ReactiveCommand<Unit, Unit> Remove { get; }

    public bool IsDefault => true;

    public bool IsSet
    {
        get => _isSet;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSet, value);
            this.RaisePropertyChanged(nameof(CanReplace));
        }
    }

    public bool FromEnvironment
    {
        get => _fromEnvironment;
        private set
        {
            this.RaiseAndSetIfChanged(ref _fromEnvironment, value);
            this.RaisePropertyChanged(nameof(CanReplace));
        }
    }

    public bool CanSave
    {
        get => _canSave;
        private set
        {
            this.RaiseAndSetIfChanged(ref _canSave, value);
            this.RaisePropertyChanged(nameof(CanReplace));
        }
    }

    /// <summary>A stored secret can be replaced or removed (not one from the environment).</summary>
    public bool CanReplace => IsSet && !FromEnvironment && CanSave;

    /// <summary>The input box is shown.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set => this.RaiseAndSetIfChanged(ref _isEditing, value);
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

    /// <summary>Where the secret comes from, or why it can not be stored here.</summary>
    public string Note
    {
        get => _note;
        private set
        {
            this.RaiseAndSetIfChanged(ref _note, value);
            this.RaisePropertyChanged(nameof(HasNote));
        }
    }

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public SecretField SetTexts(string saved, string environment, string noStorage, string invalid, string failed, string watermark)
    {
        SavedText = saved;
        EnvironmentText = environment;
        NoStorageText = noStorage;
        InvalidText = invalid;
        FailedText = failed;
        Watermark = watermark;
        Refresh();
        return this;
    }

    public void Refresh()
    {
        IsSet = _store.HasSecret;
        FromEnvironment = _store.FromEnvironment;
        CanSave = _store.CanSave;
        Note = FromEnvironment ? EnvironmentText : CanSave ? string.Empty : NoStorageText;
        // Nothing to type when the environment provides it or nothing can be stored.
        IsEditing = CanSave && !FromEnvironment && (IsEditing || !IsSet);
    }

    private void OnSubmit(string? text)
    {
        Error = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || !CanSave || FromEnvironment) return;
        try
        {
            _store.Save(text);
        }
        catch (ArgumentException)
        {
            Error = InvalidText;
            return;
        }
        catch (PlatformNotSupportedException)
        {
            Error = NoStorageText;
            return;
        }
        catch (Exception ex)
        {
            Error = FailedText;
            SaveFailed?.Invoke(this, new SecretSaveFailedEventArgs(ex));
            return;
        }
        IsEditing = false;
        Refresh();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private async Task OnRemove()
    {
        if (!IsSet || FromEnvironment) return;
        if (ConfirmRemove is not null && !await ConfirmRemove()) return;
        try
        {
            _store.Delete();
        }
        catch (Exception ex)
        {
            Error = FailedText;
            SaveFailed?.Invoke(this, new SecretSaveFailedEventArgs(ex));
            return;
        }
        Error = string.Empty;
        Refresh();
        Removed?.Invoke(this, EventArgs.Empty);
    }
}
