using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using DynamicData.Binding;
using tot_lib;

namespace tot_gui_lib;

/// <summary>
/// 
/// </summary>
public class ItemDragBehavior : StyledElementBehavior<Control>
{
    private bool _enableDrag;
    private bool _dragStarted;
    private Point _start;
    private int _draggedIndex;
    private int _targetIndex;
    private ItemsControl? _itemsControl;
    private Control? _draggedContainer;
    private bool _captured;
    private Vector _scrollOffset;
    private Point _lastPosition;

    /// <summary>
    /// 
    /// </summary>
    public static readonly StyledProperty<Orientation> OrientationProperty = 
        AvaloniaProperty.Register<ItemDragBehavior, Orientation>(nameof(Orientation));

    /// <summary>
    /// 
    /// </summary>
    public static readonly StyledProperty<double> HorizontalDragThresholdProperty = 
        AvaloniaProperty.Register<ItemDragBehavior, double>(nameof(HorizontalDragThreshold), 3);

    /// <summary>
    /// 
    /// </summary>
    public static readonly StyledProperty<double> VerticalDragThresholdProperty =
        AvaloniaProperty.Register<ItemDragBehavior, double>(nameof(VerticalDragThreshold), 3);

    /// <summary>
    /// 
    /// </summary>
    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>
    /// 
    /// </summary>
    public double HorizontalDragThreshold
    {
        get => GetValue(HorizontalDragThresholdProperty);
        set => SetValue(HorizontalDragThresholdProperty, value);
    }

    /// <summary>
    /// 
    /// </summary>
    public double VerticalDragThreshold
    {
        get => GetValue(VerticalDragThresholdProperty);
        set => SetValue(VerticalDragThresholdProperty, value);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree()
    {
        if (AssociatedObject is not null)
        {
            AssociatedObject.AddHandler(InputElement.PointerReleasedEvent, PointerReleased, RoutingStrategies.Tunnel);
            AssociatedObject.AddHandler(InputElement.PointerPressedEvent, PointerPressed, RoutingStrategies.Tunnel);
            AssociatedObject.AddHandler(InputElement.PointerMovedEvent, PointerMoved, RoutingStrategies.Tunnel);
            AssociatedObject.AddHandler(InputElement.PointerCaptureLostEvent, PointerCaptureLost, RoutingStrategies.Tunnel);
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree()
    {
        if (AssociatedObject is not null)
        {
            AssociatedObject.RemoveHandler(InputElement.PointerReleasedEvent, PointerReleased);
            AssociatedObject.RemoveHandler(InputElement.PointerPressedEvent, PointerPressed);
            AssociatedObject.RemoveHandler(InputElement.PointerMovedEvent, PointerMoved);
            AssociatedObject.RemoveHandler(InputElement.PointerCaptureLostEvent, PointerCaptureLost);
        }
    }

    private void PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Don't start row-drag from action buttons (update/trash/steam).
        if (e.Source is Visual source
            && source.TryGetParent<Button>(out var button)
            && !ReferenceEquals(button, AssociatedObject))
            return;

        var properties = e.GetCurrentPoint(AssociatedObject).Properties;
        if (!properties.IsLeftButtonPressed || AssociatedObject is null)
            return;

        // Prefer ListBox via visual ancestors — ListBoxItem.Parent is often VirtualizingStackPanel.
        ItemsControl? itemsControl = AssociatedObject.FindAncestorOfType<ListBox>()
            ?? (AssociatedObject.TryGetParent<ItemsControl>(out var parentItems) ? parentItems : null);
        if (itemsControl is null)
            return;

        _enableDrag = true;
        _dragStarted = false;
        _start = e.GetPosition(itemsControl);
        _draggedIndex = -1;
        _targetIndex = -1;
        _itemsControl = itemsControl;
        _draggedContainer = AssociatedObject;

        if (TryGetScroller(out var scrollable))
        {
            _scrollOffset = scrollable.Offset;
            scrollable.ScrollChanged += OnScrollChanged;
        }

        if (_draggedContainer is not null)
        {
            SetDraggingPseudoClasses(_draggedContainer, true);
        }

        AddTransforms(_itemsControl);

        // Do not Pointer.Capture here — ListBox selection steals capture and fires
        // CaptureLost, which aborts drag before any move (matches upstream ItemDragBehavior).
        _captured = true;
    }

    private bool TryGetScroller([NotNullWhen(true)]out ScrollViewer? scrollViewer)
    {
        scrollViewer = null;
        if (_itemsControl is null) return false;
        var attr = _itemsControl.GetType().GetCustomAttributes(typeof(TemplatePartAttribute), true)
            .OfType<TemplatePartAttribute>().FirstOrDefault(x => x.Type == typeof(IScrollable));
        var nameScope = _itemsControl.FindNameScope();
        if (attr is not null && _itemsControl.TryFindChild<ScrollViewer>(attr.Name, out scrollViewer))
            return true;

        if (!_itemsControl.TryGetParent<ScrollViewer>(out var scrollParent)) return false;
        scrollViewer = scrollParent;
        return true;

    }

    private void PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_captured)
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                Released();
            }

