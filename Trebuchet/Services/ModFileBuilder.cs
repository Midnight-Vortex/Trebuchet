using System;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using AvaloniaEdit.Utils;
using ReactiveUI;
using Trebuchet.Assets;
using Trebuchet.ViewModels;
using TrebuchetLib;

namespace Trebuchet.Services;

public class ModFileBuilder(IModFile modFile, TaskBlocker blocker)
{
    public ModFileBuilder SetActions(Func<IModFile, Task> remove, Func<IPublishedModFile, Task>? updater = null)
    {
        SetOpenWorkshopAction();
        SetUpdater(updater);
        SetRemove(remove);
        return this;
    }

    public ModFileBuilder SetActions(Func<IPublishedModFile, Task>? updater = null)
    {
        SetOpenWorkshopAction();
        SetUpdater(updater);
        SetRemoveReadOnly();
        return this; 
    }

    public IModFile Build()
    {
        return modFile;
    }

    public ModFileBuilder SetUpdater(Func<IPublishedModFile, Task>? updater)
    {
        if (modFile is not IPublishedModFile published || updater is null)
        {
            modFile.Actions.Add(new ModFileAction(
                Resources.Update,
                "mdi-update",
                ReactiveCommand.Create(() => {}, Observable.Empty<bool>().StartWith(false))
            ));
            return this;
        }
        
        modFile.Actions.Add(new ModFileAction(
            Resources.Update,
            "mdi-update",
            new UpdateModCommand(blocker, () => updater.Invoke(published))
        ));
        return this;
    }
    
    public ModFileBuilder SetRemove(Func<IModFile, Task> remove)
    {
        var action = new ModFileAction(
            Resources.RemoveFromList,
            "mdi-delete",
            ReactiveCommand.CreateFromTask(() => remove.Invoke(modFile)));
        action.Classes.Add(@"Red");
        modFile.Actions.Add(action);
        return this;
    }

    public ModFileBuilder SetRemoveReadOnly()
    {
        modFile.Actions.Add(new ModFileAction(
            Resources.Update,
            "mdi-delete",
            ReactiveCommand.Create(() => {}, Observable.Empty<bool>().StartWith(false))
        ));
        return this;
    }

    public ModFileBuilder SetOpenWorkshopAction()
    {
        if (modFile is not IPublishedModFile published)
        {
            modFile.Actions.Add(new ModFileAction(
                Resources.Update,
                "mdi-steam",
                ReactiveCommand.Create(() => {}, Observable.Empty<bool>().StartWith(false))
            ));
            return this;
        }
        
        published.Actions.Add(new ModFileAction(
            Resources.OpenWorkshopPage,
            "mdi-steam",
            ReactiveCommand.Create(() =>
            {
                tot_lib.Utils.OpenWeb(string.Format(Constants.SteamWorkshopURL, published.PublishedId));
            }))
        );
        return this;
    }

    /// <summary>
    /// Update action of a workshop mod row. It was a ReactiveCommand with
    /// <c>blocker.WhenAnyValue(x => x.CanDownloadMods)</c>: that subscribed to the singleton TaskBlocker for the
    /// command's whole life and the commands are never disposed, so every rebuild of a mod list kept all old rows
    /// (and the list view model they call) alive. This command reads the blocker when asked and only listens to it
    /// while a button is bound to it (a Button removes its CanExecuteChanged handler when the row leaves the view).
    /// </summary>
    private sealed class UpdateModCommand(TaskBlocker blocker, Func<Task> execute) : ICommand
    {
        private EventHandler? _canExecuteChanged;
        private bool _isExecuting;

        public event EventHandler? CanExecuteChanged
        {
            add
            {
                if (_canExecuteChanged is null) blocker.PropertyChanged += OnBlockerChanged;
                _canExecuteChanged += value;
            }
            remove
            {
                _canExecuteChanged -= value;
                if (_canExecuteChanged is null) blocker.PropertyChanged -= OnBlockerChanged;
            }
        }

        public bool CanExecute(object? parameter) => !_isExecuting && blocker.CanDownloadMods;

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            _isExecuting = true;
            RaiseCanExecuteChanged();
            try
            {
                await execute();
            }
            catch (Exception ex)
            {
                // Same handling as an unhandled ReactiveCommand error: logged and shown.
                App.HandleReactiveError(ex);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        private void OnBlockerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(TaskBlocker.CanDownloadMods))
                RaiseCanExecuteChanged();
        }

        private void RaiseCanExecuteChanged()
        {
            // The blocker can change from a background thread; buttons must be updated on the UI thread.
            if (Dispatcher.UIThread.CheckAccess())
                _canExecuteChanged?.Invoke(this, EventArgs.Empty);
            else
                Dispatcher.UIThread.Post(() => _canExecuteChanged?.Invoke(this, EventArgs.Empty));
        }
    }
}
