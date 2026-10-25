namespace TrebuchetLib;

public class PublishedMod(SteamWorksWebAPI.PublishedFile published, UGCFileStatus status)
{
    public UGCFileStatus Status { get; } = status;
    public string Title { get; } = published.Title;
    /// <summary>The picture of the mod on the workshop (a link), empty when Steam sent none.</summary>
    public string PreviewUrl { get; } = published.PreviewUrl;
    public ulong TimeUpdated { get;  } = published.TimeUpdated;
    /// <summary>Steam sent the file of the mod (missing after a Steam error or when it left the workshop).</summary>
    public bool HasWorkshopFile { get; } = ulong.TryParse(published.HcontentFile, out _);
    public ulong PublishedFileId { get;  } = published.PublishedFileID;
    public long FileSize { get;  } = published.FileSize;
    public uint CreatorAppId { get;  } = published.CreatorAppId;
    public uint ConsumerAppId { get; } = published.ConsumerAppId;
    public IReadOnlyList<string> Tags { get; } = published.Tags?
        .Select(t => t.tag)
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .ToList() ?? [];
}