            _captured = false;
        }
    }

    private void PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        Released();
        _captured = false;
    }

    private void Released()
    {
        if (!_enableDrag)
        {
            return;
        }

        RemoveTransforms(_itemsControl);

        if (_itemsControl is not null)
        {
            foreach (var control in _itemsControl.GetRealizedContainers())
            {
                SetDraggingPseudoClasses(control, true);
            }
        }
        
        if (TryGetScroller(out var scrollable))
        {
            scrollable.ScrollChanged -= OnScrollChanged;
        }

        if (_dragStarted)
        {
            if (_draggedIndex >= 0 && _targetIndex >= 0 && _draggedIndex != _targetIndex)
            {
                MoveDraggedItem(_itemsControl, _draggedIndex, _targetIndex);
            }
        }

        if (_itemsControl is not null)
        {
            foreach (var control in _itemsControl.GetRealizedContainers())
            {
                SetDraggingPseudoClasses(control, false);
            }
        }

        if (_draggedContainer is not null)
        {
            SetDraggingPseudoClasses(_draggedContainer, false);
        }

        _draggedIndex = -1;
        _targetIndex = -1;
        _enableDrag = false;
        _dragStarted = false;
        _itemsControl = null;

        _draggedContainer = null;
    }

    private void AddTransforms(ItemsControl? itemsControl)
    {
        if (itemsControl?.Items is null)
        {
            return;
        }

        var i = 0;

        foreach (var _ in itemsControl.Items)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container is not null)
            {
                SetTranslateTransform(container, 0, 0);
            }
  
            i++;
        }  
    }

    private void RemoveTransforms(ItemsControl? itemsControl)
    {
        if (itemsControl?.Items is null)
        {
            return;
        }

        var i = 0;

        foreach (var _ in itemsControl.Items)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container is not null)
            {
                //SetTranslateTransform(container, 0, 0);
                container.RenderTransform = null;
            }
  
            i++;
        }  
    }
    
    private void MoveDraggedItem(ItemsControl? itemsControl, int draggedIndex, int targetIndex)
    {
        if (itemsControl is ListBox
            {
                SelectionMode: SelectionMode.Multiple,
                ItemsSource: IList listSource,
                SelectedItems.Count: > 1
            } listBox)
        {
            IDisposable? disposable = null;
            if (itemsControl.ItemsSource!.Cast<object?>() is ObservableCollectionExtended<object?> observable)
                disposable = observable.SuspendNotifications();
            
            var i = 0;
            if (draggedIndex < targetIndex) targetIndex++;
            var indexes = listBox.Selection.SelectedIndexes.ToList();
            if(!indexes.Contains(draggedIndex)) indexes.Add(draggedIndex);
            indexes.Sort();
            var list = indexes.Select(x => listSource[x]).ToList();
            foreach(var item in list)
            {
                var tIndex = targetIndex + i;
                if (listSource.IndexOf(item) < tIndex)
                    tIndex--;
                else
                    i++;
                listSource.Remove(item);
                if (listSource.Count <= tIndex)
                    listSource.Add(item);
                else
                    listSource.Insert(tIndex, item);

            }

            if (list.Count == 1)
                listBox.SelectedIndex = listSource.IndexOf(list.First());
            else
                listBox.Selection.SelectRange(listSource.IndexOf(list.First()), listSource.IndexOf(list.Last()));
            disposable?.Dispose();
        }
        else if (itemsControl?.ItemsSource is IList itemsSource)
        {
            var draggedItem = itemsSource[draggedIndex];
            itemsSource.RemoveAt(draggedIndex);
            itemsSource.Insert(targetIndex, draggedItem);

            if (itemsControl is SelectingItemsControl selectingItemsControl)
            {
                selectingItemsControl.SelectedIndex = targetIndex;
            }
        }
        else
        {
            if (itemsControl?.Items is {IsReadOnly: false} itemCollection)
            {
                var draggedItem = itemCollection[draggedIndex];
                itemCollection.RemoveAt(draggedIndex);
                itemCollection.Insert(targetIndex, draggedItem);

                if (itemsControl is SelectingItemsControl selectingItemsControl)
                {
                    selectingItemsControl.SelectedIndex = targetIndex;
                } 
            }
        }
    }
    
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        MoveItems();
    }

    private void PointerMoved(object? sender, PointerEventArgs e)
    {
        var properties = e.GetCurrentPoint(AssociatedObject).Properties;
        _lastPosition = e.GetPosition(_itemsControl);
        if (!_captured || !properties.IsLeftButtonPressed) return;

        if (_draggedContainer is not null && _draggedContainer.RenderTransform is null)
            SetTranslateTransform(_draggedContainer, 0, 0);

        MoveItems();
        if (_dragStarted)
            e.Handled = true;
    }

    private void MoveItems()
    {
        if (_itemsControl?.Items is null || _draggedContainer is null || !_enableDrag)
        {
            return;
        }

        if (_draggedContainer.RenderTransform is null)
            SetTranslateTransform(_draggedContainer, 0, 0);

        Vector offset = Vector.Zero;
        if (TryGetScroller(out var scrollable))
        {
            offset = scrollable.Offset - _scrollOffset;
        }

        var orientation = Orientation;
        
        var delta = orientation == Orientation.Horizontal ? _lastPosition.X - _start.X + offset.X : _lastPosition.Y - _start.Y + offset.Y;

        if (!_dragStarted)
        {
            var diff = _start - _lastPosition;
            var horizontalDragThreshold = HorizontalDragThreshold;
            var verticalDragThreshold = VerticalDragThreshold;

            if (orientation == Orientation.Horizontal)
            {
                if (Math.Abs(diff.X) > horizontalDragThreshold)
                {
                    _dragStarted = true;
                }
                else
                {
                    return;
                }
            }
            else
            {
                if (Math.Abs(diff.Y) > verticalDragThreshold)
                {
                    _dragStarted = true;
                }
                else
                {
                    return;
                }
            }
        }

        if (orientation == Orientation.Horizontal)
        {
            SetTranslateTransform(_draggedContainer, delta, 0);
        }
        else
        {
            SetTranslateTransform(_draggedContainer, 0, delta);
        }

        _draggedIndex = _itemsControl.IndexFromContainer(_draggedContainer);
        _targetIndex = -1;

        var draggedBounds = _draggedContainer.Bounds;

        var draggedStart = orientation == Orientation.Horizontal ? draggedBounds.X : draggedBounds.Y;

        var draggedDeltaStart = orientation == Orientation.Horizontal
            ? draggedBounds.X + delta
            : draggedBounds.Y + delta;

        var draggedDeltaEnd = orientation == Orientation.Horizontal
            ? draggedBounds.X + delta + draggedBounds.Width
            : draggedBounds.Y + delta + draggedBounds.Height;

        var i = 0;

        foreach (var _ in _itemsControl.Items)
        {
            var targetContainer = _itemsControl.ContainerFromIndex(i);
            if (targetContainer?.RenderTransform is null || ReferenceEquals(targetContainer, _draggedContainer))
            {
                i++;
                continue;
            }

            var targetBounds = targetContainer.Bounds;

            var targetStart = orientation == Orientation.Horizontal ? targetBounds.X : targetBounds.Y;

            var targetMid = orientation == Orientation.Horizontal
                ? targetBounds.X + targetBounds.Width / 2
                : targetBounds.Y + targetBounds.Height / 2;

            var targetIndex = _itemsControl.IndexFromContainer(targetContainer);

            if (targetStart > draggedStart && draggedDeltaEnd >= targetMid)
            {
                if (orientation == Orientation.Horizontal)
                {
                    SetTranslateTransform(targetContainer, -draggedBounds.Width, 0);
                }
                else
                {
                    SetTranslateTransform(targetContainer, 0, -draggedBounds.Height);
                }

                _targetIndex = _targetIndex == -1 ? targetIndex :
                    targetIndex > _targetIndex ? targetIndex : _targetIndex;
            }
            else if (targetStart < draggedStart && draggedDeltaStart <= targetMid)
            {
                if (orientation == Orientation.Horizontal)
                {
                    SetTranslateTransform(targetContainer, draggedBounds.Width, 0);
                }
                else
                {
                    SetTranslateTransform(targetContainer, 0, draggedBounds.Height);
                }

                _targetIndex = _targetIndex == -1 ? targetIndex :
                    targetIndex < _targetIndex ? targetIndex : _targetIndex;
            }
            else
            {
                if (orientation == Orientation.Horizontal)
                {
                    SetTranslateTransform(targetContainer, 0, 0);
                }
                else
                {
                    SetTranslateTransform(targetContainer, 0, 0);
                }
            }

            i++;
        }
    }

    private void SetDraggingPseudoClasses(Control control, bool isDragging)
    {
        if (isDragging)
        {
            ((IPseudoClasses)control.Classes).Add(":dragging");
        }
        else
        {
            ((IPseudoClasses)control.Classes).Remove(":dragging");
        }
    }

    private void SetTranslateTransform(Control control, double x, double y)
    {
        var transformBuilder = new TransformOperations.Builder(1);
        transformBuilder.AppendTranslate(x, y);
        control.RenderTransform = transformBuilder.Build();
    }
}
