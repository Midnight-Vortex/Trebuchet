using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ReactiveUI;

namespace Trebuchet.Controls;

public partial class FileDrop : UserControl
{
    public static readonly StyledProperty<object?> ControlContentProperty
        = AvaloniaProperty.Register<FileDrop, object?>(nameof(ControlContent));

    public static readonly StyledProperty<bool> DropPanelVisibleProperty
        = AvaloniaProperty.Register<FileDrop, bool>(nameof(DropPanelVisible), false);
    
    public static readonly StyledProperty<ICommand?> DroppedCommandProperty
        = AvaloniaProperty.Register<FileDrop, ICommand?>(nameof(DroppedCommand));
    
    public FileDrop()
    {
        InitializeComponent();
    }
    
    public object? ControlContent
    {
        get => GetValue(ControlContentProperty);
        set => SetValue(ControlContentProperty, value);
    }
    
    public bool DropPanelVisible
    {
        get => GetValue(DropPanelVisibleProperty);
        set => SetValue(DropPanelVisibleProperty, value);
    }
    
    public ICommand? DroppedCommand
    {
        get => GetValue(DroppedCommandProperty);
        set => SetValue(DroppedCommandProperty, value);
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DropPanelVisible = false;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        if (items is not null &&
            items.All(x => x.Path.IsFile && System.IO.Path.GetExtension(x.Path.LocalPath) == @".pak"))
            DropPanelVisible = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropPanelVisible = false;
        var items = e.DataTransfer.TryGetFiles()?.ToList();
        if (items is null) return;
        
        if (DroppedCommand is null) return;
        if (items.All(x => x.Path.IsFile && System.IO.Path.GetExtension(x.Path.LocalPath) == @".pak"))
        {
            DroppedCommand.Execute(items);
        }
    }


}