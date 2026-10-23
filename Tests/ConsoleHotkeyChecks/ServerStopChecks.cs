using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TrebuchetLib;
using TrebuchetLib.Processes;

/// <summary>
/// Stopping a server: ways that cannot be delivered (no RCon, no console to send Ctrl+C to, no window) hand over at
/// once. They used to wait 3 minutes each, so a server without RCon only stopped after 3 minutes, by a kill.
/// </summary>
static class ServerStopChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        // Ctrl+C is sent by a helper process (the launcher itself never attaches to the console of a server, that ended
        // Trebuchet on the first stop). The call of the helper is exact; without a helper nothing is sent.
        Check(ConsoleControl.IsHelperCall(["--send-ctrl-c", "123"], out var helperPid) && helperPid == 123
              && ConsoleControl.IsHelperCall([ConsoleControl.HelperArg, "2147483647"], out var biggestPid) && biggestPid == int.MaxValue,
            "The helper call carries the process id of the server");
        foreach (var bad in new[]
                 {
                     Array.Empty<string>(), ["--send-ctrl-c"], ["--send-ctrl-c", "abc"], ["--send-ctrl-c", "0"], ["--send-ctrl-c", "-5"], ["--send-ctrl-c", "+5"],
                     ["--send-ctrl-c", " 5"], ["--send-ctrl-c", "5 "], ["--send-ctrl-c", "99999999999"], ["--send-ctrl-c", "5", "x"], ["--SEND-CTRL-C", "5"],
                     ["5", "--send-ctrl-c"], ["--send-ctrl-c", ""]
                 })
            Check(!ConsoleControl.IsHelperCall(bad, out var none) && none == 0, $"'{string.Join(' ', bad)}' is no helper call");
        Check(!ConsoleControl.IsAvailable && !ConsoleControl.TrySendCtrlC(Environment.ProcessId) && !await ConsoleControl.TrySendCtrlCAsync(Environment.ProcessId),
            "Without a helper nothing is sent");
        Check(OperatingSystem.IsWindows() || ConsoleControl.RunHelper(Environment.ProcessId) == ConsoleControl.Unsupported, "The helper only works on Windows");
        Check(ConsoleControl.RunHelper(0) == ConsoleControl.Unsupported && ConsoleControl.RunHelper(-1) == ConsoleControl.Unsupported, "The helper refuses a process id that is none");
        ConsoleControl.UseHelper(Path.Combine(Path.GetTempPath(), "no-such-program-" + Guid.NewGuid().ToString("N") + ".exe"));
        try
        {
            Check(!ConsoleControl.TrySendCtrlC(Environment.ProcessId) && !ConsoleControl.TrySendCtrlC(0), "A helper that can not be started delivers nothing, and does not throw");
        }
        finally
        {
            ConsoleControl.UseHelper(null);
        }
        Check(!ConsoleControl.IsAvailable, "The helper can be switched off again");

        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
            : new ProcessStartInfo("sleep", "60");
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        var process = Process.Start(start)!;
        var processId = process.Id;
        var log = Path.GetTempFileName();
        try
        {
            using var server = new ConanServerProcess(process, new LogReader(NullLogger<LogReader>.Instance, log))
            {
                Infos = new ConanServerInfos { Instance = 0, QueryPort = 1 },
                SourceQueryReader = new SourceQueryReader(new IPEndPoint(IPAddress.Loopback, 1), 100, 1000),
                StartUtc = DateTime.UtcNow
            };

            var clock = Stopwatch.StartNew();
            // Without RCon and window this ends with Ctrl+C (Windows, when possible) or a kill: both end it at once.
            await server.StopAsync();
            for (var i = 0; i < 20 && ProcessSafety.IsPidAlive(processId); i++)
            {
                await Task.Delay(250);
                await server.RefreshAsync();
            }
            Check(!ProcessSafety.IsPidAlive(processId) && clock.Elapsed < TimeSpan.FromSeconds(10),
                $"A server no stop request can reach is killed at once instead of after 3 minutes ({clock.Elapsed.TotalSeconds:F1} s)");
            await server.RefreshAsync();
            Check(server.State == ProcessState.STOPPED, "The killed server is reported as stopped: " + server.State);
        }
        finally
        {
            // The server watcher disposed the process object already.
            if (ProcessSafety.IsPidAlive(processId)) Process.GetProcessById(processId).Kill(entireProcessTree: true);
            process.Dispose();
            File.Delete(log);
        }

        return count;
    }
}
