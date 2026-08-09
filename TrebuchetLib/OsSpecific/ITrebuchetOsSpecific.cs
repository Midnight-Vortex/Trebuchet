using System.Diagnostics;

namespace TrebuchetLib.OsSpecific;

public interface ITrebuchetOsSpecific
{
    IEnumerable<ProcessData> GetChildProcesses(int parentId);
    ProcessData GetFirstChildProcesses(int parentId);
    ProcessData GetProcess(int processId);
    Task<List<ProcessData>> GetProcessesWithName(string processName);
    void SetEveryoneAccess(DirectoryInfo dir);
    void FocusWindow(IntPtr hwnd);
}