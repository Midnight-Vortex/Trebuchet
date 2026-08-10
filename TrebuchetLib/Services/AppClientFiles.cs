using System.Diagnostics.CodeAnalysis;

namespace TrebuchetLib.Services;

public class AppClientFiles(AppSetup appSetup) : IAppClientFiles
{
    private readonly Dictionary<ClientProfileRef, ClientProfile> _cache = [];

    public bool UseSubFolders => true;
    public Dictionary<ClientProfileRef, ClientProfile> Cache => _cache;
    
    public ClientProfileRef Ref(string name)
    {
        return new ClientProfileRef(name, this);
    }
    
    public string GetBaseFolder()
    {
        return Path.Combine(
            appSetup.GetDataDirectory().FullName,
            appSetup.VersionFolder,
            Constants.FolderClientProfiles);
    }
}