using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace tot_lib.OsSpecific;

[SupportedOSPlatform("windows")]
public class OsPlatformWindows : IOsPlatformSpecific
{
    private const int SymbolicLinkFlagFile = 0;
    private const int SymbolicLinkFlagAllowUnprivilegedCreate = 0x2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);

    public bool TryGetLogonRun(string appName, [NotNullWhen(true)] out string? data)
    {
        data = null;
        try
        {
            var rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (rk is null) return false;

            data = rk.GetValue(appName) as string;
            return data != null;
        }
        catch
        {
            return false;
        }
    }
    
    public bool SetLogonRun(string appName, string data)
    {
        try
        {
            var rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (rk is null) return false;
            rk.SetValue(appName, data);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public bool RemoveLogonRun(string appName)
    {
        try
        {
            var rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (rk is null) return false;
            rk.DeleteValue(appName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsProcessElevated()
    {
        return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void RemoveSymbolicLink(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            File.Delete(path);
        else if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            JunctionPoint.Delete(path);
        else if (Directory.Exists(path))
            Directory.Delete(path, true);
    }

    public void MakeSymbolicLink(string path, string targetPath)
    {
        if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            JunctionPoint.Delete(path);
        else if (Directory.Exists(path) || File.Exists(path))
            throw new Exception($"Can't setup Symbolic link at {path}");
        JunctionPoint.Create(path, targetPath, true);
    }

    public void MakeFileSymbolicLink(string linkPath, string targetPath)
    {
        if (Directory.Exists(linkPath))
            throw new IOException($"Can't create file symbolic link at {linkPath}: path is a directory.");

        if (File.Exists(linkPath))
        {
            if (File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
                File.Delete(linkPath);
            else
                File.Delete(linkPath);
        }

        targetPath = Path.GetFullPath(targetPath);
        if (!CreateSymbolicLink(linkPath, targetPath, SymbolicLinkFlagFile | SymbolicLinkFlagAllowUnprivilegedCreate))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public bool IsSymbolicLink(string path)
    {
        if (JunctionPoint.Exists(path))
            return true;

        return File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }

    public string GetSymbolicLinkTarget(string path)
    {
        if (JunctionPoint.Exists(path))
            return JunctionPoint.GetTarget(path);

        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            return ResolveLinkTarget(path, new FileInfo(path).LinkTarget);

        return string.Empty;
    }

    private static string ResolveLinkTarget(string linkPath, string? target)
    {
        if (string.IsNullOrEmpty(target))
            return string.Empty;

        if (Path.IsPathRooted(target))
            return Path.GetFullPath(target);

        var baseDir = Path.GetDirectoryName(linkPath);
        return baseDir is null ? target : Path.GetFullPath(Path.Combine(baseDir, target));
    }
}
