using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using DynamicData.Binding;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using tot_lib;
using Trebuchet.ViewModels.InnerContainer;
using TrebuchetLib;

namespace Trebuchet.ViewModels;

public class ClientConnectionListViewModel : ReactiveObject
{
    public ClientConnectionListViewModel(ILogger<ClientConnectionListViewModel> logger, DialogueBox dialogueBox)
    {
        _logger = logger;
        _dialogueBox = dialogueBox;
        List.CollectionChanged += (_,_) => OnListChanged();
        AddConnection = ReactiveCommand.Create(() => List.Add(CreateItem(new ClientConnection())));
    }
    private readonly ILogger<ClientConnectionListViewModel> _logger;
    private readonly DialogueBox _dialogueBox;
    private bool _isReadOnly;

    public bool IsReadOnly
    {
        get => _isReadOnly;
    }
    public ObservableCollectionExtended<ClientConnectionViewModel> List { get; } = [];
    public ReactiveCommand<Unit,Unit> AddConnection { get; }

    public AsyncEventHandler? ConnectionListChanged;
    public void SetList(IEnumerable<ClientConnection> connections)
    {
        using (List.SuspendNotifications())
        {
            List.Clear();
            List.AddRange(connections.Select(CreateItem));
        }
    }

    /// <summary>
    /// Every row saves its edits and can be deleted, also a newly added one (its edits were lost and its delete
    /// button did nothing).
    /// </summary>
    private ClientConnectionViewModel CreateItem(ClientConnection connection)
    {
        var vm = new ClientConnectionViewModel(connection, IsReadOnly);
        vm.Deleted += (sender, _) => Remove(sender);
        vm.PropertyChanged += (_,_) => OnListChanged();
        return vm;
    }

    public void SetReadOnly()
    {
        if (_isReadOnly) return;
        _isReadOnly = true;
        foreach (var conn in List)
            conn.IsReadOnly = true;
    }
    
    private Task Remove(object? sender)
    {
        List.Remove((ClientConnectionViewModel)sender!);
        return Task.CompletedTask;
    }
    
    private async void OnListChanged()
    {
        try
        {
            if (ConnectionListChanged is not null)
                await ConnectionListChanged.Invoke(this, EventArgs.Empty);
        }
        catch(Exception ex)
        {
            _logger.LogError(ex, @"Failed to notify list changes");
            await _dialogueBox.OpenErrorAsync(ex.Message);
        }
    }
}