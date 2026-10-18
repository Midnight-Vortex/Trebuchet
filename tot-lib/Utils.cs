using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net.Mime;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using tot_lib.Git;
using tot_lib.OsSpecific;

namespace tot_lib;

public static class Utils
{
    public static DirectoryInfo GetProperCasedDirectoryInfo(this DirectoryInfo dirInfo)
    {
        if (!dirInfo.Exists) return dirInfo;

        if (dirInfo.Parent == null) return dirInfo;

        return dirInfo.Parent.GetProperCasedDirectoryInfo().GetDirectories(dirInfo.Name)[0];
    }

    public static FileInfo GetProperCasedFileInfo(this FileInfo fileInfo)
    {
        if (!fileInfo.Exists)
            // Will not be able to match filesystem
            return fileInfo;

        if (fileInfo.Directory == null) return fileInfo;

        return fileInfo.Directory.GetProperCasedDirectoryInfo().GetFiles(fileInfo.Name)[0];
    }

    public static string PosixFullName(this FileInfo fileInfo)
    {
        return fileInfo.FullName.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string PosixFullName(this DirectoryInfo dirInfo)
    {
        return dirInfo.FullName.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string RemoveBaseDir(this string dirPath, DirectoryInfo parent)
    {
        return dirPath.Substring(parent.FullName.Length + 1);
    }

    public static string PosixFullName(this string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Reads a null-terminated string into a c# compatible string.</summary>
    /// <param name="input">
    ///     Binary reader to pull the null-terminated string from.  Make sure it is correctly positioned in the
    ///     stream before calling.
    /// </param>
    /// <returns>String of the same encoding as the input BinaryReader.</returns>
    public static string? ReadNullTerminatedString(this BinaryReader input)
    {
        var sb = new StringBuilder(1000);
        var read = input.ReadChar();
        while (read != '\x00')
        {
            sb.Append(read);
            read = input.ReadChar();
        }

        var result = sb.ToString();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    public static string RemoveExtension(this string path)
    {
        return path[..^Path.GetExtension(path).Length];
    }

    public static string RemoveRootFolder(this string path, string root)
    {
        var result = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Substring(root.Length);
        return result.StartsWith("\\") ? result.Substring(1) : result;
    }

    public static string RemoveRootFolder(this string path, FileInfo root)
    {
        return RemoveRootFolder(path, root?.DirectoryName ?? "");
    }

    public static string RemoveRootFolder(this string path, DirectoryInfo root)
    {
        return RemoveRootFolder(path, root.FullName);
    }

    public static T? GetAssemblyAttribute<T>(this Type type) where T : Attribute
    {
        Assembly assembly = type.Assembly;
        var attributes = assembly.GetCustomAttributes(typeof(T), true).FirstOrDefault();
        if(attributes is null) return null;
        return (attributes as T);
    }

    public static DirectoryInfo GetStandardFolder(this Type type, Environment.SpecialFolder folder)
    {
        string company = type.GetAssemblyAttribute<AssemblyCompanyAttribute>()?.Company ?? string.Empty;
        if (string.IsNullOrEmpty(company)) company = "tot-software";
        string product = type.GetAssemblyAttribute<AssemblyProductAttribute>()?.Product ?? string.Empty;
        if (string.IsNullOrEmpty(product)) product = "tot-default";
        var appData = Environment.GetFolderPath(folder);
        var directory = new DirectoryInfo(Path.Combine(appData, company, product));
        return directory;
    }
    
    const int ERROR_SHARING_VIOLATION = 32;
    const int ERROR_LOCK_VIOLATION = 33;
    public static bool IsFileLocked(Exception exception)
    {
        var errorCode = exception.GetErrorCode();
        return errorCode == ERROR_SHARING_VIOLATION || errorCode == ERROR_LOCK_VIOLATION;
    }

    public static int GetErrorCode(this Exception exception)
    {
        return Math.Max(1,Marshal.GetHRForException(exception) & ((1 << 16) - 1));
    }
    
    public static async Task WaitUntil(Func<bool> condition, int frequency = 25, int timeout = -1)
    {
        var waitTask = Task.Run(async () =>
        {
            while (!condition()) await Task.Delay(frequency);
        });

        if (waitTask != await Task.WhenAny(waitTask, 
                Task.Delay(timeout))) 
            throw new TimeoutException();
    }
    
    public static async Task WaitWhile(Func<bool> condition, int frequency = 25, int timeout = -1)
    {
        var waitTask = Task.Run(async () =>
        {
            while (condition()) await Task.Delay(frequency);
        });

        if(waitTask != await Task.WhenAny(waitTask, Task.Delay(timeout)))
            throw new TimeoutException();
    }
    
    public static string ToTitleCase(this string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (value.Length < 2)
        {
            return value.ToUpper();
        }

        return char.ToUpper(value[0]) + value[1..];
    }
    
    public static void WriteNullTerminatedString(this BinaryWriter writer, string content)
    {
        var bytes = Encoding.ASCII.GetBytes(content);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    public static IEnumerable<Type> GetTypesWithAttribute<T>(Assembly? assembly = null) where T : Attribute
    {
        IEnumerable<Type> types = assembly == null ? AppDomain.CurrentDomain.GetAssemblies().SelectMany(t => t.GetTypes()) : assembly.GetTypes();
        foreach (var type in types)
        {
            if (type.GetCustomAttributes(typeof(T), true).Length > 0)
            {
                yield return type;
            }
        }
    }

    public static IEnumerable<(Type type, T attr)> GetTypesWithSingleAttribute<T>(Assembly? assembly = null)
        where T : Attribute
    {
        IEnumerable<Type> types = assembly == null ? AppDomain.CurrentDomain.GetAssemblies().SelectMany(t => t.GetTypes()) : assembly.GetTypes();
        foreach (var type in types)
        {
            if (type.GetCustomAttributes(typeof(T), true).FirstOrDefault() is T attr)
            {
                yield return (type, attr);
            }
        }
    }
    
    public static string TrimMatchingQuotes(this string input, char quote)
    {
        if (input.Length >= 2 && input[0] == quote && input[^1] == quote)
            return input[1..^1];

        return input;
    }

    public static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
    {
        // Unix timestamp is seconds past epoch
        var dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        dateTime = dateTime.AddSeconds(unixTimeStamp).ToUniversalTime();
        return dateTime;
    }

    public static void UnzipFile(string file, string destination)
    {
        using var archive = ZipFile.OpenRead(file);
        foreach (var entry in archive.Entries)
            entry.ExtractToFile(Path.Join(destination, entry.FullName));
    }
    
    public static IEnumerable<string> SplitCommandLine(string commandLine)
    {
        var inQuotes = false;

        return commandLine.Split(c =>
            {
                if (c == '\"')
                    inQuotes = !inQuotes;

                return !inQuotes && c == ' ';
            })
            .Select(arg => arg.Trim().TrimMatchingQuotes('\"'))
            .Where(arg => !string.IsNullOrEmpty(arg));
    }
    
    public static IEnumerable<string> Split(this string str, Func<char, bool> controller)
    {
        var nextPiece = 0;

        for (var c = 0; c < str.Length; c++)
        {
            if (controller(str[c]))
            {
                yield return str.Substring(nextPiece, c - nextPiece);
                nextPiece = c + 1;
            }
        }

        yield return str.Substring(nextPiece);
    }
    
    public static bool IsDirectoryWritable(string dirPath, bool throwIfFails = false)
    {
        try
        {
            using var fs = File.Create(
                Path.Combine(
                    dirPath,
                    Path.GetRandomFileName()
                ),
                1,
                FileOptions.DeleteOnClose);

            return true;
        }
        catch
        {
            if (throwIfFails)
                throw;
            return false;
        }
    }

    public static bool IsSymbolicLink(string path)
    {
        return Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }
    
    public static string GetRootPath()
    {
        return Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory) ??
               throw new DirectoryNotFoundException("Assembly directory is not found.");
    }
    
    public static void DeleteIfExists(string file)
    {
        if (Directory.Exists(file))
            Directory.Delete(file, true);
        else if (File.Exists(file))
            File.Delete(file);
    }

    public static long DirectorySize(string folder)
    {
        return DirectorySize(new DirectoryInfo(folder));
    }

    public static long DirectorySize(DirectoryInfo folder)
    {
        long size = 0;
        // Add file sizes.
        var fis = folder.GetFiles();
        foreach (var fi in fis)
        {
            size += fi.Length;
        }

        // Add subdirectory sizes.
        DirectoryInfo[] dis = folder.GetDirectories();
        foreach (var di in dis)
        {
            size += DirectorySize(di);
        }

        return size;
    }

    public static string GetFileContent(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        return File.ReadAllText(path);
    }

    public static string GetFirstDirectoryName(string folder, string pattern)
    {
        if (!Directory.Exists(folder))
            return string.Empty;
        var profiles = Directory.GetDirectories(folder, pattern);
        if (profiles.Length == 0)
            return string.Empty;
        return Path.GetFileNameWithoutExtension(profiles[0]);
    }

    public static string GetFirstFileName(string folder, string pattern)
    {
        if (!Directory.Exists(folder))
            return string.Empty;
        var profiles = Directory.GetFiles(folder, pattern);
        if (profiles.Length == 0)
            return string.Empty;
        return Path.GetFileNameWithoutExtension(profiles[0]);
    }
    
    public static void DeepCopy(string directory, string destinationDir)
    {
        foreach (var dir in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
        {
            var dirToCreate = dir.Replace(directory, destinationDir);
            Directory.CreateDirectory(dirToCreate);
        }

        foreach (var newPath in Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            File.Copy(newPath, newPath.Replace(directory, destinationDir), true);
        }
    }

    public static async Task DeepCopyAsync(string directory, string destinationDir, CancellationToken token)
    {
        foreach (var dir in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
        {
            var dirToCreate = dir.Replace(directory, destinationDir);
            Directory.CreateDirectory(dirToCreate);
        }

        foreach (var newPath in Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            if (token.IsCancellationRequested)
                return;

            await Task.Run(() => File.Copy(newPath, newPath.Replace(directory, destinationDir), true), token);
        }
    }
    
    public static void OpenWeb(string url)
    {
        var proc = new Process();
        var infos = new ProcessStartInfo
        {
            UseShellExecute = true,
            FileName = url
        };
        proc.StartInfo = infos;
        proc.Start();
    }

    public static string GetAllExceptions(this Exception ex)
    {
        var x = 0;
        var pattern = "EXCEPTION #{0}:\r\n{1}";
        var message = String.Format(pattern, ++x, ex.Message);
        message += "\r\n============\r\n" + ex.StackTrace;
        var inner = ex.InnerException;
        while (inner != null)
        {
            message += "\r\n============\r\n" + String.Format(pattern, ++x, inner.Message);
            message += "\r\n============\r\n" + inner.StackTrace;
            inner = inner.InnerException;
        }

        return message;
    }

    public static string GetEmbeddedTextFile(string path)
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(path) ??
                           throw new Exception($"Could not find resource {path}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string GetEmbeddedTextFile<T>(string path)
    {
        var assembly = typeof(T).Assembly;
        using var stream = assembly.GetManifestResourceStream(path) ??
                           throw new Exception($"Could not find resource {path}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static long Clamp2CpuThreads(long value)
    {
        var maxCpu = Environment.ProcessorCount;
        for (var i = 0; i < 64; i++)
            if (i >= maxCpu)
                value &= ~(1L << i);
        return value;
    }
    
    public static async Task CopyToAsync(this Stream source, Stream destination, int bufferSize, IProgress<long>? progress = null, CancellationToken cancellationToken = default) {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (!source.CanRead)
            throw new ArgumentException("Has to be readable", nameof(source));
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));
        if (!destination.CanWrite)
            throw new ArgumentException("Has to be writable", nameof(destination));
        if (bufferSize < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        var buffer = new byte[bufferSize];
        long totalBytesRead = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0) {
            await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
            totalBytesRead += bytesRead;
            progress?.Report(totalBytesRead);
        }
    }
    
    public static string Colorize(this string text, string color)
    {
        if (Console.IsOutputRedirected) return text;
        return color + text + ConsoleColors.NORMAL;
    }
    
    public static string Bold(this string text)
    {
        if (Console.IsOutputRedirected) return text;
        return ConsoleColors.BOLD + text + ConsoleColors.NOBOLD;
    }
    
    public static string Underline(this string text)
    {
        if (Console.IsOutputRedirected) return text;
        return ConsoleColors.UNDERLINE + text + ConsoleColors.NOUNDERLINE;
    }
}