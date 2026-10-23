using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using tot_lib.OsSpecific;
using TrebuchetLib;
using TrebuchetLib.DiscordBot;
using TrebuchetLib.OsSpecific;
using TrebuchetLib.Processes;
using TrebuchetLib.Services;

/// <summary>
/// Launcher failures that stopped servers from being managed: an invalid port in a save (the running server was never
/// attached and started twice, every tick leaked a log reader and a query thread), an emoji in a server or player name
/// (the server never showed online, RCon answers failed), a running server whose Saved folder is no junction (every
/// start of another instance failed), and a crash during a timed stop or restart (the crashed server stayed down until
/// the end of the countdown, up to a day).
/// </summary>
static class CrashLauncherChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        await InvalidPortChecks(Check);
        await QueryTextChecks(Check);
        await RConTextChecks(Check);
        await LauncherChecks(Check);
        return count;
    }

    private static async Task InvalidPortChecks(Action<bool, string> check)
    {
        var log = Path.GetTempFileName();
        var standIn = StartStandIn();
        try
        {
            IConanServerProcess? server = null;
            Exception? error = null;
            try
            {
                server = await CreateFactory().Create()
                    .SetProcess(Process.GetProcessById(standIn.Id))
                    .SetServerInfos(new ConanServerInfos { Instance = 0, QueryPort = 270150, RConPort = 99999 })
                    .SetLogFile(log)
                    .UseRCon()
                    .BuildServer();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            check(error is null && server is { RCon: null, State: ProcessState.RUNNING },
                "A server with a query or RCon port out of range is watched without them: " + error?.GetType().Name);
            server?.Dispose();
        }
        finally
        {
            Stop(standIn);
            File.Delete(log);
        }
    }

    private static async Task QueryTextChecks(Action<bool, string> check)
    {
        const string name = "Exiles \U0001F525 Grüße";
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        using var stop = new CancellationTokenSource();
        var answering = Task.Run(async () =>
        {
            var answer = InfoAnswer(name, 3, 40);
            while (!stop.IsCancellationRequested)
            {
                var request = await responder.ReceiveAsync(stop.Token);
                await responder.SendAsync(answer, request.RemoteEndPoint, stop.Token);
            }
        });

        using (var query = new SourceQueryReader(new IPEndPoint(IPAddress.Loopback, port), 1500, 2000))
        {
            query.StartQueryThread();
            for (var i = 0; i < 50 && !query.Online; i++)
            {
                await Task.Delay(100);
                query.Refresh();
            }
            check(query is { Online: true, Players: 3, MaxPlayers: 40, Map: "ConanSandbox" } && query.Name == name,
                "A server named with an emoji answers the query and shows online: " + query.Online + " " + query.Name);
        }

        await stop.CancelAsync();
        try { await answering.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* stopped */ }
    }

    private static async Task RConTextChecks(Action<bool, string> check)
    {
        const string players = "Idx | Char name | Player name\n0 | Conan | Bob \U0001F525\n";
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var auth = await ReadPacket(stream);
            await WritePacket(stream, auth, 2, string.Empty);
            var command = await ReadPacket(stream);
            await WritePacket(stream, command, 0, players);
            // Open until the client is done.
            await ReadPacket(stream);
        });

        var rcon = new Rcon(new IPEndPoint(IPAddress.Loopback, port), "secret", NullLogger<Rcon>.Instance) { Timeout = 5 };
        try
        {
            var response = await rcon.Send("ListPlayers", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            check(response.Exception is null && response.Response == players,
                "An RCon answer naming a player with an emoji is read: " + response.Exception?.Message);
        }
        finally
        {
            rcon.Dispose();
            listener.Stop();
        }
        try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* closed */ }
    }

    private static async Task LauncherChecks(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "TrebuchetCrashLauncherChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tag = "TrebuchetCrashCheck-" + Guid.NewGuid().ToString("N")[..8];
        var config = new Config { ServerInstanceCount = 2, DataDirectory = root, AutoUpdateStatus = AutoUpdateStatus.Never };
        var setup = new AppSetup(config, GameEdition.Enhanced, false, false);
        var os = OsPlatformSpecificExtensions.GetOsPlatformSpecific();
        var files = new AppFiles(setup, os);
        var scan = new ProcessScan();
        using var steam = new Steam(NullLogger<Steam>.Instance, os, setup, new TrebuchetLib.Services.Progress());
        var launcher = new Launcher(files, os, scan, setup, steam, new BackupManager(setup, files), CreateFactory(),
            NullLogger<Launcher>.Instance);
        // The launcher is used on one thread, like the UI thread of the window.
        using var ui = new SerialBotDispatcher();
        Task<T> OnLauncher<T>(Func<Task<T>> work) => ui.InvokeAsync(work);
        Task<T> Read<T>(Func<T> read) => ui.InvokeAsync(() => Task.FromResult(read()));
        var saveA = files.Server.Ref(tag + "-A");
        var saveB = files.Server.Ref(tag + "-B");
        var mods = files.Mods.Ref(tag);
        var standIn = StartStandIn();
        var savesCreated = false;
        try
        {
            // The ini of instance 0 as a typo in the query port of its save writes it.
            var engineIni = Path.Combine(setup.GetInstancePath(0), string.Format(Constants.FileIniServer, "Engine"));
            Directory.CreateDirectory(Path.GetDirectoryName(engineIni)!);
            await File.WriteAllTextAsync(engineIni, "[OnlineSubsystemSteam]\nGameServerQueryPort=270150\n");
            scan.Servers.Add(new ProcessData
            {
                pid = standIn.Id, filename = setup.GetInstanceInternalBinary(0), args = string.Empty,
                processName = Constants.FileServerBin, start = DateTime.UtcNow.AddMinutes(-5)
            });
            Exception? attachError = null;
            try
            {
                await OnLauncher(async () =>
                {
                    await launcher.AttachRunningServers();
                    return true;
                });
            }
            catch (Exception ex)
            {
                attachError = ex;
            }
            scan.Servers.Clear();
            check(attachError is null && await Read(() => launcher.GetServerProcesses().Any(x => x.Infos.Instance == 0)),
                "A running server with a query port out of range in its ini is attached: " + attachError?.GetType().Name);

            // Starts need saves on disk: only where the save folder is a temporary one, or on the build server.
            var saveFolder = files.Server.GetBaseFolder();
            if (!Path.IsPathRooted(saveFolder) || !(IsInside(saveFolder, Path.GetTempPath())
                    || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)))
                return;
            savesCreated = true;
            await Read(() =>
            {
                // The stand-in has no players: a countdown that ends early on an empty server would end at once.
                files.Server.Create(saveA).RestartEarlyWhenEmpty = false;
                files.Server.Create(saveB);
                files.Mods.Create(mods);
                return true;
            });

            // Instance 0 runs, its Saved folder is no junction (started outside Trebuchet).
            var startError = await StartError(() => OnLauncher(() => launcher.CatapultServerProcess(saveB, mods, 1)));
            check(startError is not ArgumentException { ParamName: "path" },
                "A running server without a save junction does not stop other instances from starting: " + startError?.Message);

            // With its junction on save A, save A is still refused on instance 1.
            var saved = Path.Combine(setup.GetInstancePath(0), Constants.FolderGameSave);
            // On Windows the ini above made it a real folder.
            if (Directory.Exists(saved)) Directory.Delete(saved, recursive: true);
            os.MakeSymbolicLink(saved, files.Server.GetDirectory(saveA));
            var lockedError = await StartError(() => OnLauncher(() => launcher.CatapultServerProcess(saveA, mods, 1)));
            check(lockedError is ArgumentException locked && locked.Message.Contains("locked"),
                "A save used by a running server is not started twice: " + lockedError?.Message);

            // The server crashes during a restart in 30 minutes: the countdown ends, the crash restart is not held up.
            config.SetInstanceProfile(0, saveA.Uri.OriginalString);
            var countdown = await Read(() => launcher.RestartServerIn(0, TimeSpan.FromMinutes(30), "check"));
            check(await Read(() => launcher.IsCountdownRunning(0)), "The restart in 30 minutes counts down");
            standIn.Kill(entireProcessTree: true);
            for (var i = 0; i < 50 && !countdown.IsCompleted; i++)
            {
                // What the tick does: the refresh finds the server crashed.
                await OnLauncher(async () =>
                {
                    foreach (var process in launcher.GetServerProcesses().ToList())
                        await process.RefreshAsync();
                    return true;
                });
                await Task.Delay(200);
            }
            check(countdown.IsCompleted && !await Read(() => launcher.IsCountdownRunning(0)),
                "A crash ends the countdown of the server, it does not keep the crashed server down until its end");
        }
        finally
        {
            Stop(standIn);
            try { await Read(() => { launcher.Dispose(); return true; }).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* best effort */ }
            if (savesCreated)
            {
                // Only what this check created, by its unique name.
                foreach (var save in new[] { saveA, saveB })
                    if (save.Name.StartsWith(tag, StringComparison.Ordinal) && files.Server.Exists(save))
                        files.Server.Delete(save);
                if (mods.Name.StartsWith(tag, StringComparison.Ordinal) && files.Mods.Exists(mods))
                    files.Mods.Delete(mods);
            }
            try { Directory.Delete(root, recursive: true); } catch { /* a log reader may still hold a file */ }
        }
    }

    private static async Task<Exception?> StartError(Func<Task<Process>> start)
    {
        try
        {
            using var process = await start();
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static ConanProcessFactory CreateFactory() =>
        new(NullLogger<Rcon>.Instance, NullLogger<LogReader>.Instance, NullLoggerFactory.Instance);

    // A2S_INFO answer: header, protocol, name, map, folder, game, app id, players, max players, bots, type, os,
    // visibility, VAC, version.
    private static byte[] InfoAnswer(string name, byte players, byte maxPlayers)
    {
        var bytes = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 0x11 };
        void Text(string text)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(text));
            bytes.Add(0);
        }
        Text(name);
        Text("ConanSandbox");
        Text("ConanSandbox");
        Text("Conan Exiles");
        bytes.AddRange(BitConverter.GetBytes((short)0));
        bytes.AddRange([players, maxPlayers, 0, (byte)'d', (byte)'w', 0, 0]);
        Text("1.0.0.0");
        return bytes.ToArray();
    }

    private static async Task<int> ReadPacket(NetworkStream stream)
    {
        var size = BitConverter.ToInt32(await ReadExactly(stream, 4));
        var packet = await ReadExactly(stream, size);
        return BitConverter.ToInt32(packet, 0);
    }

    private static async Task<byte[]> ReadExactly(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static async Task WritePacket(NetworkStream stream, int id, int type, string body)
    {
        var text = Encoding.UTF8.GetBytes(body);
        var packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(text.Length + 10));
        packet.AddRange(BitConverter.GetBytes(id));
        packet.AddRange(BitConverter.GetBytes(type));
        packet.AddRange(text);
        packet.AddRange([0, 0]);
        await stream.WriteAsync(packet.ToArray());
    }

    private static bool IsInside(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static Process StartStandIn()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1 >nul")
            : new ProcessStartInfo("sleep", "120");
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        return Process.Start(start)!;
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* gone */ }
        process.Dispose();
    }

    /// <summary>The process scan of the launcher, with the stand-ins as server processes.</summary>
    private sealed class ProcessScan : ITrebuchetOsSpecific
    {
        public List<ProcessData> Servers { get; } = [];
        public Task<List<ProcessData>> GetProcessesWithName(string processName) =>
            Task.FromResult(processName == Constants.FileServerBin ? Servers.ToList() : []);
        public IEnumerable<ProcessData> GetChildProcesses(int parentId) => [];
        public ProcessData GetFirstChildProcesses(int parentId) => ProcessData.Empty;
        public ProcessData GetProcess(int processId) => ProcessData.Empty;
        public void SetEveryoneAccess(DirectoryInfo dir) { }
        public void FocusWindow(IntPtr hwnd) { }
    }
}
