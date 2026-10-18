namespace tot_lib;

public interface IUpdater
{
    bool IsUpToDate { get; } 
    string Name { get; }
    string Body { get; }
    DateTime Date { get; }
    string Url { get; }
    long Size { get; }
    AppVersion Version { get; }

    Task CheckForUpdates(AppVersion currentVersion);
    Task<string> DownloadUpdate(IProgress<long>? progress = null);
}