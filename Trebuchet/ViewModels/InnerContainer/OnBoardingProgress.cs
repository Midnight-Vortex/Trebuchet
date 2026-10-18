using System;
using System.Numerics;
using System.Reactive;
using System.Threading;
using System.Reactive.Linq;
using Avalonia.Threading;
using ReactiveUI;

namespace Trebuchet.ViewModels.InnerContainer;

public class OnBoardingProgress<T> : DialogueContent, IProgress<T>, IOnBoardingProgress where T : INumber<T>
{
    public OnBoardingProgress(string title, string description, T initialValue, T maxValue, CancellationTokenSource? cancellation = null)
    {
        Title = title;
        Description = description;
        MaxValue = maxValue;
        _progress = initialValue;
        _canCancel = cancellation is not null;
        CancelCommand = ReactiveCommand.Create(() =>
        {
            if (!CanCancel) return;
            CanCancel = false;
            cancellation?.Cancel();
        });

    }
    
    private T _progress;
    private bool _canCancel;
    public bool CanCancel
    {
        get => _canCancel;
        set => this.RaiseAndSetIfChanged(ref _canCancel, value);
    }
    public System.Windows.Input.ICommand CancelCommand { get; }

    public string Title { get; }
    public string Description { get; }
    public double CurrentProgress => double.CreateChecked(Progress) / double.CreateChecked(MaxValue);
    public T MaxValue { get; }

    // Derived values are raised here: ReactiveUI 25+ cannot back properties of generic types by reflection.
    public T Progress
    {
        get => _progress;
        set
        {
            this.RaiseAndSetIfChanged(ref _progress, value);
            this.RaisePropertyChanged(nameof(IsIndeterminate));
            this.RaisePropertyChanged(nameof(CurrentProgress));
        }
    }

    public bool IsIndeterminate => Progress == T.Zero;

    public void Report(T value)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            Progress = value;
        });
    }
}
