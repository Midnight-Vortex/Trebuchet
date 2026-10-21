using System.Diagnostics.CodeAnalysis;
using tot_lib;

namespace TrebuchetLib.Services;

public class AppSetup
{
    public AppSetup(Config config, GameEdition edition, bool catapult, bool experiment)
    {
        Edition = edition;
        Catapult = catapult;
        Config = config;
        Experiment = experiment;
    }

    public Config Config { get; }

    public GameEdition Edition { get; }

    public bool IsPtc => Edition == GameEdition.EnhancedPtc;

    public bool IsEnhanced => Edition is GameEdition.Enhanced or GameEdition.EnhancedPtc;

    public bool IsLegacy => Edition == GameEdition.Legacy;
    
    public bool Catapult { get; }
    
    public bool Experiment { get; }

    public uint ServerAppId => IsPtc ? Constants.AppIDPtcServer : Constants.AppIDLiveServer;
    
    public string VersionFolder => Constants.GetVersionFolder(Edition);
    
    public DirectoryInfo GetDataDirectory()
    {
        return typeof(Config).GetStandardFolder(Environment.SpecialFolder.MyDocuments);
    }
    
    public DirectoryInfo GetCommonAppDataDirectory()
    {
        var custom = Config.DataDirectory;
        // Validated once per value: the probe (a temporary file) ran on every path lookup, several times a second,
        // and one transient failure silently switched to ProgramData, where the server instances looked missing
        // (fake server update restarts, or downloads of the whole server to drive C).
        if (!string.IsNullOrEmpty(custom) && custom == _validatedDataDirectory)
            return new DirectoryInfo(custom);
        if (TryGetCustomDirectory(custom, out var dir))
        {
            _validatedDataDirectory = custom;
            return dir;
        }
        return GetCommonAppDataDirectoryDefault();
    }

    private volatile string? _validatedDataDirectory;

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
            VersionFolder, 
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

    public string GetBaseInstancePath(GameEdition edition)
    {
        return Path.Combine(
            GetCommonAppDataDirectory().FullName,
            Constants.GetVersionFolder(edition),
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
        // A process that is exiting (e.g. just killed) is reported by WMI without a command line.
        if (string.IsNullOrWhiteSpace(path)) return false;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }
        for (int i = 0; i < Config.ServerInstanceCount; i++)
        {
            var instancePath = Path.GetFullPath(GetInstanceInternalBinary(i));
            if (string.Equals(instancePath, fullPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
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

    /// <summary>Process image name of the game client for this edition (not BattlEye).</summary>
    public string GetClientProcessName() =>
        IsEnhanced ? Constants.FileClientEnhancedBin : Constants.FileClientBin;
}
