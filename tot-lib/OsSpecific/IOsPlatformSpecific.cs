using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace tot_lib.OsSpecific;

public interface IOsPlatformSpecific
{
    bool TryGetLogonRun(string appName, [NotNullWhen(true)] out string? data);
    bool SetLogonRun(string appName, string data);
    bool RemoveLogonRun(string appName);
    bool IsProcessElevated();
    void RemoveSymbolicLink(string path);
    void MakeSymbolicLink(string path, string targetPath);
    void MakeFileSymbolicLink(string linkPath, string targetPath);
    bool IsSymbolicLink(string path);
    string GetSymbolicLinkTarget(string path);
}

public static class OsPlatformSpecificExtensions
{
    public static bool HasLogonRun(this IOsPlatformSpecific os, string appName)
    {
        return os.TryGetLogonRun(appName, out _);
    }

    public static IOsPlatformSpecific GetOsPlatformSpecific()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new OsPlatformWindows();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new OsPlatformLinux();
        throw new NotSupportedException("OS not supported");
    }

    public static void RemoveAllSymbolicLinks(this IOsPlatformSpecific osSpecific, string directory)
    {
        foreach (string dir in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(dir) && File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                osSpecific.RemoveSymbolicLink(dir);
        }
    }
}