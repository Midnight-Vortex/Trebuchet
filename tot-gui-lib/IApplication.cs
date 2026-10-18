using Avalonia.Media;

namespace tot_gui_lib;

public interface IApplication
{
    IImage? AppIconPath { get; }
    bool HasCrashed { get; }

    void Crash();
}