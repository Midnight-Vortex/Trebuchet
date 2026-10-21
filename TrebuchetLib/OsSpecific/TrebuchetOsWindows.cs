using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TrebuchetLib.OsSpecific;

[SupportedOSPlatform("windows")]
public class TrebuchetOsWindows : ITrebuchetOsSpecific
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetForegroundWindow(IntPtr hwnd);

    // Only the fields ProcessData reads: "Select *" made WMI collect about 45 properties per process, every 2-4 s.
    private const string ProcessFields = "ProcessId, Name, CreationDate, CommandLine";

    // A stalled WMI provider never answered and the tick waited for it forever (no restarts, no crash detection).
    private static System.Management.EnumerationOptions QueryOptions(bool forwardOnly) => new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        ReturnImmediately = true,
        Rewindable = !forwardOnly
    };

    public IEnumerable<ProcessData> GetChildProcesses(int parentId)
    {
        var query = $"Select {ProcessFields} From Win32_Process Where ParentProcessId = {parentId}";
        using var searcher = new ManagementObjectSearcher(new ManagementScope(), new ObjectQuery(query), QueryOptions(true));
        using var processList = searcher.Get();
        return processList.EnumerateData();
    }

    public ProcessData GetFirstChildProcesses(int parentId)
    {
        return GetChildProcesses(parentId).FirstOrDefault(x => !x.IsEmpty, ProcessData.Empty);
    }

    public ProcessData GetProcess(int processId)
    {
        var query = $"Select {ProcessFields} From Win32_Process Where ProcessId = {processId}";
        using var searcher = new ManagementObjectSearcher(new ManagementScope(), new ObjectQuery(query), QueryOptions(true));
        using var processList = searcher.Get();
        return processList.EnumerateData().FirstOrDefault(x => !x.IsEmpty, ProcessData.Empty);
    }

    public async Task<List<ProcessData>> GetProcessesWithName(string processName)
    {
        return await Task.Run(() =>
        {
            var query = $"Select {ProcessFields} From Win32_Process Where Name='{processName}'";
            try
            {
                using var searcher = new ManagementObjectSearcher(new ManagementScope(), new ObjectQuery(query), QueryOptions(true));
                using ManagementObjectCollection processList = searcher.Get();
                return processList.EnumerateData().ToList();
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
            {
                return new List<ProcessData>();
            }
        });
    }

    public void SetEveryoneAccess(DirectoryInfo dir)
    {
        SetEveryoneAccess(dir.GetDirectories("*", SearchOption.AllDirectories));
        SetEveryoneAccess(dir.GetFiles("*", SearchOption.AllDirectories));
    }

    public void FocusWindow(IntPtr hwnd)
    {
        SetForegroundWindow(hwnd);
    }

    private static bool SetEveryoneAccess(string dirName)
    {
        // Make sure directory exists
        if (!Directory.Exists(dirName))
            return false;
        // Get directory access info
        DirectoryInfo dinfo = new DirectoryInfo(dirName);
        DirectorySecurity dSecurity = dinfo.GetAccessControl();
        // Add the FileSystemAccessRule to the security settings. 
        dSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.NoPropagateInherit, AccessControlType.Allow));
        // Set the access control
        dinfo.SetAccessControl(dSecurity);

        return true;
    }
    
    private static void SetEveryoneAccess(DirectoryInfo[] dirs)
    {
        foreach (var dir in dirs)
        {
            DirectorySecurity dSecurity = dir.GetAccessControl();
            // Add the FileSystemAccessRule to the security settings. 
            dSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.NoPropagateInherit, AccessControlType.Allow));
            // Set the access control
            dir.SetAccessControl(dSecurity);
        }
    }
    
    private static void SetEveryoneAccess(FileInfo[] files)
    {
        foreach (var file in files)
        {
            FileSecurity dSecurity = file.GetAccessControl();
            // Add the FileSystemAccessRule to the security settings. 
            dSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            // Set the access control
            file.SetAccessControl(dSecurity);
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class TrebuchetOsWindowsEx 
{
    public static IEnumerable<ProcessData> EnumerateData(this ManagementObjectCollection collection)
    {
        var result = new List<ProcessData>();
        try
        {
            foreach (var process in collection)
            {
                try
                {
                    result.Add(new ProcessData(process));
                }
                catch (Exception)
                {
                    // Process vanished while being enumerated (killed/exited) — skip it.
                }
            }
        }
        catch (ManagementException)
        {
            // Enumeration aborted by WMI; return what we have.
        }
        return result;
    }
}
