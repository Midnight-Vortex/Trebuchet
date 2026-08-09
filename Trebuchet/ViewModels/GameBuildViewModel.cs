using System.Reactive;
using ReactiveUI;

namespace Trebuchet.ViewModels;

public class GameBuildViewModel : ReactiveObject
{
    private readonly App _app;

    public GameBuildViewModel(App app)
    {
        _app = app;
        LiveCommand = ReactiveCommand.Create(OnLiveClicked);
        EnhancedCommand = ReactiveCommand.Create(OnEnhancedClicked);
        TestLiveCommand = ReactiveCommand.Create(OnTestLiveClicked);
    }

    public ReactiveCommand<Unit, Unit> LiveCommand { get; }
    public ReactiveCommand<Unit, Unit> EnhancedCommand { get; }
    public ReactiveCommand<Unit, Unit> TestLiveCommand { get; }
        
    private void OnLiveClicked()
    {
        _app.OpenApp(false, false);
    }

    private void OnEnhancedClicked()
    {
        _app.OpenApp(false, true);
    }
    
    private void OnTestLiveClicked()
    {
        _app.OpenApp(true, false);
    }
}