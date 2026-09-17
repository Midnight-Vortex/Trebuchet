using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Controls;
using ReactiveUI;
using Trebuchet.ViewModels.Panels;

namespace Trebuchet.ViewModels;

public delegate Task IModFileArgs(IModFile file);
public interface IModFile
{
    string Title { get; }
    ObservableCollection<string> StatusClasses { get; }
    ObservableCollection<string> IconClasses { get; }
    string IconToolTip { get; }
    string LastUpdate { get; }
    string FilePath { get; }
    long FileSize { get; }
    DateTime? UpdatedAtUtc { get; }
    ObservableCollection<ModFileAction> Actions { get; }
    string Export();
    
    ModProgressViewModel Progress { get; }
}
