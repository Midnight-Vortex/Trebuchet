using System.Runtime.InteropServices;

namespace TrebuchetLib.Processes;

/// <summary>
/// Sends Ctrl+C to the console of a server, the way an admin stops it by hand: Unreal servers then save and shut down.
/// Closing the main window did nothing for them (their log window is a console owned by conhost).
/// </summary>
public static class ConsoleControl
{
    private const uint CtrlCEvent = 0;
    private static readonly object Lock = new();
    // Trebuchet attaches to the server console for a moment and receives the Ctrl+C too: this handler keeps it running.
    // A handler routine (unlike "ignore Ctrl+C") is not inherited, so servers started later still react to Ctrl+C.
    // It only swallows events for a few seconds after we sent a Ctrl+C ourselves: a host without a console (boulder bot
    // under a service wrapper) must still get its own Ctrl+C, close and shutdown signals.
    private static readonly ConsoleCtrlHandler IgnoreAll = _ => Environment.TickCount64 < Interlocked.Read(ref _ignoreUntilTick);
    private static long _ignoreUntilTick;
    private static bool _handlerInstalled;

    private delegate bool ConsoleCtrlHandler(uint ctrlType);

    /// <returns>True when the Ctrl+C was delivered to the console of the process.</returns>
    /// <remarks>
    /// Ctrl+C reaches every process of that console. When other servers share it (e.g. several started from one
    /// terminal with Boulder) nothing is sent: it would stop them too.
    /// </remarks>
    public static bool TrySendCtrlC(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0) return false;
        lock (Lock)
        {
            try
            {
                // A console application (Boulder) would lose its own console by attaching to another one.
                if (GetConsoleWindow() != IntPtr.Zero) return false;
                if (!_handlerInstalled)
                {
                    // .NET binds Console.Out/Error to the standard handles when they are first used. Bound while we
                    // are attached to a server console they would point to a closed handle afterwards and every later
                    // Console.Write (DepotDownloader writes there) would throw: bind them now, without a console.
                    _ = Console.Out;
                    _ = Console.Error;
                    _handlerInstalled = SetConsoleCtrlHandler(IgnoreAll, true);
                }
                if (!_handlerInstalled) return false;
                if (!AttachConsole((uint)pid)) return false;
                try
                {
                    if (!IsOnlyServerOfConsole(pid)) return false;
                    // The event reaches this process too, a moment later on another thread.
                    Interlocked.Exchange(ref _ignoreUntilTick, Environment.TickCount64 + 5000);
                    return GenerateConsoleCtrlEvent(CtrlCEvent, 0);
                }
                finally
                {
                    FreeConsole();
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>True when no other server process is attached to the console we are attached to.</summary>
    private static bool IsOnlyServerOfConsole(int pid)
    {
        var ids = new uint[64];
        var count = GetConsoleProcessList(ids, (uint)ids.Length);
        if (count == 0 || count > ids.Length) return false;
        var serverName = Path.GetFileNameWithoutExtension(Constants.FileServerBin);
        var own = Environment.ProcessId;
        for (var i = 0; i < count; i++)
        {
            var id = (int)ids[i];
            if (id == pid || id == own) continue;
            try
            {
                using var other = System.Diagnostics.Process.GetProcessById(id);
                if (string.Equals(other.ProcessName, serverName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch (Exception)
            {
                // Gone meanwhile: not a server sharing the console.
            }
        }
        return true;
    }

    // https://learn.microsoft.com/windows/console/getconsoleprocesslist
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    // https://learn.microsoft.com/windows/console/attachconsole
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    // https://learn.microsoft.com/windows/console/freeconsole
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    // https://learn.microsoft.com/windows/console/generateconsolectrlevent
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    // https://learn.microsoft.com/windows/console/setconsolectrlhandler
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    // https://learn.microsoft.com/windows/console/getconsolewindow
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
