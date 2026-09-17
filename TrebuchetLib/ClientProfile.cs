using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Serialization;
using Yuu.Ini;

namespace TrebuchetLib
{
    public sealed class ClientProfile : ProfileFile<ClientProfile>
    {
        public int AddedTexturePool { get; set; } = AddedTexturePoolDefault;
        public bool BackgroundSound { get; set; } = BackgroundSoundDefault;
        public bool EnableConsoleOnHotkey { get; set; } = EnableConsoleOnHotkeyDefault;
        public string ConsoleHotkey { get; set; } = ConsoleHotkeyDefault;
        public int ConfiguredInternetSpeed { get; set; } = ConfiguredInternetSpeedDefault;
        public long CPUThreadAffinity { get; set; } = CPUThreadAffinityDefault;
        public bool EnableAsyncScene { get; set; } = EnableAsyncSceneDefault;
        public bool Log { get; set; } = LogDefault;
        public List<string> LogFilters { get; set; } = LogFiltersDefault;
        public int ProcessPriority { get; set; } = ProcessPriorityDefault;
        public bool RemoveIntroVideo { get; set; } = RemoveIntroVideoDefault;
        public bool TotAdminDoNotLoadServerList { get; set; } = TotAdminDoNotLoadServerListDefault;
        public bool UltraAnisotropy { get; set; } = UltraAnisotropyDefault;
        public bool UseAllCores { get; set; } = UseAllCoresDefault;
        public List<ClientConnection> ClientConnections { get; set; } = [];
        
        public static readonly long CPUThreadAffinityDefault = 0xffffffffffff;
        public static readonly int ConfiguredInternetSpeedDefault = 25000; 
        public static readonly bool BackgroundSoundDefault = false;
        public static readonly bool EnableConsoleOnHotkeyDefault = false;
        public const string ConsoleHotkeyDefault = "Insert";
        public static readonly int AddedTexturePoolDefault = 0;
        public static readonly bool EnableAsyncSceneDefault = false;
        public static readonly bool LogDefault = false;
        public static readonly int ProcessPriorityDefault = 0;
        public static readonly bool RemoveIntroVideoDefault = false;
        public static readonly bool TotAdminDoNotLoadServerListDefault = false;
        public static readonly bool UltraAnisotropyDefault = false;
        public static readonly bool UseAllCoresDefault = false;
        public static readonly List<string> LogFiltersDefault =
        [
            "LogSkinnedMeshComp=NoLogging",
            "NPC=Error",
            "LogLevelActorContainer=NoLogging",
            "LogSkeletalMesh=NoLogging",
            "LogServerStats=NoLogging",
            "LogDataTable=Error",
            "Gamecode_Building=Error",
            "Gamecode_Items=Error",
            "Gamecode_AI=Error",
            "Gamecode_Combat=Error",
            "Gamecode_NPC=Error",
            "Gamecode_Effects=Error",
            "Network=Error",
            "SmokeTest=NoLogging",
            "LogCook=Error",
            "LogSavePackage=Error",
            "LogPackageDependencyInfo=Error",
            "LogTexture=Error",
            "LogStreaming=Error",
            "LogGameMode=Error",
            "HeatmapMetrics=Error",
            "LogUObjectGlobals=Error",
            "AI=Error",
            "ItemInventory=Critical",
            "LogScript=Error",
            "LogNetPackageMap=Error",
            "LogCharacterMovement=Error",
            "LogAnimMontage=Error",
            "Combat=Error",
            "LogStreaming=Critical",
            "LogModController=Error",
            "LogPhysics=Error",
            "Persistence=Error",
            "LogAnimation=Error",
            "SpawnTable=Critical",
            "LogPrimitiveComponent=Error",
            "building=Critical",
            "ConanSandbox=NoLogging",
            "LogScriptCore=Error"
        ];
        
        [JsonIgnore]
        public string ProfileFolder => Path.GetDirectoryName(FilePath) ?? throw new Exception($"Invalid directory for {FilePath}.");

        [JsonIgnore]
        public string ProfileName => Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? throw new Exception($"Invalid directory for {FilePath}.");
        
        public string GetClientArgs(string modlistPath, bool autoConnect, GameEdition edition = GameEdition.Legacy)
        {
            List<string> args = new List<string>();
            if (edition == GameEdition.EnhancedPtc) args.Add(Constants.GameArgsExt);
            if (Log) args.Add(Constants.GameArgsLog);
            if (UseAllCores) args.Add(Constants.GameArgsUseAllCore);
            args.Add(string.Format(Constants.GameArgsModList, modlistPath));
            if(autoConnect) args.Add(Constants.GameArgsContinueSession);

            return string.Join(" ", args);
        }
    }
}
