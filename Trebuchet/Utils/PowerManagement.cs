using System;
using System.Runtime.InteropServices;

namespace Trebuchet.Utils;

/// <summary>
/// Keeps Windows from going to standby/sleep while servers run (a sleeping host kicks every player and stalls
/// all automation). Must be called from the same thread each time (the UI tick does).
/// </summary>
internal static class PowerManagement
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(ExecutionState state);

    private static bool? _awake;

    public static void KeepAwake(bool awake)
    {
        if (!OperatingSystem.IsWindows() || _awake == awake) return;
        try
        {
            SetThreadExecutionState(awake
                ? ExecutionState.Continuous | ExecutionState.SystemRequired
                : ExecutionState.Continuous);
            _awake = awake;
        }
        catch (Exception)
        {
            // not available on this system: nothing to keep awake
        }
    }
}
