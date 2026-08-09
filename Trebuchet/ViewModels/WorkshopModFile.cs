using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Humanizer;
using ReactiveUI;
using SteamWorksWebAPI;
using AppResources = Trebuchet.Assets.Resources;
using TrebuchetLib;

namespace Trebuchet.ViewModels;

public class WorkshopModFile : ReactiveObject, IPublishedModFile
{
    public WorkshopModFile(PublishedMod file, UGCFileStatus status, string? path = null)
    {
        IconClasses.Add(@"ModIcon");
        StatusClasses.Add(@"ModStatus");
        FilePath = path ?? string.Empty;
        PublishedId = file.PublishedFileId;
        Title = file.Title;
        AppId = file.ConsumerAppId;
        Tags = file.Tags;
        var updateDate = Tools.UnixTimeStampToDateTime(file.TimeUpdated).ToLocalTime();
        LastDateUpdate = updateDate;
        ApplyEditionIcon(file.ConsumerAppId, file.Tags);
        FileSize = file.FileSize;
        Status = status;
        GetStatusElements(out var label, out var xamlClass);
        LastUpdate = label;
        StatusClasses.Add(xamlClass);
    }
    
    public WorkshopModFile(WorkshopSearchResult file, UGCFileStatus status, string? path = null)
    {
        IconClasses.Add(@"ModIcon");
        StatusClasses.Add(@"ModStatus");
        FilePath = path ?? string.Empty;
        PublishedId = file.PublishedFileId;
        Title = file.Title;
        AppId = file.AppId;
        Tags = file.Tags;
        LastDateUpdate = file.LastUpdate;
        ApplyEditionIcon(file.AppId, file.Tags);
        FileSize = (long)file.Size;
        Status = status;
        GetStatusElements(out var label, out var xamlClass);
        LastUpdate = label;
        StatusClasses.Add(xamlClass);
    }
    
    public WorkshopModFile(WorkshopModFile file, string? path = null)
    {
        IconClasses.Add(@"ModIcon");
        StatusClasses.Add(@"ModStatus");
        FilePath = path ?? string.Empty;
        PublishedId = file.PublishedId;
        Title = file.Title;
        AppId = file.AppId;
        Tags = file.Tags;
        Status = file.Status;
        LastDateUpdate = file.LastDateUpdate;
        ApplyEditionIcon(file.AppId, file.Tags);
        FileSize = file.FileSize;
        GetStatusElements(out var label, out var xamlClass);
        LastUpdate = label;
        StatusClasses.Add(xamlClass);
    }
    
    public UGCFileStatus Status { get; }
    public uint AppId { get; }
    public IReadOnlyList<string> Tags { get; }
    public ulong PublishedId { get; }
    public string Title { get; }
    public string FilePath { get; }
    public long FileSize { get; }
    public DateTime LastDateUpdate { get; }
    public ObservableCollection<string> StatusClasses { get; } = [];
    public ObservableCollection<string> IconClasses { get; } = [];
    public string IconToolTip { get; private set; } = string.Empty;
    public string LastUpdate { get; }
    public ObservableCollection<ModFileAction> Actions { get; } = [];
    public ModProgressViewModel Progress { get; } = new();
    public string Export()
    {
        return PublishedId.ToString();
    }

    private void ApplyEditionIcon(uint appId, IReadOnlyList<string> tags)
    {
        if (appId == Constants.AppIDTestLiveClient)
        {
            IconClasses.Add(@"TestLive");
            IconToolTip = AppResources.TestLiveMod;
            return;
        }

        if (tags.Any(t => string.Equals(t, Constants.WorkshopTagEnhanced, StringComparison.OrdinalIgnoreCase)))
        {
            IconClasses.Add(@"Enhanced");
            IconToolTip = AppResources.EnhancedMod;
            return;
        }

        IconClasses.Add(@"Live");
        IconToolTip = AppResources.LegacyMod;
    }

    private void GetStatusElements(out string label, out string xamlClass)
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
        {
            label = @$"{AppResources.Missing} - {AppResources.LastUpdate}: {LastDateUpdate.Humanize()}";
            xamlClass = @"Missing";
            return;
        }

        switch (Status.Status)
        {
            case UGCStatus.Corrupted:
                label = @$"{AppResources.Corrupted} - {AppResources.LastUpdate}: {LastDateUpdate.Humanize()} ({FileSize.Bytes().Humanize()})";
                xamlClass = @"Missing";
                break;
            case UGCStatus.Updatable:
                label = @$"{AppResources.UpdateAvailable} - {AppResources.LastUpdate}: {LastDateUpdate.Humanize()} ({FileSize.Bytes().Humanize()})";
                xamlClass = @"UpdateAvailable";
                break;
            case UGCStatus.UpToDate:
                label = @$"{AppResources.UpToDate} - {AppResources.LastUpdate}: {LastDateUpdate.Humanize()} ({FileSize.Bytes().Humanize()})";
                xamlClass = @"Up2Date";
                break;
            default:
                label = @$"{AppResources.Missing} - {AppResources.LastUpdate}: {LastDateUpdate.Humanize()} ({FileSize.Bytes().Humanize()})";
                xamlClass = @"Missing";
                break;
        }
    } 
}
