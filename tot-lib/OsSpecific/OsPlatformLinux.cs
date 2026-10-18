using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace tot_lib.OsSpecific;

[SupportedOSPlatform("linux")]
public class OsPlatformLinux : IOsPlatformSpecific
{
    public bool TryGetLogonRun(string appName, [NotNullWhen(true)] out string? data)
    {
        data = null;
        return false;
    }

    public bool SetLogonRun(string appName, string data)
    {
        return false;
    }

    public bool RemoveLogonRun(string appName)
    {
        return false;
    }

    public bool IsProcessElevated()
    {
        return false;
    }

    public void RemoveSymbolicLink(string path)
    {
        if (IsSymbolicLink(path))
            File.Delete(path);
        else if (Directory.Exists(path))
            Directory.Delete(path, true);
    }

    public void MakeSymbolicLink(string path, string targetPath)
    {
        if (IsSymbolicLink(path))
            File.Delete(path);
        else if (Directory.Exists(path) || File.Exists(path))
            throw new Exception($"Can't setup Symbolic link at {path}");

        Directory.CreateSymbolicLink(path, targetPath);
    }

    public void MakeFileSymbolicLink(string linkPath, string targetPath)
    {
        if (Directory.Exists(linkPath))
            throw new IOException($"Can't create file symbolic link at {linkPath}: path is a directory.");

        if (File.Exists(linkPath) || IsSymbolicLink(linkPath))
            File.Delete(linkPath);

        File.CreateSymbolicLink(linkPath, targetPath);
    }

    public bool IsSymbolicLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public string GetSymbolicLinkTarget(string path)
    {
        if (!IsSymbolicLink(path))
            return string.Empty;

        return ResolveLinkTarget(path, new FileInfo(path).LinkTarget);
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
