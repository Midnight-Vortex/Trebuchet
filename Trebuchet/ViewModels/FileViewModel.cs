using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using tot_lib;
using TrebuchetLib;

namespace Trebuchet.ViewModels;

public interface IFileViewModel : IReactiveObject
{
    bool IsOver { get; set; }
    bool Selected { get; set; }
    bool IsPopupOpen { get; set; }
    bool DisplayButton { get; }
    bool Exportable { get; }
    string Name { get; }
    ReactiveCommand<Unit, Unit> Select { get; }
    ReactiveCommand<Unit, Unit> OpenFolder { get; }
    ReactiveCommand<Unit, Unit> Rename { get; }
    ReactiveCommand<Unit, Unit> Duplicate { get; }
    ReactiveCommand<Unit, Unit> Export { get; }
    ReactiveCommand<Unit, Unit> Delete { get; }
    ReactiveCommand<Unit,Unit> TogglePopup { get; }
} 

public class FileViewModel<T, TRef> : ReactiveObject, IFileViewModel
    where T : ProfileFile<T>
    where TRef : class,IPRef<T, TRef>
{
    public FileViewModel(TRef reference, bool selected, bool exportable)
    {
        Name = reference.Name;
        Selected = selected;
        Exportable = exportable;
        Reference = reference;

        Select = ReactiveCommand.CreateFromTask(() => OnClicked(FileViewAction.Select));
        OpenFolder = ReactiveCommand.CreateFromTask(() => OnClicked(FileViewAction.OpenFolder));
        Rename = ReactiveCommand.CreateFromTask(() => OnClicked(FileViewAction.Rename));
        Duplicate = ReactiveCommand.CreateFromTask(() => OnClicked(FileViewAction.Duplicate));
        Export = ReactiveCommand.CreateFromTask(() => OnClicked(FileViewAction.Export));
        Delete = ReactiveCommand.CreateFromTask(() => OnClicked(FileViewAction.Delete));
        TogglePopup = ReactiveCommand.Create(() =>
        {
            IsPopupOpen = !IsPopupOpen;
        });

        _displayButton = this.WhenAnyValue(x => x.IsPopupOpen, x => x.IsOver, x => x.Selected, (p, o, s) => p || o || s)
            .ToProperty(this, x => x.DisplayButton);
    }
    private bool _isPopupOpen;
    private bool _isOver;
    private bool _selected;
    private readonly ObservableAsPropertyHelper<bool> _displayButton;

    public ReactiveCommand<Unit, Unit> Select { get; }
    public ReactiveCommand<Unit, Unit> OpenFolder { get; }
    public ReactiveCommand<Unit, Unit> Rename { get; }
    public ReactiveCommand<Unit, Unit> Duplicate { get; }
    public ReactiveCommand<Unit, Unit> Export { get; }
    public ReactiveCommand<Unit, Unit> Delete { get; }
    public ReactiveCommand<Unit,Unit> TogglePopup { get; }

    public string Name { get; }
    
    public TRef Reference { get; }

    public bool Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }
    public bool Exportable { get; }
    
    public bool IsPopupOpen
    {
        get => _isPopupOpen;
        set => this.RaiseAndSetIfChanged(ref _isPopupOpen, value);
    }

    public bool IsOver
    {
        get => _isOver;
        set => this.RaiseAndSetIfChanged(ref _isOver, value);
    }

    public bool DisplayButton => _displayButton.Value;

    public event AsyncEventHandler<FileViewModelEventArgs<T, TRef>>? Clicked;

    protected virtual async Task OnClicked(FileViewAction action)
    {
        IsPopupOpen = false;
        if(Clicked is not null)
            await Clicked.Invoke(this, new FileViewModelEventArgs<T, TRef>(Reference, action));
    }
}
