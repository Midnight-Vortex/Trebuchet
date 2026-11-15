using TrebuchetLib.Services;

namespace TrebuchetLib;

/// <summary>What stands against a new directory for the saves and profiles.</summary>
public enum ProfileDirectoryCheck
{
    Ok,
    /// <summary>It is the directory in use.</summary>
    Same,
    /// <summary>Missing or not writable.</summary>
    Invalid,
    /// <summary>A network path: the game reaches its saves through a junction, which cannot point there.</summary>
    Network,
    /// <summary>Inside the saves in use (or they inside it), or inside a folder that is not for them (the game).</summary>
    Overlap,
    /// <summary>It already holds saves or profiles: they are never mixed with the ones in use.</summary>
    HasData,
    /// <summary>It holds a folder with a name of ours (Backups) that is not from Trebuchet: nothing is put into it.</summary>
    Occupied,
    /// <summary>It holds what a move that did not finish left behind (<see cref="ProfileDirectoryMove.Discard"/> removes it).</summary>
    Unfinished
}

/// <summary>
/// Moves the saves and profiles of one edition (game saves, server saves, mod lists, sync lists, backups) to another
/// root: copy, compare, then the caller switches the settings and restarts, and the old folders are removed at the
/// next start (<see cref="FinishCleanup"/>). Nothing is ever merged into folders that hold data, links are never
/// followed, and the extracted mods inside a save are left behind (the game makes them again).
/// </summary>
public static class ProfileDirectoryMove
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The folders of an edition below the root, relative to it.</summary>
    public static IReadOnlyList<string> GetItems(GameEdition edition)
    {
        var version = Constants.GetVersionFolder(edition);
        return
        [
            Path.Combine(version, Constants.FolderClientProfiles),
            Path.Combine(version, Constants.FolderServerProfiles),
            Path.Combine(version, Constants.FolderModlistProfiles),
            Path.Combine(version, Constants.FolderSyncProfiles),
            // The backups of Legacy lie next to its edition folder (BackupManager).
            edition == GameEdition.Legacy ? Constants.FolderBackup : Path.Combine(version, Constants.FolderBackup)
        ];
    }

    /// <param name="forbidden">Folders the target must not lie in (the game installation).</param>
    public static ProfileDirectoryCheck Check(string sourceRoot, string targetRoot, GameEdition edition, IEnumerable<string> forbidden)
    {
        if (string.IsNullOrWhiteSpace(targetRoot)) return ProfileDirectoryCheck.Invalid;
        // Told from the path alone: asking a server that does not answer takes long.
        if (targetRoot.TrimStart().StartsWith(@"\\", StringComparison.Ordinal)) return ProfileDirectoryCheck.Network;
        if (!AppFiles.IsDirectoryValidForData(targetRoot)) return ProfileDirectoryCheck.Invalid;
        var source = Normalize(sourceRoot);
        var target = Normalize(targetRoot);
        if (source.Equals(target, PathComparison)) return ProfileDirectoryCheck.Same;
        if (IsNetworkDrive(target)) return ProfileDirectoryCheck.Network;
        if (forbidden.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => IsSameOrInside(target, Normalize(x))))
            return ProfileDirectoryCheck.Overlap;

        var items = GetItems(edition);
        foreach (var item in items)
        {
            var from = Path.Combine(source, item);
            var to = Path.Combine(target, item);
            if (IsSameOrInside(to, from) || IsSameOrInside(from, to) || IsSameOrInside(target, from))
                return ProfileDirectoryCheck.Overlap;
        }
        if (File.Exists(MarkerOf(target, edition))) return ProfileDirectoryCheck.Unfinished;
        var version = Constants.GetVersionFolder(edition) + Path.DirectorySeparatorChar;
        var inEdition = items.Where(x => x.StartsWith(version, PathComparison)).Any(x => HasEntries(Path.Combine(target, x)));
        var beside = items.Where(x => !x.StartsWith(version, PathComparison)).Any(x => HasEntries(Path.Combine(target, x)));
        // A Backups folder next to no saves of ours (Legacy keeps its backups beside the edition folder) belongs to someone else.
        if (beside && !inEdition) return ProfileDirectoryCheck.Occupied;
        return inEdition ? ProfileDirectoryCheck.HasData : ProfileDirectoryCheck.Ok;
    }

    /// <summary>
    /// Folders below the saves and profiles that are links (junctions, symbolic links), relative to the root: a save kept
    /// on another drive through a link. The copy leaves links out, so such a save would be missing in the new directory
    /// and its link would be removed with the old one: a move is refused while there are any.
    /// </summary>
    public static List<string> FindLinks(string root, GameEdition edition, CancellationToken token = default)
    {
        var links = new List<string>();
        foreach (var item in GetItems(edition))
        {
            var folder = new DirectoryInfo(Path.Combine(root, item));
            if (folder.Exists) FindLinks(folder, SkipOf(folder.FullName), Normalize(root), links, token);
        }
        return links;
    }

    private static void FindLinks(DirectoryInfo folder, Func<DirectoryInfo, bool> skip, string root, List<string> links, CancellationToken token)
    {
        foreach (var child in folder.EnumerateDirectories())
        {
            token.ThrowIfCancellationRequested();
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                links.Add(Path.GetRelativePath(root, child.FullName));
            else if (!skip(child))
                FindLinks(child, skip, root, links, token);
        }
    }

    /// <summary>Files and bytes of the folders of the edition, as the move copies them.</summary>
    public static (long Files, long Bytes) Measure(string root, GameEdition edition, CancellationToken token = default)
    {
        long files = 0, bytes = 0;
        foreach (var item in GetItems(edition))
        {
            var folder = Path.Combine(root, item);
            if (!Directory.Exists(folder)) continue;
            foreach (var entry in DirectoryCopy.Entries(folder, token, SkipOf(folder)))
                if (entry is FileInfo file)
                {
                    files++;
                    bytes += file.Length;
                }
        }
        return (files, bytes);
    }

    // Files, bytes and the times of the last change: tells whether something wrote into the folders in the meantime.
    private static (long Files, long Bytes, long Changed) Stamp(string root, GameEdition edition, CancellationToken token)
    {
        long files = 0, bytes = 0, changed = 0;
        foreach (var item in GetItems(edition))
        {
            var folder = Path.Combine(root, item);
            if (!Directory.Exists(folder)) continue;
            foreach (var entry in DirectoryCopy.Entries(folder, token, SkipOf(folder)))
                if (entry is FileInfo file)
                {
                    files++;
                    bytes += file.Length;
                    unchecked { changed += file.LastWriteTimeUtc.Ticks; }
                }
        }
        return (files, bytes, changed);
    }

    /// <summary>
    /// Copies the folders of the edition to the target and compares files and bytes; it fails when the source changed
    /// in the meantime. The target folders must be empty (<see cref="Check"/> is Ok) and the source free of links
    /// (<see cref="FindLinks"/>). When it fails or is cancelled, what was copied is removed again and it throws: the
    /// source is never touched. A note in the edition folder of the target marks the copy as not in use yet: it stays
    /// until <see cref="FinishCleanup"/> (the settings were switched) or <see cref="Discard"/> removes it, so a copy that
    /// a crash or power loss left behind is never taken for saves.
    /// </summary>
    public static async Task CopyAsync(string sourceRoot, string targetRoot, GameEdition edition, IProgress<double>? progress,
        CancellationToken token)
    {
        var items = GetItems(edition);
        // Checked again here, outside the try: a failed copy removes the target folders, which must be its own.
        if (items.Any(x => HasEntries(Path.Combine(targetRoot, x))))
            throw new IOException("The target already holds saves or profiles.");
        if (FindLinks(sourceRoot, edition, token) is { Count: > 0 } links)
            throw new IOException("Saves that are links cannot be moved: " + string.Join(", ", links));
        try
        {
            // The edition folder is what tells a later start that the directory is there.
            Directory.CreateDirectory(Path.Combine(targetRoot, Constants.GetVersionFolder(edition)));
            File.WriteAllText(MarkerOf(targetRoot, edition),
                "Trebuchet copies saves and profiles into this folder. Until this file is gone the copy is not in use and may be incomplete.");
            var before = Stamp(sourceRoot, edition, token);
            for (var i = 0; i < items.Count; i++)
            {
                var from = Path.Combine(sourceRoot, items[i]);
                if (!Directory.Exists(from)) continue;
                var index = i;
                var part = progress is null ? null : new PartProgress(progress, index, items.Count);
                await DirectoryCopy.CopyAsync(from, Path.Combine(targetRoot, items[i]), token, part, SkipOf(from)).ConfigureAwait(false);
                CopyAttributes(from, Path.Combine(targetRoot, items[i]), token);
            }

            var expected = Measure(sourceRoot, edition, token);
            var copied = Measure(targetRoot, edition, token);
            if (expected != copied)
                throw new IOException(
                    $"The copy differs from the original ({copied.Files} of {expected.Files} files, {copied.Bytes} of {expected.Bytes} bytes).");
            if (Stamp(sourceRoot, edition, token) != before)
                throw new IOException("The saves or profiles changed while they were copied.");
            progress?.Report(1);
        }
        catch
        {
            Discard(targetRoot, edition);
            throw;
        }
    }

    /// <summary>
    /// Removes a copy that is not in use (a failed or abandoned move, or what an unfinished one left behind) with its
    /// note. The note stays when not everything could be removed, so the rest is still known as a leftover.
    /// </summary>
    public static bool Discard(string root, GameEdition edition)
    {
        if (!Delete(root, edition)) return false;
        RemoveMarker(root, edition);
        return true;
    }

    // Write protection and hidden files are kept (a write protected ini the game must not rewrite); the copy only moves content.
    private static void CopyAttributes(string from, string to, CancellationToken token)
    {
        const FileAttributes kept = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
        foreach (var entry in DirectoryCopy.Entries(from, token, SkipOf(from)))
        {
            if (entry is not FileInfo file || (file.Attributes & kept) == 0) continue;
            var target = Path.Combine(to, Path.GetRelativePath(from, file.FullName));
            File.SetAttributes(target, File.GetAttributes(target) | (file.Attributes & kept));
        }
    }

    private static string MarkerOf(string root, GameEdition edition)
        => Path.Combine(root, Constants.GetVersionFolder(edition), ".trebuchet-move-unfinished");

    private static void RemoveMarker(string root, GameEdition edition)
    {
        try
        {
            var marker = MarkerOf(root, edition);
            if (File.Exists(marker)) File.Delete(marker);
            RemoveEmptyEditionFolder(root, edition);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A note that stays only makes a later move clean up once more.
        }
    }

    private static void RemoveEmptyEditionFolder(string root, GameEdition edition)
    {
        var version = new DirectoryInfo(Path.Combine(root, Constants.GetVersionFolder(edition)));
        if (version.Exists && (version.Attributes & FileAttributes.ReparsePoint) == 0 && !version.EnumerateFileSystemInfos().Any())
            version.Delete(false);
    }

    /// <summary>
    /// Removes the folders of the edition below the root, then the edition folder when that left it empty. A link is
    /// removed as a link, what it points to stays. Best effort.
    /// </summary>
    /// <returns>True when nothing of them is left.</returns>
    public static bool Delete(string root, GameEdition edition)
    {
        var clean = true;
        foreach (var item in GetItems(edition))
            clean &= DeleteTree(new DirectoryInfo(Path.Combine(root, item)));
        try
        {
            RemoveEmptyEditionFolder(root, edition);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty folder that stays is no harm.
        }
        return clean;
    }

    /// <summary>
    /// After a move and the restart: removes the folders the data was moved away from, and the note that marked the
    /// copy as not in use. The old folders go only when the directory in use is another one and holds at least what
    /// they hold. While the directory in use is not there (drive not connected) nothing is done and it is tried again
    /// at the next start; otherwise it is tried once.
    /// </summary>
    /// <returns>What was done, for the log; null when there was nothing to do.</returns>
    public static string? FinishCleanup(AppSetup setup)
    {
        var old = setup.Config.ProfileDirectoryCleanup;
        if (string.IsNullOrWhiteSpace(old)) return null;
        string result;
        try
        {
            var current = Normalize(setup.GetDataDirectory().FullName);
            if (!Directory.Exists(Path.Combine(current, setup.VersionFolder)))
                return $"The old saves and profiles in {old} are kept for now: the directory in use ({current}) is not there.";
            if (Normalize(old).Equals(current, PathComparison))
                result = $"The old saves and profiles in {old} were kept: it is the directory in use.";
            else
            {
                var was = Measure(old, setup.Edition);
                var now = Measure(current, setup.Edition);
                if (now.Files < was.Files || now.Bytes < was.Bytes)
                    result = $"The old saves and profiles in {old} were kept: the directory in use holds less ({now.Files} of {was.Files} files, {now.Bytes} of {was.Bytes} bytes). Compare them by hand.";
                else
                    result = Delete(old, setup.Edition)
                        ? $"The saves and profiles were moved, the old ones in {old} are removed."
                        : $"The saves and profiles were moved, but not all old files in {old} could be removed. Remove them by hand.";
            }
            RemoveMarker(current, setup.Edition);
        }
        catch (Exception ex)
        {
            result = $"The old saves and profiles in {old} could not be removed ({ex.Message}).";
        }
        setup.Config.ProfileDirectoryCleanup = string.Empty;
        try { setup.Config.SaveFile(); }
        catch (Exception ex) { result += $" The settings could not be saved ({ex.Message})."; }
        return result;
    }

    /// <summary>The same place below the new root when the path lies in a folder that was moved, otherwise null.</summary>
    public static string? MapPath(string path, string sourceRoot, string targetRoot, GameEdition edition)
    {
        var full = Normalize(path);
        foreach (var item in GetItems(edition))
        {
            var from = Path.Combine(Normalize(sourceRoot), item);
            if (IsSameOrInside(full, from))
                return Path.Combine(Normalize(targetRoot), item) + full[from.Length..];
        }
        return null;
    }

    // The extracted mods of a save: <item>\<save>\ExtractedMods (the game writes them into its Saved folder).
    private static Func<DirectoryInfo, bool> SkipOf(string item)
    {
        var root = Normalize(item);
        return folder => folder.Name.Equals(Constants.FolderExtractedMods, PathComparison)
                         && folder.Parent?.Parent is { } parent && Normalize(parent.FullName).Equals(root, PathComparison);
    }

    private static bool DeleteTree(DirectoryInfo folder)
    {
        try
        {
            if (!folder.Exists) return true;
            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                folder.Delete(false);
                return true;
            }

            var clean = true;
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo child)
                {
                    clean &= DeleteTree(child);
                    continue;
                }
                try
                {
                    if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
                    entry.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    clean = false;
                }
            }
            if (clean) folder.Delete(false);
            return clean;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasEntries(string folder)
    {
        try { return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static bool IsNetworkDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSameOrInside(string path, string folder)
        => path.Equals(folder, PathComparison) || path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // Reports at once and in order (Progress<T> posts its reports, they could arrive after the copy ended).
    private sealed class PartProgress(IProgress<double> target, int index, int count) : IProgress<double>
    {
        public void Report(double value) => target.Report((index + Math.Clamp(value, 0, 1)) / count);
    }
}
