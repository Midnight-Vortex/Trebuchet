using System.Windows.Input;
using Avalonia.Media;

namespace tot_gui_lib
{
    public interface ITag
    {
        SolidColorBrush Color { get; }
        string Name { get; }
    }
}