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

    public IEnumerable<ProcessData> GetChildProcesses(int parentId)
    {
        var query = $"Select * From Win32_Process Where ParentProcessId = {parentId}";
        ManagementObjectSearcher searcher = new ManagementObjectSearcher(query);
        ManagementObjectCollection processList = searcher.Get();

        return processList.EnumerateData();
    }

    public ProcessData GetFirstChildProcesses(int parentId)
    {
        var query = $"Select * From Win32_Process Where ParentProcessId = {parentId}";
        ManagementObjectSearcher searcher = new ManagementObjectSearcher(query);
        ManagementObjectCollection processList = searcher.Get();

        if (processList.Count == 0) return ProcessData.Empty;

        return new ProcessData(processList.Cast<ManagementObject>().First());
    }

    public ProcessData GetProcess(int processId)
    {
        var query = $"Select * From Win32_Process Where ProcessId = {processId}";
        ManagementObjectSearcher searcher = new ManagementObjectSearcher(query);
        ManagementObjectCollection processList = searcher.Get();

        if (processList.Count == 0) return ProcessData.Empty;

        return new ProcessData(processList.Cast<ManagementObject>().First());
    }

    public async Task<List<ProcessData>> GetProcessesWithName(string processName)
    {
        return await Task.Run(() =>
        {
            var query = $"Select * From Win32_Process Where Name='{processName}'";
            ManagementObjectSearcher searcher = new ManagementObjectSearcher(query);
            ManagementObjectCollection processList = searcher.Get();

            return processList.EnumerateData().ToList();
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
        foreach (var process in collection)
            yield return new ProcessData(process);
    }
}
