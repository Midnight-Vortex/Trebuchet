using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace TrebuchetLib.Processes;

/// <summary>
/// Sends Ctrl+C to the console of a server, the way an admin stops it by hand: Unreal servers then save and shut down.
/// Closing the main window did nothing for them (their log window is a console owned by conhost).
/// </summary>
/// <remarks>
/// Sending Ctrl+C means attaching to the console of the server, and the event reaches every process of that console,
/// the one that sends it included. Done inside Trebuchet this ended Trebuchet itself on the first stop: the event
/// was not caught, the process was gone without a log line or a crash report, and the server went on running. So it is
/// done by a short-lived helper process (the program itself, started with <see cref="HelperArg"/>): whatever happens to
/// it, the launcher never attaches to a console and never receives the event. A program that can act as the helper says
/// so with <see cref="UseHelper"/>; without that (tests, other hosts) nothing is sent and the next way of stopping is used.
/// </remarks>
public static class ConsoleControl
{
    /// <summary>First argument of the helper call: <c>--send-ctrl-c &lt;process id&gt;</c>.</summary>
    public const string HelperArg = "--send-ctrl-c";

    /// <summary>Exit codes of the helper.</summary>
    public const int Delivered = 0;
    public const int AttachFailed = 2;
    public const int ConsoleShared = 3;
    public const int Failed = 4;
    public const int Unsupported = 5;

    private const uint CtrlCEvent = 0;
    // Start of the helper, attach and the short wait that lets the event reach the console before it is left.
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(10);
    private static string? _helperPath;

    /// <summary>The program that answers <see cref="HelperArg"/> (its <c>Main</c> calls <see cref="IsHelperCall"/> first).</summary>
    public static void UseHelper(string? programPath) =>
        _helperPath = string.IsNullOrWhiteSpace(programPath) ? null : programPath;

    /// <summary>Whether <see cref="TrySendCtrlC"/> can do anything here.</summary>
    public static bool IsAvailable => _helperPath is not null && OperatingSystem.IsWindows();

    /// <summary>
    /// First thing in <c>Main</c> of a program that supports the helper: true when this process was only started to
    /// send the Ctrl+C (then <see cref="RunHelper"/> does it and <c>Main</c> ends with its exit code).
    /// </summary>
    public static bool IsHelperCall(IReadOnlyList<string> args, out int pid)
    {
        pid = 0;
        return args.Count == 2 && string.Equals(args[0], HelperArg, StringComparison.Ordinal)
                               && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0;
    }

    /// <summary>The helper: attaches to the console of the process, sends Ctrl+C there and leaves again.</summary>
    /// <returns>The exit code of the helper process.</returns>
    public static int RunHelper(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0) return Unsupported;
        try
        {
            // A console program (Boulder) starts with a console of its own: leave it first, one process has only one.
            FreeConsole();
            if (!AttachConsole((uint)pid)) return AttachFailed;
            try
            {
                if (!IsOnlyServerOfConsole(pid)) return ConsoleShared;
                // This helper ends a moment later anyway: it does not need to be hurt by the event it sends.
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                var sent = GenerateConsoleCtrlEvent(CtrlCEvent, 0);
                // Leaving the console at once could take the event with it before it was delivered.
                Thread.Sleep(500);
                return sent ? Delivered : Failed;
            }
            finally
            {
                FreeConsole();
            }
        }
        catch (Exception)
        {
            return Failed;
        }
    }

    /// <returns>True when the Ctrl+C was delivered to the console of the process.</returns>
    /// <remarks>
    /// Ctrl+C reaches every process of that console. When other servers share it (e.g. several started from one
    /// terminal with Boulder) nothing is sent: it would stop them too. Blocks until the helper is done, call it from a
    /// background thread (<see cref="TrySendCtrlCAsync"/>).
    /// </remarks>
    public static bool TrySendCtrlC(int pid)
    {
        var program = _helperPath;
        if (program is null || !OperatingSystem.IsWindows() || pid <= 0) return false;
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(HelperArg);
            start.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
            using var helper = Process.Start(start);
            if (helper is null) return false;
            if (!helper.WaitForExit(HelperTimeout))
            {
                try
                {
                    helper.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Gone meanwhile.
                }
                return false;
            }
            return helper.ExitCode == Delivered;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static Task<bool> TrySendCtrlCAsync(int pid) => Task.Run(() => TrySendCtrlC(pid));

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
                using var other = Process.GetProcessById(id);
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

    // A null handler routine with "add" set makes the calling process ignore Ctrl+C.
    // https://learn.microsoft.com/windows/console/setconsolectrlhandler
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
