using System;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace tot_gui_lib;

public class AutoScrollBehavior : StyledElementBehavior<Control>
{
    protected override void OnLoaded()
    {
        base.OnLoaded();
        if (AssociatedObject?.DataContext is IScrollController controller)
        {
            controller.ScrollToHome += OnScrollToHome;
            controller.ScrollToEnd += OnScrollToEnd;
        }
    }

    private void OnScrollToHome(object? sender, EventArgs e)
    {
        if(AssociatedObject is ScrollViewer viewer)
            viewer.ScrollToHome();
    }
    
    private void OnScrollToEnd(object? sender, EventArgs e)
    {
        if(AssociatedObject is ScrollViewer viewer)
            viewer.ScrollToEnd();
    }
}