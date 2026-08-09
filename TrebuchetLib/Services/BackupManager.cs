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
        DeleteOldBackup(prefix, maxAge);
        
        var path = appFiles.Server.GetPath(reference);
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
            throw new DirectoryNotFoundException($"Directory not found ({path})");
        await PerformServerBackup(directory, prefix);
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
        DeleteOldBackup(prefix, maxAge);
        
        var path = appFiles.Client.GetPath(reference);
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
            throw new DirectoryNotFoundException($"Directory not found ({path})");
        await PerformServerBackup(directory, prefix);
    }

    private async Task PerformServerBackup(string directory, string filePrefix)
    {
        var timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
        var filename = Path.Combine(GetBackupDirectory(), $"{filePrefix}.{timestamp}.zip");
        if (File.Exists(filename))
            return;

        try
        {
            await using var fileStream = new FileStream(filename, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await Task.Run(() => ZipFile.CreateFromDirectory(directory, fileStream));
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to perform backup", ex);
        }
    }

    private void DeleteOldBackup(string prefix, TimeSpan maxAge)
    {
        var directory = GetBackupDirectory();
        var files = new DirectoryInfo(directory).GetFiles($"{prefix}.*.zip");
        var maxDate = DateTime.UtcNow - maxAge;
        foreach (var file in files)
        {
            if(file.LastWriteTimeUtc <= maxDate)
                file.Delete();
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