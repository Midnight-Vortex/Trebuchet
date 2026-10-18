using System.Data;
using Avalonia;
using Avalonia.Controls;

namespace tot_gui_lib;

public class WindowAutoPadding : Window
{
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(!Utils.UseOsChrome() && change.Property.Name == nameof(WindowState))
            Padding = WindowState == WindowState.Maximized ? new Thickness(10) : new Thickness(0);
    }
}