using System.ComponentModel;
using System.Diagnostics;

namespace TrebuchetLib.Processes;

/// <summary>
/// Exception-safe wrappers around <see cref="Process"/>. A process that is being killed, has exited or whose
/// <see cref="Process"/> object was disposed throws from almost every member; the launcher must never crash for that.
/// </summary>
public static class ProcessSafety
{
    public static bool HasExitedSafe(this Process process, int pid)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // No process associated (disposed or never started) — treat as gone.
            return true;
        }
        catch (Win32Exception)
        {
            // Access denied on the handle: fall back to a pid lookup.
            return !IsPidAlive(pid);
        }
        catch (NotSupportedException)
        {
            return !IsPidAlive(pid);
        }
    }

    public static bool IsPidAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var probe = Process.GetProcessById(pid);
            return !probe.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // Exists but we cannot query it.
            return true;
        }
    }

    /// <summary>
    /// Kill the process (and its children). Returns true when the process is gone or the kill was issued.
    /// </summary>
    public static bool TryKill(this Process process, int pid, out Exception? error)
    {
        error = null;
        if (process.HasExitedSafe(pid)) return true;
        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return true; // exited between the check and the kill
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // Tree kill can fail on a child (access denied); still try the main process alone.
        try
        {
            process.Kill();
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        try
        {
            using var byPid = Process.GetProcessById(pid);
            byPid.Kill();
            return true;
        }
        catch (ArgumentException)
        {
            return true; // no longer running
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    public static bool TryCloseMainWindow(this Process process)
    {
        try
        {
            return process.CloseMainWindow();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void TryRefresh(this Process process)
    {
        try
        {
            process.Refresh();
        }
        catch (Exception)
        {
            // best effort
        }
    }

    public static long WorkingSetSafe(this Process process, int pid)
    {
        try
        {
            if (process.HasExitedSafe(pid)) return 0;
            return process.WorkingSet64;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static TimeSpan TotalProcessorTimeSafe(this Process process, int pid)
    {
        try
        {
            if (process.HasExitedSafe(pid)) return TimeSpan.Zero;
            return process.TotalProcessorTime;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Probe <c>Responding</c> on a separate <see cref="Process"/> instance so the shared object is never touched
    /// from two threads, and an exit during the probe cannot throw into the caller.
    /// </summary>
    public static bool ProbeResponding(int pid)
    {
        try
        {
            using var probe = Process.GetProcessById(pid);
            return probe.Responding;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
