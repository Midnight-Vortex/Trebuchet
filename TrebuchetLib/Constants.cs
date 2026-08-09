using tot_lib;
using TrebuchetLib.Services;

namespace TrebuchetLib;

public static class Constants
{
    public const uint AppIDLiveClient = 440900;
    public const uint AppIDLiveServer = 443030;
    public const uint AppIDTestLiveClient = 931180;
    public const uint AppIDTestLiveServer = 931580;
    public const string FileBuildID = "buildid";
    public const string FileClientBEBin = "ConanSandbox_BE.exe";
    public const string FileClientBin = "ConanSandbox.exe";
    public const string FileClientBinShipping = "ConanSandbox-Win64-Shipping.exe";
    public const string FileLiveConfig = "settings.live.json";
    public const string FileEnhancedConfig = "settings.enhanced.json";
    public const string FileTestLiveConfig = "settings.testlive.json";
    public const string FileGeneratedModlist = "modlist.txt";
    public const string FileIniBase = "Engine\\Config\\Base{0}.ini";
    public const string FileIniDefault = "ConanSandbox\\Config\\Default{0}.ini";
    public const string FileIniServer = "ConanSandbox\\Saved\\Config\\WindowsServer\\{0}.ini";
    /// <summary>Legacy (UE4) client user INI folder template.</summary>
    public const string FileIniUser = "ConanSandbox\\Saved\\Config\\WindowsNoEditor\\{0}.ini";
    /// <summary>Enhanced (UE5) client user INI folder template.</summary>
    public const string FileIniUserEnhanced = "ConanSandbox\\Saved\\Config\\Windows\\{0}.ini";
    public const string FileMapJson = "maps.json";
    public const string FileStartDateJson = "start-dates.json";
    public const string FileProfileConfig = "profile.json";
    public const string FileServerBin = "ConanSandboxServer-Win64-Shipping.exe";
    public const string FileServerProxyBin = "ConanSandboxServer.exe";
    public const string FileGameLogFile = "ConanSandbox.log";
    public const string FolderClientProfiles = "ClientProfiles";
    public const string FolderGameBinaries = "ConanSandbox\\Binaries\\Win64";
    public const string FolderGameSave = "ConanSandbox\\Saved";
    public const string FolderGameSaveLog = "Logs";
    /// <summary>Enhanced client extracts workshop/pak content here under Saved.</summary>
    public const string FolderExtractedMods = "ExtractedMods";
    public const string FolderConfig = "Config";
    public const string FolderCrashes = "Crashes";
    public const string FolderSaveGames = "SaveGames";
    public const string FolderExilesExtreme = "ExilesExtreme";

    /// <summary>
    /// Under Hybrid Saved (Enhanced + ManageClient), these stay as junctions into the
    /// client profile. ExtractedMods must remain a real directory on the game drive.
    /// </summary>
    public static readonly string[] HybridSavedLinkedDirectories =
    [
        FolderConfig,
        FolderCrashes,
        FolderExilesExtreme,
        FolderGameSaveLog,
        FolderSaveGames
    ];

    public const string FolderInstancePattern = "Instance_{0}";
    public const string FolderLive = "Live";
    public const string FolderEnhanced = "Enhanced";
    public const string FolderModlistProfiles = "Modlists";
    public const string FolderSyncProfiles = "Sync";
    public const string FolderServerInstances = "ServerInstances";
    public const string FolderServerProfiles = "ServerProfiles";
    public const string FolderTestLive = "TestLive";
    public const string FolderWorkshop = "Workshop";
    public const string FolderBackup = "Backups";
    public const string GameArgsLog = "-log";
    public const string GameArgsModList = "-modlist=\"{0}\"";
    public const string GameArgsContinueSession = "--continuesession";
    public const string GameArgsUseAllCore = "-useallavailablecores";
    public const string RegexSavedFolder = @"ConanSandbox([\\/]+)Saved";
    public const string ServerArgsMaxPlayers = "-MaxPlayers={0}";
    public const string ServerArgsMultiHome = "-MULTIHOME={0}";
    public const string SteamWorkshopURL = "https://steamcommunity.com/sharedfiles/filedetails/?id={0}";
    public const string GamePrimaryJunction = "GameSaved";
    public const string GameEmptyJunction = "EmptyGame";
    public const string JsonExt = "json";
    public const string PakExt = "pak";
    public const string TxtExt = "txt";
    
    public const string BoulderExe = "boulder.exe";
    public const string SteamClientExe = "steam.exe";
    
    public const string argLive = "--live";
    public const string argEnhanced = "--enhanced";
    public const string argTestLive = "--testlive";
    public const string argCatapult = "--catapult";
    public const string argExperiment = "--experiment";
    public const string argBoulderSave = "--save";
    public const string argBoulderInstance = "--instance";
    public const string argBoulderModlist = "--modlist";
    public const string argBoulderAutoConnect = "--auto-connect";
    public const string argBoulderBattleEye = "--battle-eye";
    public const string cmdBoulderLamb = "lamb";
    public const string cmdBoulderLambClient = "lamb client";
    public const string cmdBoulderLambServer = "lamb server";
    
    public const string LogFolder = "logs";

