using System.Net.Quic;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;

namespace tot_lib;

public class GithubUpdater : IUpdater
{
    public GithubUpdater(string owner, string repo, string contentType)
    {
        Owner = owner;
        Repo = repo;
        ContentType = contentType;
    }
    public const string ReleaseUrl = "https://api.github.com/repos/{0}/{1}/releases";
    
    public const string WindowsMimeType = "application/x-msdownload";
    
    public string Owner { get; }
    public string Repo { get; }
    public string ContentType { get; }

    public bool IsUpToDate { get; private set; } = true;
    public string Name { get; private set; } = string.Empty;
    public AppVersion Version { get; private set; } = AppVersion.Default;
    public string Body { get; private set; } = string.Empty;
    public DateTime Date { get; private set; } = DateTime.MinValue;
    public string Url { get; private set; } = string.Empty;
    public long Size { get; private set; }

    public async Task CheckForUpdates(AppVersion currentVersion)
    {
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(10);

        httpClient.DefaultRequestHeaders.Add($"user-agent", $"TotTrebuchet/{currentVersion}");
        var data = await httpClient.GetStringAsync(string.Format(ReleaseUrl, Owner, Repo));
        var releases = JsonSerializer.Deserialize(data, GithubReleaseListJsonContext.Default.ListGithubRelease);
        if (releases is null || releases.Count == 0) return;
        var latest = releases.FirstOrDefault(x => x.Assets.Any(y => y.ContentType == ContentType));
        if (latest is null) return;

        var onlineVersion = new AppVersion(latest.Name);
        if (onlineVersion.CompareTo(currentVersion) > 0)
        {
            var asset = latest.Assets.First(x => x.ContentType == ContentType);
            IsUpToDate = false;
            Name = latest.Name;
            Body = AggregatePastUpdates(currentVersion, releases);
            Date = latest.PublishedAt;
            Url = asset.BrowserDownloadUrl;
            Size = asset.Size;
            Version = onlineVersion;
        }
        else
        {
            IsUpToDate = true;
            Name = string.Empty;
            Body = string.Empty;
            Date = DateTime.MinValue;
            Url = string.Empty;
            Size = 0;
            Version = AppVersion.Default;
        }
    }

    public async Task<string> DownloadUpdate(IProgress<long>? progress = null)
    {
        if (IsUpToDate) return string.Empty;
        var uri = new Uri(Url);
        var filename = uri.Segments.Last();
        var tmpDir = Path.GetTempPath();
        var file = new FileInfo(Path.Combine(tmpDir, filename));
        if(file.Exists) file.Delete();

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(15);

        using var downloadStream = await httpClient.GetStreamAsync(uri);
        using var fileStream = new FileStream(file.FullName, FileMode.Create, FileAccess.Write, FileShare.None);

        await downloadStream.CopyToAsync(fileStream, 81920, progress);
        await fileStream.FlushAsync();
        fileStream.Close();

        return file.FullName;
    }

    private string AggregatePastUpdates(AppVersion current, List<GithubRelease> releases)
    {
        StringBuilder _aggregator = new();
        foreach (var release in releases)
        {
            var onlineVersion = new AppVersion(release.Name);
            if (onlineVersion.CompareTo(current) > 0)
            {
                _aggregator.AppendLine($"# {release.Name}");
                _aggregator.AppendLine(release.Body);
                _aggregator.AppendLine();
                _aggregator.AppendLine();
            }
            else break;
        }

        return _aggregator.ToString();
    }
}