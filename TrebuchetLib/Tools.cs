using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamWorksWebAPI;
using tot_lib;
using tot_lib.OsSpecific;
using TrebuchetLib.Services;

namespace TrebuchetLib;

public static class Tools
{
    public static long Clamp2CPUThreads(long value)
    {
        int maxCPU = Environment.ProcessorCount;
        for (int i = 0; i < 64; i++)
            if (i >= maxCPU)
                value &= ~(1L << i);
        return value;
    }

    public static void CreateDir(string directory)
    {
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    public static void DeepCopy(string directory, string destinationDir)
    {
        foreach (string dir in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
        {
            string dirToCreate = dir.Replace(directory, destinationDir);
            Directory.CreateDirectory(dirToCreate);
        }

        var files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);
        foreach (string newPath in files)
        {
            File.Copy(newPath, newPath.Replace(directory, destinationDir), true);
            
        }
    }

    public static async Task DeepCopyAsync(string directory, string destinationDir, CancellationToken token, IProgress<double>? progress = null)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string dir in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
        {
            string dirToCreate = dir.Replace(directory, destinationDir);
            Directory.CreateDirectory(dirToCreate);
        }
        var dirInfo = new DirectoryInfo(directory);

        var files = dirInfo.GetFiles("*.*", SearchOption.AllDirectories);
        if (files.Length == 0) return;
        
        long total = files.Select(f => f.Length).Aggregate((a, b) => a + b);
        long count = 0;
        foreach (FileInfo file in files)
        {
            if (token.IsCancellationRequested)
                return;
            await Task.Run(() => File.Copy(file.FullName, file.FullName.Replace(directory, destinationDir), true));
            count += file.Length;
            progress?.Report((double)count / total);
        }
    }

    public static void RemoveAllJunctions(string directory)
    {

    }

    public static void DeleteIfExists(string file)
    {
        if (Directory.Exists(file))
            Directory.Delete(file, true);
        else if (File.Exists(file))
            File.Delete(file);
    }

    public static long DirectorySize(string folder) => DirectorySize(new DirectoryInfo(folder));

    public static long DirectorySize(DirectoryInfo folder)    {
        long size = 0;
        // Add file sizes.
        FileInfo[] fis = folder.GetFiles();
        foreach (FileInfo fi in fis)
        {
            size += fi.Length;
        }
        // Add subdirectory sizes.
        DirectoryInfo[] dis = folder.GetDirectories();
        foreach (DirectoryInfo di in dis)
        {
            size += DirectorySize(di);
        }
        return size;
    }

