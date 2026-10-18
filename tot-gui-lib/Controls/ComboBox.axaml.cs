using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using ReactiveUI.Avalonia;
using ReactiveUI;

namespace tot_gui_lib.Controls;

public class ComboBoxItem(object item, bool selected, int index)
{
    public object Item { get; } = item;
    public bool Selected { get; } = selected;
    public int Index { get; } = index;
}

public partial class ComboBox : ReactiveUserControl<ComboBox>
{

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty
        = AvaloniaProperty.Register<ComboBox, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty
        = AvaloniaProperty.Register<ComboBox, object?>(nameof(SelectedItem), defaultBindingMode:BindingMode.TwoWay);
    
    public static readonly StyledProperty<int> SelectedIndexProperty
        = AvaloniaProperty.Register<ComboBox, int>(nameof(SelectedIndex), -1, defaultBindingMode:BindingMode.TwoWay);
    
    public static readonly StyledProperty<bool> PopupOpenProperty
        = AvaloniaProperty.Register<ComboBox, bool>(nameof(PopupOpen));
    
    public ComboBox()
    {
        SelectItem = ReactiveCommand.Create<ComboBoxItem>((selected) =>
        {
            SelectedItem = selected.Item;
            SelectedIndex = selected.Index;
            PopupOpen = false;
        });
        TogglePopup = ReactiveCommand.Create(OnPopupToggle);
        
        InitializeComponent();
    }

    public ReactiveCommand<ComboBoxItem, Unit> SelectItem { get; } 
    public ReactiveCommand<Unit,Unit> TogglePopup { get; }

    public bool PopupOpen
    {
        get => GetValue(PopupOpenProperty);
        set => SetValue(PopupOpenProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }
    
    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }
    
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    private void OnPopupToggle()
    {
        PopupOpen = !PopupOpen;
        if (PopupOpen)
            RefreshList();
    }
    
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedItemProperty)
        {
            if(SelectedItem is not null)
                ApplySelectionItem(SelectedItem);
        }
        else if (change.Property == SelectedIndexProperty)
        {
            if(SelectedIndex >= 0)
                ApplySelectionIndex(SelectedIndex);
        }
        else if (change.Property == ItemsSourceProperty)
        {
            if(SelectedIndex >= 0)
                ApplySelectionIndex(SelectedIndex);
            else if(SelectedItem is not null)
                ApplySelectionItem(SelectedItem);
        }
    }

    private void ApplySelectionItem(object item)
    {
        var label = this.Find<TextBlock>("SelectedLabel");
        if (label is null) return;
        label.Text = item.ToString() ?? string.Empty;
    }

    private void ApplySelectionIndex(int index)
    {
        var label = this.Find<TextBlock>("SelectedLabel");
        if (label is null) return;
        if (ItemsSource is null)
            label.Text = string.Empty;
        else
        {
            var value = ItemsSource.OfType<object>().ElementAtOrDefault(index);
            label.Text = value?.ToString() ?? string.Empty;
        }
    }

    private void RefreshList()
    {
        var control = this.Find<ItemsControl>("PopupControl");
        if (control is null) return;
        if (ItemsSource is null)
        {
            control.ItemsSource = null;
            return;
        }
        
        int i = 0;
        List<ComboBoxItem> list = [];
        foreach (var o in ItemsSource)
        {
            list.Add(new (o, o.Equals(SelectedItem), i));
            i++;
        }
        control.ItemsSource = list;
    }
}