namespace TrebuchetLib;

public class PublishedMod(SteamWorksWebAPI.PublishedFile published, UGCFileStatus status)
{
    public UGCFileStatus Status { get; } = status;
    public string Title { get; } = published.Title;
    public ulong TimeUpdated { get;  } = published.TimeUpdated;
    public ulong PublishedFileId { get;  } = published.PublishedFileID;
    public long FileSize { get;  } = published.FileSize;
    public uint CreatorAppId { get;  } = published.CreatorAppId;
    public uint ConsumerAppId { get; } = published.ConsumerAppId;
    public IReadOnlyList<string> Tags { get; } = published.Tags?
        .Select(t => t.tag)
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .ToList() ?? [];
}
