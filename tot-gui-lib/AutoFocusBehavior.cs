using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Xaml.Interactivity;

namespace tot_gui_lib;

public class AutoFocusBehavior : StyledElementBehavior<Control>
{
    protected override void OnLoaded()
    {
        base.OnLoaded();
        if (AssociatedObject is TextBox box)
        {
            box.Focus(NavigationMethod.Pointer);
            box.SelectAll();
        }
        else
            AssociatedObject?.Focus();
    }
}