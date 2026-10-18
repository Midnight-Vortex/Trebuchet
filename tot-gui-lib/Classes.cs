using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Xaml.Interactivity;

namespace tot_gui_lib;

public class Classes : AvaloniaObject
{
    public static readonly AttachedProperty<ObservableCollection<string>> ListProperty =
        AvaloniaProperty.RegisterAttached<Classes, Interactive, ObservableCollection<string>>("List");

    static Classes()
    {
        ListProperty.Changed.AddClassHandler<Interactive>(OnClassesChanged);
    }

    private static void OnClassesChanged(Interactive sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.OldValue is ObservableCollection<string> collection)
            collection.CollectionChanged -= OnCollectionChanged;
        if (args.NewValue is ObservableCollection<string> newCollection)
        {
            newCollection.CollectionChanged += OnCollectionChanged;
            sender.Classes.Clear();
            sender.Classes.AddRange(newCollection);
        }
    }


    private static void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is not Interactive interactive) return;
        interactive.Classes.Clear();
        interactive.Classes.AddRange(GetList(interactive));
    }

    public static void SetList(AvaloniaObject element, ObservableCollection<string> value)
    {
        element.SetValue(ListProperty, value);
    }

    public static ObservableCollection<string> GetList(AvaloniaObject element)
    {
        return element.GetValue(ListProperty);
    }
}