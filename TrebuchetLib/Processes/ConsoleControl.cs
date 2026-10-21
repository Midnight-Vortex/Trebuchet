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
    private static readonly ConsoleCtrlHandler IgnoreAll = _ => true;
    private static bool _handlerInstalled;

    private delegate bool ConsoleCtrlHandler(uint ctrlType);

    /// <returns>True when the Ctrl+C was delivered to the console of the process.</returns>
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
                    _handlerInstalled = SetConsoleCtrlHandler(IgnoreAll, true);
                if (!_handlerInstalled) return false;
                if (!AttachConsole((uint)pid)) return false;
                try
                {
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
