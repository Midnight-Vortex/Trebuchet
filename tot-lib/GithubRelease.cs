using System.Text.Json.Serialization;

namespace tot_lib;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GithubRelease))]
public partial class GithubReleaseJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<GithubRelease>))]
public partial class GithubReleaseListJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GithubAsset))]
public partial class GithubAssetJsonContext : JsonSerializerContext
{
}

public class GithubRelease
{
    public string Name { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    [JsonPropertyName("published_at")]
    public DateTime PublishedAt { get; set; }
    public List<GithubAsset> Assets { get; set; } = [];
}

public class GithubAsset
{
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;
    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
}