using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.Services;

public class BackupManager(AppSetup setup, AppFiles appFiles)
{
    public Task PerformServerBackup(int serverInstance, TimeSpan maxAge)
    {
        var uri = setup.Config.GetInstanceProfile(serverInstance);
        if (appFiles.Server.TryResolve(uri, out var reference))
            return PerformServerBackup(reference, maxAge);
        throw new Exception($"Invalid profile for server instance {serverInstance}");
    }
    
    public async Task PerformServerBackup(ServerProfileRef reference, TimeSpan maxAge)
    {
        var prefix = $"Server.{reference.Name}";
        var path = appFiles.Server.GetPath(reference);
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
            throw new DirectoryNotFoundException($"Directory not found ({path})");
        await PerformServerBackup(directory, prefix);
        // Old backups go only once a new one exists: failing backups left nothing but expired ones deleted.
        DeleteOldBackup(prefix, maxAge);
    }

    public Task PerformClientBackup(TimeSpan maxAge)
    {
        var uri = setup.Config.SelectedClientProfile;
        if (appFiles.Client.TryResolve(uri, out var reference))
            return PerformClientBackup(reference, maxAge);
        throw new Exception("Invalid current profile for client");
    }
    
    public async Task PerformClientBackup(ClientProfileRef reference, TimeSpan maxAge)
    {
        var prefix = $"Client.{reference.Name}";
        var path = appFiles.Client.GetPath(reference);
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
            throw new DirectoryNotFoundException($"Directory not found ({path})");
        await PerformServerBackup(directory, prefix);
        DeleteOldBackup(prefix, maxAge);
    }

    /// <summary>
    /// DSL style backup after a server shutdown: game databases, last log, the three server ini files and the
    /// black/white lists go into &lt;target&gt;/&lt;day&gt;/&lt;time&gt;. Optionally only the newest day folders are kept.
    /// </summary>
    public async Task<string> PerformShutdownBackup(ServerProfileRef reference)
    {
        var profile = reference.Get();
        var root = string.IsNullOrWhiteSpace(profile.BackupDirectory)
            ? Path.Combine(GetBackupDirectory(), $"Server.{reference.Name}")
            : Environment.ExpandEnvironmentVariables(profile.BackupDirectory.Trim());
        var now = DateTime.Now;
        var target = Path.Combine(root, now.ToString("yyyy-MM-dd"), now.ToString("HH-mm-ss"));

        await Task.Run(() =>
        {
            Directory.CreateDirectory(target);
            try
            {
                foreach (var (source, relative) in GetShutdownBackupFiles(profile.ProfileFolder))
                {
                    var destination = Path.Combine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, true);
                }
            }
            catch
            {
                // A half copied backup looks complete later: remove it.
                try { Directory.Delete(target, true); } catch { /* best effort */ }
                throw;
            }

            if (profile is { BackupLimitEnabled: true, BackupLimitDays: > 0 })
                TrimDayFolders(root, profile.BackupLimitDays);
        });
        return target;
    }

    /// <summary>Files of a server profile (Saved folder) that are part of a shutdown backup.</summary>
    public static IEnumerable<(string source, string relative)> GetShutdownBackupFiles(string savedFolder)
    {
        if (!Directory.Exists(savedFolder)) yield break;
        foreach (var db in Directory.EnumerateFiles(savedFolder, "*.db"))
        {
            if (!IsBackedUpDatabase(Path.GetFileName(db))) continue;
            yield return (db, Path.GetFileName(db));
            // SQLite write-ahead files hold data not yet merged into the database.
            foreach (var companion in new[] { db + "-wal", db + "-shm" })
                if (File.Exists(companion))
                    yield return (companion, Path.GetFileName(companion));
        }

        var candidates = new[]
        {
            Path.Combine("Logs", Constants.FileGameLogFile),
            Path.Combine("Config", "WindowsServer", "Game.ini"),
            Path.Combine("Config", "WindowsServer", "Engine.ini"),
            Path.Combine("Config", "WindowsServer", "ServerSettings.ini"),
            "blacklist.txt",
            "whitelist.txt"
        };
        foreach (var relative in candidates)
        {
            var path = Path.Combine(savedFolder, relative);
            if (File.Exists(path)) yield return (path, relative);
        }
    }

    /// <summary>
    /// DSL 1.9.0 rules: any game db (game.db, game_0.db on UE5, DLC_Siptah.db, custom maps), except the game's own
    /// copies whose name contains "backup" or "upgrade_tags". (The older "_number" rule would skip UE5's game_0.db.)
    /// </summary>
    public static bool IsBackedUpDatabase(string fileName)
    {
        if (!fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.Contains("backup", StringComparison.OrdinalIgnoreCase)) return false;
        return !fileName.Contains("upgrade_tags", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Keep only the newest <paramref name="keep"/> day folders (yyyy-MM-dd).</summary>
    public static void TrimDayFolders(string root, int keep)
    {
        if (!Directory.Exists(root)) return;
        var days = new DirectoryInfo(root).GetDirectories()
            .Where(d => DateTime.TryParseExact(d.Name, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _))
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();
        foreach (var day in days)
        {
            try
            {
                day.Delete(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder in use stays until the next backup: trimming is best effort and never fails the backup.
            }
        }
    }

    private async Task PerformServerBackup(string directory, string filePrefix)
    {
        var timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
        var filename = Path.Combine(GetBackupDirectory(), $"{filePrefix}.{timestamp}.zip");
        if (File.Exists(filename))
            return;

        // Written under a temporary name: a failed backup left a truncated zip that looked like a good one.
        var temporary = filename + ".tmp";
        try
        {
            await using (var fileStream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read))
                await Task.Run(() => ZipFile.CreateFromDirectory(directory, fileStream));
            File.Move(temporary, filename);
        }
        catch (Exception ex)
        {
            try { File.Delete(temporary); } catch { /* best effort */ }
            throw new Exception("Failed to perform backup", ex);
        }
    }

    private void DeleteOldBackup(string prefix, TimeSpan maxAge)
    {
        // The new backup exists already: a locked or missing old one must not turn it into a failed backup (and cancel
        // a sequence that stops on failure).
        try
        {
            var directory = GetBackupDirectory();
            var files = new DirectoryInfo(directory).GetFiles($"{prefix}.*.zip");
            var maxDate = DateTime.UtcNow - maxAge;
            foreach (var file in files)
            {
                try
                {
                    if (file.LastWriteTimeUtc <= maxDate)
                        file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Tried again with the next backup.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same: old backups are cleaned up best effort.
        }
    }

    private string GetBackupDirectory()
    {
        var basePath = setup.GetDataDirectory().FullName;
        if (setup.IsEnhanced)
            basePath = Path.Combine(basePath, setup.VersionFolder);

        var directory = Path.Combine(basePath, Constants.FolderBackup);
        Directory.CreateDirectory(directory);
        return directory;
    }
}