    public const string UriScheme = "trebuchet";
    public const string UriSyncHost = "sync";
    public const string UriModListHost = "mods";
    public const string UriClientHost = "clients";
    public const string UriServerHost = "servers";
    
    public static string GetConfigPath(bool testlive)
        => GetConfigPath(testlive ? GameEdition.TestLive : GameEdition.Legacy);

    public static string GetConfigPath(GameEdition edition)
    {
        var folder = AppSetup.GetAppConfigDirectory();
        if(!folder.Exists)
            Directory.CreateDirectory(folder.FullName);
        var file = edition switch
        {
            GameEdition.Enhanced => FileEnhancedConfig,
            GameEdition.TestLive => FileTestLiveConfig,
            _ => FileLiveConfig
        };
        return Path.Combine(folder.FullName, file);
    }

    public static string GetCliArg(GameEdition edition) => edition switch
    {
        GameEdition.Enhanced => argEnhanced,
        GameEdition.TestLive => argTestLive,
        _ => argLive
    };

    public static string GetVersionFolder(GameEdition edition) => edition switch
    {
        GameEdition.Enhanced => FolderEnhanced,
        GameEdition.TestLive => FolderTestLive,
        _ => FolderLive
    };

    public static string GetFileIniUser(GameEdition edition) =>
        edition == GameEdition.Enhanced ? FileIniUserEnhanced : FileIniUser;

    public static string GetClientBin(GameEdition edition) =>
        edition == GameEdition.Enhanced ? FileClientBinShipping : FileClientBin;

    /// <summary>Steam Workshop tag set by the Enhanced modkit uploader.</summary>
    public const string WorkshopTagEnhanced = "Enhanced";
    /// <summary>Steam Workshop tag for Legacy (UE4) mods.</summary>
    public const string WorkshopTagLegacy = "legacy";

    /// <summary>
    /// Tag that must be present for Steam workshop search. Neither edition requires its own
    /// tag (many mods are untagged or only have category tags). Use <see cref="GetWorkshopExcludedTag"/>.
    /// </summary>
    public static string? GetWorkshopRequiredTag(GameEdition edition) => null;

    /// <summary>
    /// Tag that must not be present in workshop search / listing filters.
    /// Legacy excludes Enhanced; Enhanced excludes legacy (symmetric).
    /// </summary>
    public static string? GetWorkshopExcludedTag(GameEdition edition) =>
        edition switch
        {
            GameEdition.Legacy => WorkshopTagEnhanced,
            GameEdition.Enhanced => WorkshopTagLegacy,
            _ => null
        };

    public static string GetEditionDisplayName(GameEdition edition) => edition switch
    {
        GameEdition.Enhanced => "Enhanced",
        GameEdition.TestLive => "Test Live",
        _ => "Legacy"
    };

    /// <summary>
    /// Returns false when the mod is wrong for the edition.
    /// Enhanced rejects only mods with an explicit <see cref="WorkshopTagLegacy"/> tag;
    /// untagged and category-only workshop tags are allowed (Steam often omits Enhanced).
    /// Legacy rejects Enhanced-tagged mods and titles that imply Enhanced/UE5.
    /// </summary>
    public static bool IsWorkshopModCompatible(
        GameEdition edition,
        IReadOnlyList<string>? tags,
        uint consumerAppId,
        string? title = null)
    {
        if (edition == GameEdition.TestLive)
            return consumerAppId == 0 || consumerAppId == AppIDTestLiveClient;

        if (consumerAppId != 0 &&
            consumerAppId != AppIDLiveClient &&
            consumerAppId != AppIDTestLiveClient)
            return false;

        var tagList = tags ?? [];
        var hasEnhanced = tagList.Any(t => string.Equals(t, WorkshopTagEnhanced, StringComparison.OrdinalIgnoreCase));
        var hasLegacy = tagList.Any(t => string.Equals(t, WorkshopTagLegacy, StringComparison.OrdinalIgnoreCase));
        if (!hasEnhanced && TitleImpliesEnhanced(title))
            hasEnhanced = true;

        return edition switch
        {
            GameEdition.Enhanced => !hasLegacy,
            GameEdition.Legacy => !hasEnhanced,
            _ => true
        };
    }

    /// <summary>
    /// True when the workshop title clearly advertises the Enhanced/UE5 build (tag payload missing or incomplete).
    /// </summary>
    public static bool TitleImpliesEnhanced(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var options = System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        // Match "Enhanced" as its own token: "(Enhanced)", "Enhanced Chat", "Tot ! Enhanced Module".
        // Also match UE5 / UE 5 for Legacy-side rejection when tags are absent.
        return System.Text.RegularExpressions.Regex.IsMatch(title, @"\bEnhanced\b", options)
            || System.Text.RegularExpressions.Regex.IsMatch(title, @"\bUE5\b", options)
            || System.Text.RegularExpressions.Regex.IsMatch(title, @"\bUE\s*5\b", options);
    }
    
    public static DirectoryInfo GetLoggingDirectory()
    {
        var folder = AppSetup.GetAppConfigDirectory();
        if(!folder.Exists)
            Directory.CreateDirectory(folder.FullName);
        return new DirectoryInfo(Path.Combine(folder.FullName, LogFolder));
    }
}