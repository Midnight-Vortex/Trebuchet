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

        return count;
    }
}
