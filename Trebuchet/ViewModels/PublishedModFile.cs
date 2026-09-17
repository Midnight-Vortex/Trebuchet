using System;
using System.Collections.ObjectModel;
using System.IO;
using ReactiveUI;
using Trebuchet.Assets;

namespace Trebuchet.ViewModels;

public class PublishedModFile : ReactiveObject, IPublishedModFile
{
    public PublishedModFile(ulong publishedId, string? path = null)
    {
        IconClasses.Add(@"ModIcon");
        StatusClasses.Add(@"ModStatus");
        PublishedId = publishedId;
        FilePath = path ?? string.Empty;
        Title = string.IsNullOrEmpty(path) ? publishedId.ToString() : Path.GetFileName(path);
        LastUpdate = File.Exists(path) ? Resources.Loading : Resources.Missing;
        IconClasses.Add(@"Live");
        IconToolTip = Resources.LiveMod;
        FileSize = 0;
        UpdatedAtUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path!) : null;
        StatusClasses.Add(!string.IsNullOrEmpty(path) && File.Exists(path) ? @"Loading" : @"Missing");
    }
    
    public ulong PublishedId { get; }
    public string Title { get; }
    public ObservableCollection<string> StatusClasses { get; } = [];
    public ObservableCollection<string> IconClasses { get; } = [];
    public string IconToolTip { get; }
    public string LastUpdate { get; }
    public string FilePath { get; }
    public long FileSize { get; }
    public DateTime? UpdatedAtUtc { get; }
    public ObservableCollection<ModFileAction> Actions { get; } = [];

    public ModProgressViewModel Progress { get; } = new();
    
    public string Export()
    {
        return PublishedId.ToString();
    }
}
