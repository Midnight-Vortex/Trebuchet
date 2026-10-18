using System.Text.Json.Serialization;
using TrebuchetLib.Sequences;

namespace TrebuchetLib
{
    public class ServerProfile : ProfileFile<ServerProfile>
    {
        public string AdminPassword { get; set; } = AdminPasswordDefault;
        public long CPUThreadAffinity { get; set; } = CPUThreadAffinityDefault;
        public bool DisableHighPrecisionMoveTool { get; set; } = DisableHighPrecisionMoveToolDefault;
        public bool EnableBattleEye { get; set; } = EnableBattleEyeDefault;
        public bool EnableMultiHome { get; set; } = EnableMultiHomeDefault;
        public bool EnableRCon { get; set; } = EnableRConDefault;
        public bool EnableVAC { get; set; } = EnableVACDefault;
        public int GameClientPort { get; set; } = GameClientPortDefault;
        public bool KillZombies { get; set; } = KillZombiesDefault;
        public bool Log { get; set; } = LogDefault;
        public List<string> LogFilters { get; set; } = LogFiltersDefault;
        public string Map { get; set; } = MapDefault;
        public int MaximumTickRate { get; set; } = MaximumTickRateDefault;
        public int MaxPlayers { get; set; } = MaxPlayersDefault;
        public string MultiHomeAddress { get; set; } = MultiHomeAddressDefault;
        public bool NoAISpawn { get; set; } = NoAISpawnDefault;
        public int ProcessPriority { get; set; } = ProcessPriorityDefault;
        public int RConMaxKarma { get; set; } = RConMaxKarmaDefault;
        public string RConPassword { get; set; } = RConPasswordDefault;
        public int RConPort { get; set; } = RConPortDefault;
        public bool RestartWhenDown { get; set; } = RestartWhenDownDefault;
        public string ServerName { get; set; } = ServerNameDefault;
        public string ServerPassword { get; set; } = ServerPasswordDefault;
        public int ServerRegion { get; set; } = ServerRegionDefault;
        public int SourceQueryPort { get; set; } = SourceQueryPortDefault;
        public List<string> SudoSuperAdmins { get; set; } = SudoSuperAdminsDefault;
        public bool UseAllCores { get; set; } = UseAllCoresDefault;
        public int ZombieCheckSeconds { get; set; } = ZombieCheckSecondsDefault;
        public bool AutoRestart { get; set; } = AutoRestartDefault;
        public TimeSpan AutoRestartMinUptime { get; set; } = AutoRestartMinUptimeDefault;
        public List<TimeSpan> AutoRestartDailyTime { get; set; } = AutoRestartDailyTimeDefault;
        public int AutoRestartMaxPerDay { get; set; } = AutoRestartMaxPerDayDefault;
        public string DiscordWebHookNotifications { get; set; } = DiscordWebHookNotificationsDefault;
        public string DiscordBotName { get; set; } = DiscordBotNameDefault;
        public bool DiscordTimestamp { get; set; } = DiscordTimestampDefault;
        public bool NotifyExternalIpChange { get; set; } = NotifyExternalIpChangeDefault;
        public string AdditionalArguments { get; set; } = AdditionalArgumentsDefault;
        public bool EnableMultiHomeHttp { get; set; } = EnableMultiHomeHttpDefault;
        public bool EnforceProcessSettings { get; set; } = EnforceProcessSettingsDefault;
        public bool RequiresRestartFileCheck { get; set; } = RequiresRestartFileCheckDefault;
        public int RConBroadcastMethod { get; set; } = RConBroadcastMethodDefault;
        public bool UseRestartWarnings { get; set; } = UseRestartWarningsDefault;
        public List<RestartWarning> RestartWarnings { get; set; } = RestartWarningsDefault;
        public bool RestartEarlyWhenEmpty { get; set; } = RestartEarlyWhenEmptyDefault;
        public string MessageReady { get; set; } = MessageReadyDefault;
        public string MessageRestarted { get; set; } = MessageRestartedDefault;
        public string MessageShutdown { get; set; } = MessageShutdownDefault;
        public bool BackupOnShutdown { get; set; } = BackupOnShutdownDefault;
        public string BackupDirectory { get; set; } = BackupDirectoryDefault;
        public bool BackupLimitEnabled { get; set; } = BackupLimitEnabledDefault;
        public int BackupLimitDays { get; set; } = BackupLimitDaysDefault;
        public int ScriptMode { get; set; } = ScriptModeDefault;
        public string ScriptPath { get; set; } = ScriptPathDefault;
        public string ScriptArguments { get; set; } = ScriptArgumentsDefault;

        public Sequence StoppingSequence { get; set; } = new();
        public Sequence StartingSequence { get; set; } = new();

        
        [JsonIgnore]
        public string ProfileFolder => Path.GetDirectoryName(FilePath) ?? throw new Exception($"Invalid directory for {FilePath}.");

