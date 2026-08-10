namespace TrebuchetLib.Services;

public class AppServerFiles(AppSetup appSetup) : IAppServerFiles
{
    private readonly Dictionary<ServerProfileRef, ServerProfile> _cache = [];

    public bool UseSubFolders => true;
    public Dictionary<ServerProfileRef, ServerProfile> Cache => _cache;
    
    public ServerProfileRef Ref(string name)
    {
        return new ServerProfileRef(name, this);
    }

    public string GetBaseFolder()
    {
        return Path.Combine(
            appSetup.GetDataDirectory().FullName,
            appSetup.VersionFolder,
            Constants.FolderServerProfiles);
    }
}