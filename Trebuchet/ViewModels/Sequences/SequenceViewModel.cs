using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DynamicData.Binding;
using ReactiveUI;
using tot_lib;
using Trebuchet.Assets;
using TrebuchetLib.Sequences;

namespace Trebuchet.ViewModels.Sequences;

public class SequenceViewModel : ReactiveObject
{
    public SequenceViewModel(Sequence sequence)
    {
        Sequence = sequence;
        Actions.AddRange(sequence.Actions.Select(MakeActionViewModel));
        ActionTypes.AddRange(GenerateActionTypeList());

        Add = ReactiveCommand.Create(() =>
        {
            PopupOpen = true;
        });

        AddAction = ReactiveCommand.Create<Type>((type) =>
        {
            PopupOpen = false;
            ISequenceAction? action;
            try
            {
                action = Activator.CreateInstance(type) as ISequenceAction;
            }
            catch (Exception)
            {
                // Never let an action type break the editor (the command pipeline would end the app).
                return;
            }
            if (action is null) return;
            Actions.Add(MakeActionViewModel(action));
        });

        Remove = ReactiveCommand.Create<SequenceActionViewModel>((vm) =>
        {
            Actions.Remove(vm);
        });

        Actions.CollectionChanged += (_,_) => OnActionChanged(this, EventArgs.Empty);
    }

    private bool _popupOpen;

    public bool PopupOpen
    {
        get => _popupOpen;
        set => this.RaiseAndSetIfChanged(ref _popupOpen, value);
    }
    
    public ObservableCollectionExtended<SequenceActionViewModel> Actions { get; } = [];
    public List<SequenceActionTypeViewModel> ActionTypes { get; } = [];
    
    public Sequence Sequence { get; }
    
    public ReactiveCommand<Unit, Unit> Add { get; }
    public ReactiveCommand<Type, Unit> AddAction { get; } 
    public ReactiveCommand<SequenceActionViewModel, Unit> Remove { get; }

    public event EventHandler? SequenceChanged;

    public IEnumerable<ISequenceAction> GetSequence()
    {
        return Actions.Select(x => x.SequenceAction);
    }

    private SequenceActionViewModel MakeActionViewModel(ISequenceAction action)
    {
        var viewModel = SequenceActionViewModel.MakeViewModel(action);
        viewModel.ActionChanged += OnActionChanged;
        return viewModel;
    }

    private IEnumerable<SequenceActionTypeViewModel> GenerateActionTypeList()
    {
        // Only actions the editor can show and create: an editor view model ([SequenceAction]) and a public
        // parameterless constructor. Internal actions (e.g. the built-in restart countdown) stay hidden.
        var editable = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeGetTypes)
            .Select(x => x.GetCustomAttribute<SequenceActionAttribute>()?.Target)
            .OfType<Type>()
            .ToHashSet();
        return editable
            .Where(x => typeof(ISequenceAction).IsAssignableFrom(x) && x is { IsPublic: true, IsAbstract: false, IsInterface: false }
                        && x.GetConstructor(Type.EmptyTypes) is not null)
            .Select(x => new SequenceActionTypeViewModel(
                x,
                Resources.ResourceManager.GetString(x.Name, Resources.Culture) ?? x.Name))
            .OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private void OnActionChanged(object? sender, EventArgs args)
    {
        SequenceChanged?.Invoke(this, EventArgs.Empty);
    }
}