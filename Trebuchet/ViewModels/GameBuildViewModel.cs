using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using TrebuchetLib;

namespace Trebuchet.ViewModels;

public class GameBuildViewModel : ReactiveObject
{
    private readonly App _app;
    private bool _isOpening;

    public GameBuildViewModel(App app)
    {
        _app = app;
        LiveCommand = ReactiveCommand.CreateFromTask(OnLiveClicked);
        EnhancedCommand = ReactiveCommand.CreateFromTask(OnEnhancedClicked);
        TestLiveCommand = ReactiveCommand.CreateFromTask(OnTestLiveClicked);
    }

    /// <summary>Legacy build (former Live).</summary>
    public ReactiveCommand<Unit, Unit> LiveCommand { get; }
    public ReactiveCommand<Unit, Unit> EnhancedCommand { get; }
    public ReactiveCommand<Unit, Unit> TestLiveCommand { get; }

    public bool IsOpening
    {
        get => _isOpening;
        private set => this.RaiseAndSetIfChanged(ref _isOpening, value);
    }
        
    private async Task OnLiveClicked()
    {
        await OpenEditionAsync(GameEdition.Legacy);
    }

    private async Task OnEnhancedClicked()
    {
        await OpenEditionAsync(GameEdition.Enhanced);
    }

    private async Task OnTestLiveClicked()
    {
        await OpenEditionAsync(GameEdition.TestLive);
    }

    private async Task OpenEditionAsync(GameEdition edition)
    {
        if (IsOpening) return;
        IsOpening = true;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await _app.OpenAppAsync(edition);
    }
}
