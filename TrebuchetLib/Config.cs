using System.Text.Json.Serialization;
using tot_lib;

namespace TrebuchetLib
{
    public static class AutoUpdateStatus
    {
        public const int CheckForUpdates = 2;
        public const int Never = 0;
        public const int OnlyOnStart = 1;
    }

    public sealed class Config : ConfigFile<Config>
    {
        private string[] _selectedServerModlists = [];
        private string[] _selectedServerProfiles = [];
        
        public int AutoUpdateStatus { get; set; } = AutoUpdateStatusDefault;
        public TimeSpan UpdateCheckFrequency { get; set; } = UpdateCheckFrequencyDefault;
        public string ClientPath { get; set; } = ClientPathDefault;
        public bool ManageClient { get; set; } = ManageClientDefault;
        public int MaxDownloads { get; set; } = MaxDownloadsDefault;
        public int ServerInstanceCount { get; set; } = ServerInstanceCountDefault;
        public bool VerifyAll { get; set; } = VerifyAllDefault;
        public string SelectedClientModlist { get; set; } = string.Empty;
        public string SelectedClientProfile { get; set; } = string.Empty;
        public string[] SelectedServerModlists { get => _selectedServerModlists; set => _selectedServerModlists = value; }
        public string[] SelectedServerProfiles { get => _selectedServerProfiles; set => _selectedServerProfiles = value; }
        public string DataDirectory { get; set; } = DataDirectoryDefault;
        public string NotificationServerCrash { get; set; } = NotificationServerCrashDefault;
        public string NotificationServerOnline { get; set; } = NotificationServerOnlineDefault;
        public string NotificationServerStop { get; set; } = NotificationServerStopDefault;
        public string NotificationServerManualStop { get; set; } = NotificationServerManualStopDefault;
        public string NotificationServerAutomatedRestart { get; set; } = NotificationServerAutomatedRestartDefault;
        public string NotificationServerModUpdate { get; set; } = NotificationServerModUpdateDefault;
        public string NotificationServerServerUpdate { get; set; } = NotificationServerServerUpdateDefault;
        public uint CellId { get; set; } = CellIdDefault;
        public string NotificationServerManualRestart { get; set; } = NotificationServerManualRestartDefault;
        public string NotificationExternalIpChanged { get; set; } = NotificationExternalIpChangedDefault;
        public string NotificationCrashLoop { get; set; } = NotificationCrashLoopDefault;
        public string ScriptBeforeUpdate { get; set; } = string.Empty;
        public string ScriptAfterUpdate { get; set; } = string.Empty;
        
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        [Obsolete]
        public string InstallPath { get; set; } = string.Empty;
        

        public static string GetDefaultInstallPath()
        {
            return typeof(Config).GetStandardFolder(Environment.SpecialFolder.MyDocuments).FullName;
        }
        
        public void SetInstanceParameters(int instance, string modlist, string profile)
        {
            SetInstanceModlist(instance, modlist);
            SetInstanceProfile(instance, profile);
        }
        
        public void SetInstanceModlist(int instance, string modlist)
        {
            if (SelectedServerModlists.Length <= instance)
                Array.Resize(ref _selectedServerModlists, instance + 1);
            SelectedServerModlists[instance] = modlist;
        }

        public void SetInstanceProfile(int instance, string profile)
        {
            if (SelectedServerProfiles.Length <= instance)
                Array.Resize(ref _selectedServerProfiles, instance + 1);
            SelectedServerProfiles[instance] = profile;
        }

        public string GetInstanceModlist(int instance)
        {
            if (SelectedServerModlists.Length <= instance) return string.Empty;
            return SelectedServerModlists[instance];
        }

        public string GetInstanceProfile(int instance)
        {
            if (SelectedServerProfiles.Length <= instance) return string.Empty;
            return SelectedServerProfiles[instance];
        }
        
        public static readonly int AutoUpdateStatusDefault = 1;
        public static readonly string ClientPathDefault = string.Empty;
        public static readonly bool ManageClientDefault = false;
        public static readonly int MaxDownloadsDefault = 8;
        public static readonly int ServerInstanceCountDefault = 0;
        public static readonly bool VerifyAllDefault = false;
        public static readonly string DataDirectoryDefault = string.Empty;
        public static readonly TimeSpan UpdateCheckFrequencyDefault = TimeSpan.FromMinutes(5);
        public static readonly string NotificationServerCrashDefault = "Server {serverName} has crashed";
        public static readonly string NotificationServerOnlineDefault = "Server {serverName} is now online";
        public static readonly string NotificationServerStopDefault = "Server Shutdown: {Reason}";
        public static readonly string NotificationServerManualStopDefault = "Manual shutdown requested";
        public static readonly string NotificationServerAutomatedRestartDefault = "Automated Restart";
        public static readonly string NotificationServerModUpdateDefault = "Mod update detected: {modList}";
        public static readonly string NotificationServerServerUpdateDefault = "Game update detected";
        public static readonly uint CellIdDefault = 0;
        public static readonly string NotificationServerManualRestartDefault = "Manual restart requested";
        public static readonly string NotificationCrashLoopDefault = "Server {servername} crashed {count} times within {minutes} minutes, automatic restart is paused. Check the logs and start it manually.";
        public static readonly string NotificationExternalIpChangedDefault ="The external IP address of {servername} changed from {old_ip} to {ip}";
    }
}