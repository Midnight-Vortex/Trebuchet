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

    /// <summary>Compatibility ctor for Legacy / TestLive only.</summary>
    public AppSetup(Config config, bool isTestLive, bool catapult, bool experiment)
        : this(config, isTestLive ? GameEdition.TestLive : GameEdition.Legacy, catapult, experiment)
    {
    }

    public Config Config { get; }

    public GameEdition Edition { get; }

    public bool IsTestLive => Edition == GameEdition.TestLive;

    public bool IsEnhanced => Edition == GameEdition.Enhanced;

    public bool IsLegacy => Edition == GameEdition.Legacy;
    
    public bool Catapult { get; }
    
    public bool Experiment { get; }

    public uint ServerAppId => IsTestLive ? Constants.AppIDTestLiveServer : Constants.AppIDLiveServer;
    
    public string VersionFolder => Constants.GetVersionFolder(Edition);
    
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
            VersionFolder, 
            Constants.FolderServerInstances);
    }
    
    public string GetWorkshopFolder()
    {
        if (IsEnhanced)
        {
            return Path.Combine(
                GetCommonAppDataDirectory().FullName,
                VersionFolder,
                Constants.FolderWorkshop
            );
        }

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
        => GetBaseInstancePath(testlive ? GameEdition.TestLive : GameEdition.Legacy);

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

    /// <summary>
    /// Documents edition root (default profile location).
    /// </summary>
    public string GetDocumentsDataRoot()
    {
        return Path.Combine(GetDataDirectory().FullName, VersionFolder);
    }

    /// <summary>
    /// Client data root (ClientProfiles + GameSaved). Enhanced: when the game install is on a
    /// different volume than Documents, co-locate next to the game so whole Saved→GameSaved→profile
    /// stays same-volume (UE5 can create ExtractedMods through the junction).
    /// </summary>
    public string GetClientDataRoot()
    {
        var documentsRoot = GetDocumentsDataRoot();
        if (!IsEnhanced || string.IsNullOrWhiteSpace(Config.ClientPath))
            return documentsRoot;

        try
        {
            var clientFull = Path.GetFullPath(Config.ClientPath);
            if (Tools.IsCrossVolumePath(clientFull, documentsRoot))
            {
                var gameCommon = Path.GetDirectoryName(
                    clientFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(gameCommon))
                    return Path.Combine(gameCommon, Constants.FolderTrebuchetClientData, VersionFolder);
            }
        }
        catch
        {
            // Fall back to Documents.
        }

        return documentsRoot;
    }

    /// <summary>
    /// ServerProfiles root. Enhanced: co-locate with server instances on the DataDirectory volume
    /// when Documents would cross volumes (ExtractedMods through Saved junction).
    /// </summary>
    public string GetServerProfilesBaseFolder()
    {
        var documentsProfiles = Path.Combine(GetDocumentsDataRoot(), Constants.FolderServerProfiles);
        if (!IsEnhanced)
            return documentsProfiles;

        if (!Tools.IsCrossVolumePath(GetServerInstancePath(), documentsProfiles))
            return documentsProfiles;

        return Path.Combine(GetCommonAppDataDirectory().FullName, VersionFolder, Constants.FolderServerProfiles);
    }

    public string GetPrimaryJunction()
    {
        if (IsEnhanced)
        {
            // Keep GameSaved on the same volume as client profiles (and the game when co-located).
            return Path.Combine(GetClientDataRoot(), Constants.GamePrimaryJunction);
        }

        return Path.Combine(
            GetCommonAppDataDirectory().FullName,
            Constants.GamePrimaryJunction
        );
    }

    public string GetEmptyJunction()
    {
        if (IsEnhanced)
        {
            return Path.Combine(GetClientDataRoot(), Constants.GameEmptyJunction);
        }

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
