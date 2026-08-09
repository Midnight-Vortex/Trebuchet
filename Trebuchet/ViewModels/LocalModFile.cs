using System.Collections.ObjectModel;
using System.IO;
using Humanizer;
using ReactiveUI;
using AppResources = Trebuchet.Assets.Resources;

namespace Trebuchet.ViewModels;

public class LocalModFile : ReactiveObject, IModFile
{
    public LocalModFile(string path)
    {
        IconClasses.Add(@"ModIcon");
        StatusClasses.Add(@"ModStatus");
        FilePath = path;
        Title = Path.GetFileName(path);
        IconClasses.Add(@"Local");
        IconToolTip = AppResources.LocalMod;
        
        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists)
        {
            StatusClasses.Add(@"Found");
            FileSize = fileInfo.Length;
            LastUpdate = @$"{AppResources.Found} - {AppResources.LastModified}: {fileInfo.LastWriteTime.Humanize()} ({FileSize.Bytes().Humanize()})";
        }
        else
        {
            StatusClasses.Add(@"Missing");
            LastUpdate = AppResources.Missing;
            FileSize = 0;
        }
    }
    
    public string Title { get; }
    public ObservableCollection<string> StatusClasses { get; } = [];
    public ObservableCollection<string> IconClasses { get; } = [];
    public string IconToolTip { get; }
    public string LastUpdate { get; }
    public string FilePath { get; }
    public long FileSize { get; }
    public ObservableCollection<ModFileAction> Actions { get; } = [];
    public ModProgressViewModel Progress { get; } = new();
    public string Export()
    {
        return FilePath;
    }
}