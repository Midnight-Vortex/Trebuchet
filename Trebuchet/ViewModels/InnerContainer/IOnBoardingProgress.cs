namespace Trebuchet.ViewModels.InnerContainer;

public interface IOnBoardingProgress : IOnBoarding
{
    string Title { get; }
    string Description { get; }
    double CurrentProgress { get; }
    bool IsIndeterminate { get; }
    bool CanCancel { get; }
    System.Windows.Input.ICommand CancelCommand { get; }
}
