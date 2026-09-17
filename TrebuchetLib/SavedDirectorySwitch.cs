using tot_lib.OsSpecific;

namespace TrebuchetLib;

public static class SavedDirectorySwitch
{
    /// <summary>Returns a retained backup path if cleanup fails after a successful switch.</summary>
    public static string? ReplaceWithLink(string savedDirectory, string linkTarget, IOsPlatformSpecific os)
    {
        var source = Path.GetFullPath(savedDirectory);
        if (os.IsSymbolicLink(source)) throw new IOException("Expected an ordinary Saved directory.");
        var backup = source + ".trebuchet-backup-" + Guid.NewGuid().ToString("N");
        // The backup is a unique sibling of the caller's explicit source, on the same volume.
        Directory.Move(source, backup);
        try { os.MakeSymbolicLink(source, linkTarget); }
        catch
        {
            if (os.IsSymbolicLink(source)) os.RemoveSymbolicLink(source);
            else if (Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(source).Any())
                Directory.Delete(source);
            Directory.Move(backup, source);
            throw;
        }
        try { Directory.Delete(backup, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return backup; }
        return null;
    }
}
