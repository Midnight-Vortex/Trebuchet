using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib
{
    public abstract class JsonFile<T> where T : JsonFile<T>
    {
        //todo: Replace json serialization with generated sources
        public static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            IgnoreReadOnlyProperties = true,
            TypeInfoResolver = new PrivateConstructorContractResolver()
        };

        public event EventHandler<T>? FileSaved;

        [JsonIgnore]
        public string FilePath { get; protected set; } = string.Empty;

        /// <summary>
        /// Copy the file to the specified path
        /// </summary>
        /// <param name="path"></param>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="Exception"></exception>
        internal void CopyFileTo(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is invalid");

            string? folder = Path.GetDirectoryName(path);
            if (folder == null)
                throw new DirectoryNotFoundException($"Invalid directory for {path}");

            if (File.Exists(path))
                throw new Exception($"{path} already exists");

            string? dataFolder = Path.GetDirectoryName(FilePath);
            if (dataFolder == null)
                throw new DirectoryNotFoundException($"Invalid directory for {FilePath}");

            File.Copy(FilePath, path);
        }

        /// <summary>
        /// Copy the file and its parent folder to the specified path
        /// </summary>
        /// <param name="path"></param>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="Exception"></exception>
        internal async Task CopyFolderTo(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is invalid");

            string? folder = Path.GetDirectoryName(path);
            if (folder == null)
                throw new DirectoryNotFoundException($"Invalid directory for {path}");

            if (File.Exists(path) || (Directory.Exists(folder)))
                throw new Exception($"{path} already exists");

            string? dataFolder = Path.GetDirectoryName(FilePath);
            if (dataFolder == null)
                throw new DirectoryNotFoundException($"Invalid directory for {FilePath}");

            Tools.CreateDir(folder);
            await Tools.DeepCopyAsync(dataFolder, folder, CancellationToken.None);
        }

        /// <summary>
        /// Delete the file
        /// </summary>
        /// <exception cref="FileNotFoundException"></exception>
        internal void DeleteFile()
        {
            if (!File.Exists(FilePath))
                throw new FileNotFoundException($"{FilePath} not found");
            File.Delete(FilePath);
            JsonFileStorage.DeleteSideFiles(FilePath);
        }

        /// <summary>
        /// Delete file and its parent folder
        /// </summary>
        /// <exception cref="DirectoryNotFoundException"></exception>
        internal void DeleteFolder()
        {
            string? folder = Path.GetDirectoryName(FilePath);
            if (folder == null || !Directory.Exists(folder))
                throw new DirectoryNotFoundException($"Invalid directory for {FilePath}");

            Directory.Delete(folder, true);
        }

        /// <summary>
        /// Move the file and its parent folder to the specified path
        /// </summary>
        /// <param name="path"></param>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="Exception"></exception>
        internal void MoveFolderTo(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is invalid");

            string? targetFolder = Path.GetDirectoryName(path);
            if (targetFolder == null)
                throw new DirectoryNotFoundException($"Invalid directory for {path}");

            if (File.Exists(path) || Directory.Exists(targetFolder))
                throw new Exception($"{path} already exists");

            string? folder = Path.GetDirectoryName(FilePath);
            if (folder == null)
                throw new DirectoryNotFoundException($"Invalid directory for {FilePath}");

            Directory.Move(folder, targetFolder);
            FilePath = path;
        }
        
        /// <summary>
        /// Move the file and its parent folder to the specified path
        /// </summary>
        /// <param name="path"></param>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="Exception"></exception>
        internal void MoveFileTo(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is invalid");

            if (File.Exists(path))
                throw new Exception($"{path} already exists");

            File.Move(FilePath, path);
            JsonFileStorage.DeleteSideFiles(FilePath);
            FilePath = path;
        }

        /// <summary>
        /// Save the file to disk as Json file
        /// </summary>
        /// <exception cref="Exception"></exception>
        public void SaveFile()
        {
            SaveFile(keepBackup: true);
        }

        /// <param name="keepBackup">False replaces the file without making it the new .bak (it is damaged).</param>
        private void SaveFile(bool keepBackup)
        {
            string json = JsonSerializer.Serialize(this, typeof(T), _jsonOptions);
            string? folder = Path.GetDirectoryName(FilePath);
            if (folder == null)
                throw new Exception($"{FilePath} is an invalid path");
            Tools.CreateDir(folder);
            JsonFileStorage.WriteAllTextAtomic(FilePath, json, keepBackup);
            OnFileSaved();
        }

        public static T ImportFile(string json, string path, JsonSerializerOptions? options = null)
        {
            T? file = JsonSerializer.Deserialize<T>(json, options != null ? options : _jsonOptions);
            if (file == null)
                throw new Exception($"{path} could not be loaded");

            string content = JsonSerializer.Serialize(file, typeof(T), _jsonOptions);
            string? folder = Path.GetDirectoryName(path);
            if (folder == null)
                throw new Exception($"{path} is an invalid path");
            Tools.CreateDir(folder);
            JsonFileStorage.WriteAllTextAtomic(path, content);
            file.FilePath = path;
            return file;
        }

        /// <summary>
        /// Create a new file to be saved at the specified path
        /// </summary>
        /// <param name="path"></param>
        /// <param name="constructor"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        internal static T CreateFile(string path, params object[] constructor)
        {
            T? file = (T?)Activator.CreateInstance(typeof(T), constructor);
            if (file == null)
                throw new Exception($"Failed to create data of type {typeof(T)}");
            file.FilePath = path;
            return file;
        }

        /// <summary>
        /// Load the file from json at the specified path
        /// </summary>
        /// <param name="path"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        protected static T LoadFile(string path, JsonSerializerOptions? options = null)
        {
            if (!File.Exists(path))
                return CreateFile(path);
            // Read errors (locked file, no access) are not damage: they still throw and the file is left alone.
            string json = File.ReadAllText(path);
            if (TryDeserialize(json, path, options, out var file, out var error))
            {
                file.FilePath = path;
                return file;
            }
            // Well formed json that does not fit (written by a newer version, e.g. a new sequence action) is no
            // damage: resetting it to defaults lost the save and its server settings. It stays untouched.
            if (IsWellFormedJson(json))
                throw new Exception($"{path} could not be loaded", error);

            // Empty or invalid json (e.g. cut short by a crash, a kill or a full disk while an older version saved
            // it). Throwing here stopped Trebuchet before any window opened, on every autostart. Keep the broken
            // file for inspection and continue with the last good version (.bak) or, without one, with defaults.
            var aside = JsonFileStorage.MoveAside(path);
            var backupPath = path + JsonFileStorage.BackupSuffix;
            string? backup = null;
            try
            {
                if (File.Exists(backupPath)) backup = File.ReadAllText(backupPath);
            }
            catch (Exception readError)
            {
                JsonFileStorage.LogWarning(readError, $"Could not read the backup {backupPath}");
            }

            if (backup is not null && TryDeserialize(backup, backupPath, options, out var restored, out _))
            {
                file = restored;
                file.FilePath = path;
                JsonFileStorage.LogWarning(error, $"{path} was damaged and is restored from its backup (damaged file kept as {aside ?? "-"})");
            }
            else
            {
                file = CreateFile(path);
                JsonFileStorage.LogWarning(error, $"{path} was damaged and is reset to defaults (damaged file kept as {aside ?? "-"})");
            }

            // Write the repaired file right away: a profile file that was moved aside would otherwise be missing
            // from the profile lists until its next save. When the damaged file could not be moved aside it must not
            // become the new .bak: that destroyed the good backup it was just restored from.
            try
            {
                file.SaveFile(keepBackup: aside is not null);
            }
            catch (Exception saveError)
            {
                JsonFileStorage.LogWarning(saveError, $"Could not write the repaired {path}");
            }
            return file;
        }

        protected static bool IsWellFormedJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                return document.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryDeserialize(string json, string path, JsonSerializerOptions? options,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? file, out Exception? error)
        {
            try
            {
                file = JsonSerializer.Deserialize<T>(json, options ?? _jsonOptions);
                error = file is null ? new JsonException($"{path} contains no data") : null;
            }
            catch (JsonException ex)
            {
                file = null;
                error = ex;
            }
            return file is not null;
        }

        protected virtual void OnFileSaved()
        {
            FileSaved?.Invoke(this, (T)this);
        }
    }

    /// <summary>
    /// Crash safe writes of the json settings and profiles, and reports about damaged files found on load.
    /// </summary>
    public static class JsonFileStorage
    {
        /// <summary>A save is written to this file first, then swapped in.</summary>
        public const string TempSuffix = ".tmp";
        /// <summary>The previous good version of a file, used when the file itself is damaged.</summary>
        public const string BackupSuffix = ".bak";
        /// <summary>A damaged file is renamed to this suffix plus a time stamp instead of being overwritten.</summary>
        public const string CorruptSuffix = ".corrupt-";

        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private static readonly Lock LogLock = new();
        private static readonly List<(Exception? error, string message)> PendingLogs = [];
        private static ILogger? _logger;

        /// <summary>
        /// Write the text to a temporary file next to <paramref name="path"/>, flush it to disk, then replace the
        /// file with it. <see cref="File.WriteAllText(string,string)"/> truncates first, so a crash, kill or full
        /// disk during the write left an empty or half written file. The replaced version is kept as .bak.
        /// </summary>
        public static void WriteAllTextAtomic(string path, string content, bool keepBackup = true)
        {
            var temp = path + TempSuffix;
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Utf8NoBom.GetBytes(content));
                    // On disk before the rename: after a power loss the file must not be renamed but empty.
                    stream.Flush(flushToDisk: true);
                }

                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        SwapIn(temp, path, keepBackup);
                        return;
                    }
                    catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
                    {
                        // A virus scanner or backup tool can hold one of the files for a moment.
                        Thread.Sleep(50);
                    }
                }
            }
            catch
            {
                try { File.Delete(temp); } catch { /* best effort, the next save overwrites it */ }
                throw;
            }
        }

        private static void SwapIn(string temp, string path, bool keepBackup)
        {
            if (keepBackup && File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, path + BackupSuffix, ignoreMetadataErrors: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // E.g. the backup is locked by another program: a plain atomic rename still works.
                }
            }
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>Rename a damaged file aside so it is not overwritten. Returns the new path, or null.</summary>
        internal static string? MoveAside(string path)
        {
            try
            {
                var target = $"{path}{CorruptSuffix}{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(path, target, overwrite: true);
                return target;
            }
            catch (Exception ex)
            {
                LogWarning(ex, $"Could not rename the damaged file {path}");
                return null;
            }
        }

        /// <summary>Backup and temporary files of a deleted or renamed file must not be used for a new one.</summary>
        internal static void DeleteSideFiles(string path)
        {
            foreach (var side in new[] { path + BackupSuffix, path + TempSuffix })
            {
                try { File.Delete(side); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Settings are loaded before the application logger exists: reports are kept until it is set.
        /// </summary>
        public static void SetLogger(ILogger logger)
        {
            lock (LogLock)
            {
                _logger = logger;
                foreach (var (error, message) in PendingLogs)
                    logger.LogWarning(error, "{Message}", message);
                PendingLogs.Clear();
            }
        }

        internal static void LogWarning(Exception? error, string message)
        {
            lock (LogLock)
            {
                if (_logger is not null)
                    _logger.LogWarning(error, "{Message}", message);
                else if (PendingLogs.Count < 100)
                    PendingLogs.Add((error, message));
            }
        }
    }
}