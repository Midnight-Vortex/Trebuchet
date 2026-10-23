using Avalonia.Interactivity;
using tot_gui_lib;

namespace Trebuchet.Windows;

/// <summary>The step by step setup guide of the Discord bot, opened from the panel. Shows texts only.</summary>
public partial class DiscordBotHelpWindow : WindowAutoPadding
{
    public DiscordBotHelpWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
