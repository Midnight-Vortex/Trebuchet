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
