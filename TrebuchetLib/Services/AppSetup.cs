using System.Diagnostics.CodeAnalysis;
using tot_lib;

namespace TrebuchetLib.Services;

public class AppSetup
{
    public AppSetup(Config config, bool isTestLive, bool enhanced, bool catapult, bool experiment)
    {
        IsTestLive = isTestLive;
        IsEnhanced = enhanced;
        Catapult = catapult;
        Config = config;
        Experiment = experiment;
    }

    public Config Config { get; }
    
    public bool IsTestLive { get; }
    
    public bool IsEnhanced { get; }
    
    public bool Catapult { get; }
    
    public bool Experiment { get; }

    public uint ServerAppId => IsTestLive ? Constants.AppIDTestLiveServer : Constants.AppIDLiveServer;
    
    public string VersionFolder => IsTestLive ? Constants.FolderTestLive : Constants.FolderLive;
    
    public DirectoryInfo GetDataDirectory()
    {
        return typeof(Config).GetStandardFolder(Environment.SpecialFolder.MyDocuments);
    }
    
    public DirectoryInfo GetCommonAppDataDirectory()
    {
        if (TryGetCustomDirectory(Config.DataDirectory, out var dir))
            return dir;
        return GetCommonAppDataDirectoryDefault();
    }

    public static DirectoryInfo GetAppConfigDirectory()
    {
        return typeof(Config).GetStandardFolder(Environment.SpecialFolder.ApplicationData);
    }

    private bool TryGetCustomDirectory(string dirPath, [NotNullWhen(true)]out DirectoryInfo? dir)
    {
        dir = null;
        if (!AppFiles.IsDirectoryValidForData(dirPath)) return false;
        dir = new DirectoryInfo(dirPath);
        return true;
    }

    public static DirectoryInfo GetCommonAppDataDirectoryDefault()
    {
        return typeof(Config).GetStandardFolder(Environment.SpecialFolder.CommonApplicationData);
    }
    
    public string GetServerInstancePath()
    {
        return Path.Combine(
            GetCommonAppDataDirectory().FullName, 
            IsTestLive ? Constants.FolderTestLive : Constants.FolderLive, 
            Constants.FolderServerInstances);
    }
    
    public string GetWorkshopFolder()
    {
        return Path.Combine(
            GetCommonAppDataDirectory().FullName,
            Constants.FolderWorkshop
        );
    }
    
    /// <summary>
    /// Get the path of a server instance.
    /// </summary>
    /// <param name="instance"></param>
    /// <returns></returns>
    public string GetInstancePath(int instance)
    {
        return Path.Combine(
            GetServerInstancePath(),
            string.Format(Constants.FolderInstancePattern, instance));
    }
    
    public string GetBaseInstancePath(DirectoryInfo baseFolder)
    {
        return Path.Combine(
            baseFolder.FullName, 
            VersionFolder, 
            Constants.FolderServerInstances);
    }
    
    public string GetBaseInstancePath()
    {
        return Path.Combine(
            GetCommonAppDataDirectory().FullName, 
            VersionFolder, 
            Constants.FolderServerInstances);
    }

    public string GetBaseInstancePath(bool testlive)
    {
        return Path.Combine(
            GetCommonAppDataDirectory().FullName,
            testlive ? Constants.FolderTestLive : Constants.FolderLive,
            Constants.FolderServerInstances);
    }

    /// <summary>
    /// Get the executable of a server instance.
    /// </summary>
    /// <param name="instance"></param>
    /// <returns></returns>
    public string GetIntanceBinary(int instance)
    {
        return Path.Combine(
            GetCommonAppDataDirectory().FullName, 
            VersionFolder, 
            Constants.FolderServerInstances,
            string.Format(Constants.FolderInstancePattern, instance), 
            Constants.FileServerProxyBin);
    }
    
    public string GetInstanceInternalBinary(int instance)
    {
        return Path.Combine(
            GetCommonAppDataDirectory().FullName, 
            VersionFolder, 
            Constants.FolderServerInstances,
            string.Format(Constants.FolderInstancePattern, instance), 
            Constants.FolderGameBinaries,
            Constants.FileServerBin);
    }
    
    public bool TryGetInstanceIndexFromPath(string path, out int instance)
    {
        instance = -1;
        for (int i = 0; i < Config.ServerInstanceCount; i++)
        {
            var instancePath = Path.GetFullPath(GetInstanceInternalBinary(i));
            if (string.Equals(instancePath, path, StringComparison.Ordinal))
            {
                instance = i;
                return true;
            }
        }
        return false;
    }
    
    public string GetClientFolder()
    {
        return Config.ClientPath;
    }
    
    public string GetPrimaryJunction()
    {
        return Path.Combine(
            GetCommonAppDataDirectory().FullName,
            Constants.GamePrimaryJunction
        );
    }

    public string GetEmptyJunction()
    {
        return Path.Combine(
            GetCommonAppDataDirectory().FullName,
            Constants.GameEmptyJunction
        );
    }
    
    public string GetBinFile(bool battleEye)
    {
        return Path.Combine(GetClientFolder(),
            Constants.FolderGameBinaries,
            battleEye
                ? Constants.FileClientBEBin
                : (IsEnhanced ? Constants.FileClientEnhancedBin : Constants.FileClientBin));
    }
}