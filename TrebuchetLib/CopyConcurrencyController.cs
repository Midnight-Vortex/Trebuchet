using System.Runtime.InteropServices;

namespace TrebuchetLib;

internal readonly record struct CopyLoad(double? Cpu, double? Memory, ulong AvailableMemory);

internal sealed class CopyConcurrencyController
{
    public const int Minimum = 4;
    public const int Maximum = 8;
    private int _limit = Minimum;
    private int _quietSamples;
    public int Limit => Volatile.Read(ref _limit);

    public void Update(CopyLoad load, double? ioMilliseconds)
    {
        if (load.Cpu is null || load.Memory is null)
        {
            _quietSamples = 0;
            Volatile.Write(ref _limit, Minimum);
            return;
        }
        if (load.Cpu >= 0.80 || load.Memory >= 0.85 || load.AvailableMemory < 1024UL * 1024 * 1024
            || ioMilliseconds >= 40)
        {
            _quietSamples = 0;
            Volatile.Write(ref _limit, Math.Max(Minimum, Limit - 2));
        }
        else if (load.Cpu <= 0.60 && load.Memory <= 0.75 && load.AvailableMemory >= 1536UL * 1024 * 1024
                 && ioMilliseconds is <= 15)
        {
            if (++_quietSamples < 2) return;
            _quietSamples = 0;
            Volatile.Write(ref _limit, Math.Min(Maximum, Limit + 1));
        }
        else _quietSamples = 0;
    }
}

internal sealed class CopySystemLoad
{
    private ulong _idle;
    private ulong _total;
    private bool _sampled;

    public CopyLoad Read()
    {
        if (!OperatingSystem.IsWindows()) return default;
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return default;
        var total = kernel + user; // Kernel time includes idle time.
        double? cpu = _sampled && total > _total && idle >= _idle
            ? Math.Clamp(1.0 - (double)(idle - _idle) / (total - _total), 0, 1) : null;
        _idle = idle;
        _total = total;
        _sampled = true;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref memory)
            ? new CopyLoad(cpu, memory.Load / 100.0, memory.AvailablePhysical) : default;
    }

    // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    // https://learn.microsoft.com/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
