using System.Web;
using SteamWorksWebAPI;
using SteamWorksWebAPI.Interfaces;

namespace TrebuchetLib.Services;

public class AppSyncFiles(AppSetup setup) : IAppSyncFiles
{
    private readonly Dictionary<SyncProfileRef, SyncProfile> _cache = [];

    public bool UseSubFolders => false;
    public Dictionary<SyncProfileRef, SyncProfile> Cache => _cache;
    public SyncProfileRef Ref(string name)
    {
        return new SyncProfileRef(name, this);
    }

    public string GetBaseFolder()
    {
        return Path.Combine(
            setup.GetDataDirectory().FullName, 
            setup.VersionFolder, 
            Constants.FolderSyncProfiles);
    }

    private bool ResolveMod(uint appId, ref string mod)
    {
        string file = mod;
        if (long.TryParse(mod, out _))
            file = Path.Combine(setup.GetWorkshopFolder(), appId.ToString(), mod, "none");

        string? folder = Path.GetDirectoryName(file);
        if (folder == null)
            return false;

        if (!long.TryParse(Path.GetFileName(folder), out _))
            return File.Exists(file);

        if (!Directory.Exists(folder))
            return false;

        string[] files = Directory.GetFiles(folder, "*.pak", SearchOption.TopDirectoryOnly);
        if (files.Length > 0)
        {
            mod = files[0];
            return true;
        }

        var hasIoStore = Directory.EnumerateFiles(folder, "*.ucas", SearchOption.TopDirectoryOnly).Any()
                         && Directory.EnumerateFiles(folder, "*.utoc", SearchOption.TopDirectoryOnly).Any();
        if (!hasIoStore)
            return false;

        var any = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (any is null)
            return false;
        mod = any;
        return true;
    }
    
    public bool ResolveMod(ref string mod)
    {
        try
        {
            if (ResolveMod(Constants.AppIDLiveClient, ref mod))
                return true;
            if (ResolveMod(Constants.AppIDTestLiveClient, ref mod))
                return true;
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    public IEnumerable<string> GetResolvedModlist(IEnumerable<string> mods, bool throwIfFailed = true)
    {
        foreach (string mod in mods)
        {
            string path = mod;
            if (!ResolveMod(ref path))
                if (throwIfFailed)
                    throw new Exception($"Could not resolve mod {path}.");
                else yield return mod;
            else
                yield return path;
        }
    }

    public async Task Sync(SyncProfileRef name)
    {
        var profile = this.Get(name);
        if (string.IsNullOrWhiteSpace(profile.SyncURL))
            throw new Exception("Url is invalid");
        
        UriBuilder builder = new UriBuilder(profile.SyncURL);

        if (SteamWorks.SteamCommunityHost == builder.Host)
        {
            profile.Modlist = await SyncSteamCollection(builder);
            profile.SaveFile();
        }
        else
            await SyncJson(builder, name);
    }
    
    private async Task SyncJson(UriBuilder builder, SyncProfileRef name)
    {
        var result = await Tools.DownloadModList(builder.ToString(), CancellationToken.None);
        await this.Import(result, name);
    }

    private async Task<List<string>> SyncSteamCollection(UriBuilder builder)
    {
        var query = HttpUtility.ParseQueryString(builder.Query);
        var id = query.Get(@"id");
        if (id == null || !ulong.TryParse(id, out var collectionId))
            throw new Exception("Steam Collection URL is invalid");

        var result = await SteamRemoteStorage.GetCollectionDetails(
            new GetCollectionDetailsQuery(collectionId), CancellationToken.None);

        return result.CollectionDetails
            .First()
            .Children.Select(x => x.PublishedFileId).ToList();
    }
}