    public static async Task<string> DownloadModList(string url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url))
            throw new ArgumentException("Sync URL is invalid");

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        var contentLength = response.Content.Headers.ContentLength;
        if (response.Content.Headers.ContentLength > 1024 * 1024 * 10)
            throw new Exception("Content was too big.");
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new Exception("Content was not json.");

        return await response.Content.ReadAsStringAsync(ct);
    }

    public static async Task<string> GetFileContent(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        return await File.ReadAllTextAsync(path);
    }
        
    public static string GetFirstDirectoryName(string folder, string pattern)
    {
        if (!Directory.Exists(folder))
            return string.Empty;
        string[] profiles = Directory.GetDirectories(folder, pattern);
        if (profiles.Length == 0)
            return string.Empty;
        return Path.GetFileNameWithoutExtension(profiles[0]);
    }

    public static string GetFirstFileName(string folder, string pattern)
    {
        if (!Directory.Exists(folder))
            return string.Empty;
        string[] profiles = Directory.GetFiles(folder, pattern);
        if (profiles.Length == 0)
            return string.Empty;
        return Path.GetFileNameWithoutExtension(profiles[0]);
    }

    public static string GetRootPath()
    {
        return Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory) ?? throw new DirectoryNotFoundException("Assembly directory is not found.");
    }

    public static bool IsClientInstallValid(AppSetup setup)
    {
        return IsClientInstallValid(setup.Config.ClientPath, setup.IsEnhanced);
    }

    public static bool IsClientInstallValid(string directory, bool enhanced)
    {
        return !string.IsNullOrEmpty(directory) &&
               File.Exists(GetClientInstallPath(directory, enhanced));
    }
    
    public static string GetClientInstallPath(string directory, bool enhanced)
    {
        return Path.Combine(directory, Constants.FolderGameBinaries,
            enhanced ? Constants.FileClientEnhancedBin : Constants.FileClientBin);
    }

    public static bool IsDirectoryWritable(string dirPath, bool throwIfFails = false)
    {
        try
        {
            if(!Directory.Exists(dirPath))
                Directory.CreateDirectory(dirPath);
            using (FileStream fs = File.Create(
                       Path.Combine(
                           dirPath,
                           Path.GetRandomFileName()
                       ),
                       1,
                       FileOptions.DeleteOnClose)
                  )
            { }
            return true;
        }
        catch
        {
            if (throwIfFails)
                throw;
            else
                return false;
        }
    }

    public static bool IsRunning(this ProcessState state)
    {
        return state is 
            ProcessState.RUNNING or 
            ProcessState.STOPPING or 
            ProcessState.ONLINE;
    }

    public static bool IsStopping(this ProcessState state)
    {
        return state is
            ProcessState.STOPPING;
    }

    public static bool IsServerInstallValid(Config config)
    {
        return config.ServerInstanceCount > 0;
    }

    public static bool IsSymbolicLink(string path)
    {
        return Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }

    public static string PosixFullName(this string path) => path.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Reads a null-terminated string into a c# compatible string.</summary>
    /// <param name="input">Binary reader to pull the null-terminated string from.  Make sure it is correctly positioned in the stream before calling.</param>
    /// <returns>String of the same encoding as the input BinaryReader.</returns>
    public static string? ReadNullTerminatedString(this BinaryReader input)
    {
        StringBuilder sb = new StringBuilder();
        char read = input.ReadChar();
        while (read != '\x00')
        {
            sb.Append(read);
            read = input.ReadChar();
        }
        string result = sb.ToString();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    public static string RemoveExtension(this string path) => path[..^Path.GetExtension(path).Length];

    public static string RemoveRootFolder(this string path, string root)
    {
        string result = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Substring(root.Length);
        return result.StartsWith("\\") ? result.Substring(1) : result;
    }

    public static async Task SetFileContent(string path, string content)
    {
        string? folder = Path.GetDirectoryName(path);
        if (folder == null) throw new Exception($"Invalid folder for {path}.");
        CreateDir(folder);
        await File.WriteAllTextAsync(path, content);
    }

    public static IEnumerable<string> Split(this string str, Func<char, bool> controller)
    {
        int nextPiece = 0;

        for (int c = 0; c < str.Length; c++)
        {
            if (controller(str[c]))
            {
                yield return str.Substring(nextPiece, c - nextPiece);
                nextPiece = c + 1;
            }
        }

        yield return str.Substring(nextPiece);
    }

    public static IEnumerable<string> SplitCommandLine(string commandLine)
    {
        bool inQuotes = false;

        return commandLine.Split(c =>
            {
                if (c == '\"')
                    inQuotes = !inQuotes;

                return !inQuotes && c == ' ';
            })
            .Select(arg => arg.Trim().TrimMatchingQuotes('\"'))
            .Where(arg => !string.IsNullOrEmpty(arg));
    }

    public static string TrimMatchingQuotes(this string input, char quote)
    {
        if ((input.Length >= 2) && (input[0] == quote) && (input[input.Length - 1] == quote))
            return input[1..^1];

        return input;
    }

    public static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
    {
        // Unix timestamp is seconds past epoch
        DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        dateTime = dateTime.AddSeconds(unixTimeStamp).ToUniversalTime();
        return dateTime;
    }

    public static void UnzipFile(string file, string destination)
    {
        using (ZipArchive archive = ZipFile.OpenRead(file))
            foreach (ZipArchiveEntry entry in archive.Entries)
                entry.ExtractToFile(Path.Join(destination, entry.FullName));
    }

    public static bool ValidateInstallDirectory(string installPath)
    {
        if (string.IsNullOrEmpty(installPath))
            return false;
        if (!Directory.Exists(installPath))
            return false;
        if (Regex.IsMatch(installPath, Constants.RegexSavedFolder))
            return false;
        return true;
    }

    public static void WriteNullTerminatedString(this BinaryWriter writer, string content)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(content);
        writer.Write(bytes);
        writer.Write((byte)0);
    }
        
            
    /// <summary>
    /// Get the map preset list saved in JSon/Maps.json.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public static Dictionary<string, string> GetMapList()
    {
        string? appFolder = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory);
        if (string.IsNullOrEmpty(appFolder)) throw new Exception("Path to assembly is invalid.");

        string file = Path.Combine(appFolder, Constants.FileMapJson);
        if (!File.Exists(file)) throw new Exception("Map list file is missing.");

        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
        if (data == null) throw new Exception("Map list could ne be parsed.");

        return data;
    }
    
    public static IEnumerable<(ulong, ulong)> GetManifestKeyValuePairs(this List<PublishedFile> list)
    {
        return list.AsEnumerable().GetManifestKeyValuePairs();
    }

    public static IEnumerable<(ulong, ulong)> GetManifestKeyValuePairs(this IEnumerable<PublishedFile> list)
    {
        foreach (var file in list)
        {
            if (ulong.TryParse(file.HcontentFile, out var manifest))
                yield return (file.PublishedFileID, manifest);
        }
    }
}