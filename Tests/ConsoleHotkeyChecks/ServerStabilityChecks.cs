using System.Diagnostics;
using System.Net.Sockets;
using TrebuchetLib;
using TrebuchetLib.Processes;
using TrebuchetLib.Services;

/// <summary>
/// Server area: kill/exit safety, DSL style macros, launch arguments and port test.
/// </summary>
static class ServerStabilityChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        // A killed server is reported by WMI without a command line: must not throw.
        var setup = new AppSetup(new Config { ServerInstanceCount = 2 }, GameEdition.Enhanced, false, false);
        Check(!setup.TryGetInstanceIndexFromPath(string.Empty, out _), "Empty process path is ignored instead of throwing");
        Check(!setup.TryGetInstanceIndexFromPath("   ", out _), "Blank process path is ignored");
        Check(!setup.TryGetInstanceIndexFromPath("\"", out _), "Malformed process path is ignored");

        // Exited and disposed processes never throw from the safe accessors.
        var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        var pid = process.Id;
        await process.WaitForExitAsync();
        Check(process.HasExitedSafe(pid), "Exited process is detected");
        Check(process.WorkingSetSafe(pid) == 0, "Memory of an exited process reads as zero");
        Check(process.TotalProcessorTimeSafe(pid) == TimeSpan.Zero, "CPU time of an exited process reads as zero");
        Check(process.TryKill(pid, out _), "Killing an exited process is a no-op");
        Check(!ProcessSafety.ProbeResponding(pid) || ProcessSafety.IsPidAlive(pid), "Responding probe on exited process does not throw");
        process.Dispose();
        Check(process.HasExitedSafe(pid), "Disposed process counts as exited");
        Check(process.WorkingSetSafe(pid) == 0, "Disposed process memory does not throw");
        Check(process.TryKill(pid, out _), "Killing a disposed process does not throw");

        // Killing a live process tree works and reports success.
        var sleeper = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        Check(sleeper.TryKill(sleeper.Id, out var killError), "Live process can be killed: " + killError?.Message);
        Check(sleeper.WaitForExit(5000), "Killed process exits");
        sleeper.Dispose();

        // Launch arguments: DSL puts additional parameters first, MULTIHOMEHTTP follows MULTIHOME.
        var profile = new ServerProfile
        {
            AdditionalArguments = " -MyMapArg ",
            EnableMultiHome = true,
            MultiHomeAddress = "192.168.1.5",
            EnableMultiHomeHttp = true,
            Log = false,
            UseAllCores = false
        };
        var args = profile.GetServerArgs(0, "mods.txt");
        Check(args.StartsWith(ServerProfile.MapDefault + " -MyMapArg ", StringComparison.Ordinal), "Additional parameters follow the map");
        Check(args.Contains("-MULTIHOME=192.168.1.5") && args.Contains("-MULTIHOMEHTTP=192.168.1.5"), "MULTIHOME and MULTIHOMEHTTP are passed");
        profile.EnableMultiHomeHttp = false;
        Check(!profile.GetServerArgs(0, "mods.txt").Contains("MULTIHOMEHTTP"), "MULTIHOMEHTTP can be disabled");
        profile.MultiHomeAddress = string.Empty;
        Check(!profile.GetServerArgs(0, "mods.txt").Contains("MULTIHOME"), "Empty MULTIHOME address is not passed (DSL 1.7.7)");

        // Discord: several webhooks.
        profile.DiscordWebHookNotifications = "https://discord.com/api/webhooks/1/a\r\nhttps://discord.com/api/webhooks/2/b, https://discord.com/api/webhooks/1/a";
        Check(profile.GetDiscordWebHooks().Count() == 2, "Webhooks are split per line/comma and deduplicated");

        // Macros.
        profile.ServerName = "My Server";
        profile.ServerPassword = "pw";
        profile.GameClientPort = 7777;
        profile.SourceQueryPort = 27015;
        var context = new MacroContext { Profile = profile, Reason = "Mod update", ExternalIp = "1.2.3.4", EngineVersion = "UE5" };
        Check(ServerMacros.Expand("{servername} {SERVERNAME}", context) == "My Server My Server", "Macros are case-insensitive");
        Check(ServerMacros.Expand("{steamconnect}", context) == "steam://connect/1.2.3.4:27015/pw", "Steam connect contains password");
        Check(ServerMacros.Expand("{steamconnect_nopassword}", context) == "steam://connect/1.2.3.4:27015", "Steam connect without password");
        Check(ServerMacros.Expand("{reason} {ip}:{game_port} {engine_version}", context) == "Mod update 1.2.3.4:7777 UE5", "Reason, ip, port, engine macros");
        Check(ServerMacros.Expand("{unknown} {map_name}", context) == "{unknown} ConanSandbox", "Unknown macros stay, map name is extracted");

        // Port test: a port held by a socket is reported as in use.
        using (var holder = new UdpClient(0))
        {
            var port = ((System.Net.IPEndPoint)holder.Client.LocalEndPoint!).Port;
            var result = NetworkTools.TestPort("test", port, ProtocolType.Udp, null);
            Check(!result.Available && result.Message.Length > 0, "Used UDP port is reported with the socket error");
        }
        var tcp = new TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        var freePort = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        Check(NetworkTools.TestPort("test", freePort, ProtocolType.Tcp, null).Available, "Free TCP port is reported as available");
        Check(!NetworkTools.TestPort("test", 0, ProtocolType.Tcp, null).Available, "Invalid port is rejected");

        // DSL backup: database selection rules, file set and day folder limit.
        Check(BackupManager.IsBackedUpDatabase("game.db") && BackupManager.IsBackedUpDatabase("DLC_Siptah.db")
              && BackupManager.IsBackedUpDatabase("game_0.db"), "Game (UE4 and UE5 game_0.db) and DLC databases are backed up");
        Check(!BackupManager.IsBackedUpDatabase("game_0_backup_1.db") && !BackupManager.IsBackedUpDatabase("game_0_upgrade_tags_%d30_1.db"),
            "The game's own backup and upgrade copies are skipped (DSL 1.9.0)");
        var saved = Path.Combine(Path.GetTempPath(), "TrebuchetBackupChecks-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(saved, "Logs"));
            Directory.CreateDirectory(Path.Combine(saved, "Config", "WindowsServer"));
            foreach (var file in new[] { "game_0.db", "game_0.db-wal", "game_0_backup_1.db", "blacklist.txt", Path.Combine("Logs", "ConanSandbox.log"),
                         Path.Combine("Config", "WindowsServer", "Engine.ini"), Path.Combine("Config", "WindowsServer", "Game.ini") })
                await File.WriteAllTextAsync(Path.Combine(saved, file), "x");
            var files = BackupManager.GetShutdownBackupFiles(saved).Select(x => x.relative).ToList();
            Check(files.Contains("game_0.db") && files.Contains("game_0.db-wal") && !files.Contains("game_0_backup_1.db")
                  && files.Contains("blacklist.txt") && files.Contains(Path.Combine("Logs", "ConanSandbox.log")) && files.Count == 6,
                "Shutdown backup takes db with WAL, log, ini files and lists");

            var root = Path.Combine(saved, "backups");
            foreach (var day in new[] { "2026-10-01", "2026-10-02", "2026-10-03", "not-a-day" })
                Directory.CreateDirectory(Path.Combine(root, day));
            BackupManager.TrimDayFolders(root, 2);
            Check(!Directory.Exists(Path.Combine(root, "2026-10-01")) && Directory.Exists(Path.Combine(root, "2026-10-03"))
                  && Directory.Exists(Path.Combine(root, "not-a-day")), "Only the newest day folders are kept, other folders untouched");
        }
        finally
        {
            Directory.Delete(saved, true);
        }

        // Warnings and message method.
        var warningProfile = new ServerProfile();
        Check(!warningProfile.UseRestartWarnings && warningProfile.RestartWarnings.Count == 3
              && warningProfile.RestartWarnings.All(x => x.Message.Contains("{reason}")), "Default DSL warnings are prepared but off");
        Check(warningProfile.GetRConBroadcastCommand() == "broadcast", "Default message method is broadcast");
        warningProfile.RConBroadcastMethod = 1;
        Check(warningProfile.GetRConBroadcastCommand() == "server", "Pippi server method");
        warningProfile.RConBroadcastMethod = 2;
        Check(warningProfile.GetRConBroadcastCommand() == "/alert", "Alert method");
        var json = System.Text.Json.JsonSerializer.Serialize(new TrebuchetLib.Sequences.Sequence
        {
            Actions = [new TrebuchetLib.Sequences.SequenceActionBroadcast { Message = "hi {servername}" }]
        });
        var back = System.Text.Json.JsonSerializer.Deserialize<TrebuchetLib.Sequences.Sequence>(json);
        Check(back?.Actions.Single() is TrebuchetLib.Sequences.SequenceActionBroadcast { Message: "hi {servername}" },
            "Broadcast sequence action survives profile serialization");

        return count;
    }
}