        [JsonIgnore]
        public string ProfileName => Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? string.Empty;
        
        
        /// <summary>
        /// Generate the server arguments for a server instance.
        /// </summary>
        /// <param name="instance"></param>
        /// <param name="modlistPath"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public string GetServerArgs(int instance, string modlistPath)
        {
            List<string> args = new List<string>() { Map };
            // Like the Dedicated Server Launcher, user parameters go first so custom map arguments keep working.
            if (!string.IsNullOrWhiteSpace(AdditionalArguments)) args.Add(AdditionalArguments.Trim());
            if (Log) args.Add(Constants.GameArgsLog);
            if (UseAllCores) args.Add(Constants.GameArgsUseAllCore);
            args.Add(string.Format(Constants.ServerArgsMaxPlayers, MaxPlayers));
            args.Add(string.Format(Constants.GameArgsModList, modlistPath));
            if (EnableMultiHome && !string.IsNullOrWhiteSpace(MultiHomeAddress))
            {
                args.Add(string.Format(Constants.ServerArgsMultiHome, MultiHomeAddress.Trim()));
                if (EnableMultiHomeHttp)
                    args.Add(string.Format(Constants.ServerArgsMultiHomeHttp, MultiHomeAddress.Trim()));
            }

            return string.Join(" ", args);
        }
        
        public static readonly string AdminPasswordDefault  = string.Empty;
        public static readonly long CPUThreadAffinityDefault = 0xffffffffffff;
        public static readonly bool DisableHighPrecisionMoveToolDefault = false;
        public static readonly bool EnableBattleEyeDefault = false;
        public static readonly bool EnableMultiHomeDefault = false;
        public static readonly bool EnableRConDefault = false;
        public static readonly bool EnableVACDefault = false;
        public static readonly int GameClientPortDefault = 7777;
        public static readonly bool KillZombiesDefault = false;
        public static readonly bool LogDefault = false;
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
        public static readonly string MapDefault = "/Game/Maps/ConanSandbox/ConanSandbox";
        public static readonly int MaximumTickRateDefault = 30;
        public static readonly int MaxPlayersDefault = 30;
        public static readonly string MultiHomeAddressDefault = string.Empty;
        public static readonly bool NoAISpawnDefault = false;
        public static readonly int ProcessPriorityDefault = 0;
        public static readonly int RConMaxKarmaDefault = 60;
        public static readonly string RConPasswordDefault = string.Empty;
        public static readonly int RConPortDefault = 25575;
        public static readonly bool RestartWhenDownDefault = false;
        public static readonly string ServerNameDefault = "Conan Exiles Dedicated Server";
        public static readonly string ServerPasswordDefault = string.Empty;
        public static readonly int ServerRegionDefault = 0;
        public static readonly int SourceQueryPortDefault = 27015;
        public static readonly List<string> SudoSuperAdminsDefault = [];
        public static readonly bool UseAllCoresDefault = true;
        public static readonly int ZombieCheckSecondsDefault = 300;
        public static readonly TimeSpan AutoRestartMinUptimeDefault = TimeSpan.FromHours(2);
        public static List<TimeSpan> AutoRestartDailyTimeDefault => [TimeSpan.FromHours(12)];
        public static readonly int AutoRestartMaxPerDayDefault = 0;
        public static readonly bool AutoRestartDefault = false;
        public static readonly string DiscordWebHookNotificationsDefault = string.Empty;
        public static readonly string DiscordBotNameDefault = string.Empty;
        public static readonly bool DiscordTimestampDefault = false;
        public static readonly bool NotifyExternalIpChangeDefault = false;
        public static readonly string AdditionalArgumentsDefault = string.Empty;
        public static readonly bool EnableMultiHomeHttpDefault = true;
        public static readonly bool EnforceProcessSettingsDefault = false;
        public static readonly bool RequiresRestartFileCheckDefault = true;

        public static readonly int RConBroadcastMethodDefault = 0;
        public static readonly bool UseRestartWarningsDefault = false;
        public static List<RestartWarning> RestartWarningsDefault =>
        [
            new(TimeSpan.FromMinutes(5), "The server will restart in 5 minutes... Reason: {reason}"),
            new(TimeSpan.FromMinutes(3), "3 minutes before server restart! Reason: {reason}"),
            new(TimeSpan.FromMinutes(1), "ONE MINUTE BEFORE RESTART, GET SAFE! Reason: {reason}")
        ];
        public static readonly bool RestartEarlyWhenEmptyDefault = true;
        public static readonly string MessageReadyDefault = string.Empty;
        public static readonly string MessageRestartedDefault = "Restarting the server... Reason: {reason}";
        public static readonly string MessageShutdownDefault = string.Empty;
        public static readonly bool BackupOnShutdownDefault = false;
        public static readonly string BackupDirectoryDefault = string.Empty;
        public static readonly bool BackupLimitEnabledDefault = false;
        public static readonly int BackupLimitDaysDefault = 7;
        public static readonly int ScriptModeDefault = ScriptModeNone;
        public static readonly string ScriptPathDefault = string.Empty;
        public static readonly string ScriptArgumentsDefault = string.Empty;

        // DSL "Run Scripts" choices.
        public const int ScriptModeNone = 0;
        public const int ScriptModeBeforeStart = 1;
        public const int ScriptModeShutdownBeforeBackup = 2;
        public const int ScriptModeShutdownAfterBackup = 3;

        /// <summary>RCon command used for messages to players (DSL "broadcast / server / alert" selector).</summary>
        public string GetRConBroadcastCommand() => RConBroadcastMethod switch
        {
            1 => "server",
            2 => "/alert",
            _ => "broadcast"
        };

        /// <summary>ProcessPriority value that leaves priority and affinity untouched.</summary>
        public const int ProcessPriorityUnchanged = 4;

        /// <summary>
        /// Webhook URLs, several allowed (one per line, or separated by comma/semicolon).
        /// </summary>
        public IEnumerable<string> GetDiscordWebHooks()
        {
            return DiscordWebHookNotifications
                .Split(['\n', '\r', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}