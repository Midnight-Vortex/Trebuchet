using System.Diagnostics;

namespace TrebuchetLib;

/// <summary>Bounded, cancellable background I/O for save/profile migrations.</summary>
public static class DirectoryCopy
{
    private static readonly SemaphoreSlim CopyGate = new(1, 1);
    public const int BytesPerSecond = 32 * 1024 * 1024;
    private const int BufferSize = 128 * 1024;

    // Enumeration and metadata access must also stay off the calling/UI thread.
    public static Task CopyAsync(string source, string destination, CancellationToken token, IProgress<double>? progress = null)
        => Task.Run(async () =>
        {
            await CopyGate.WaitAsync(token).ConfigureAwait(false);
            try { await CopyCore(source, destination, token, progress).ConfigureAwait(false); }
            finally { CopyGate.Release(); }
        }, token);

    private static async Task CopyCore(string source, string destination, CancellationToken token, IProgress<double>? progress)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (source.Equals(destination, comparison)
            || destination.StartsWith(source + Path.DirectorySeparatorChar, comparison)
            || source.StartsWith(destination + Path.DirectorySeparatorChar, comparison))
            throw new IOException("Source and destination directories must not overlap.");

        token.ThrowIfCancellationRequested();
        long total = 0;
        // Stream metadata; never retain one FileInfo per file in a potentially huge mod cache.
        foreach (var entry in Entries(source, token))
            if (entry is FileInfo file) total = checked(total + file.Length);

        Directory.CreateDirectory(destination);
        var buffer = new byte[BufferSize];
        long copied = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        foreach (var entry in Entries(source, token))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, entry.FullName));
            if ((File.Exists(target) || Directory.Exists(target))
                && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Cannot overwrite a linked destination: {target}");
            if (entry is DirectoryInfo)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".copying";
            try
            {
                await using (var input = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        copied += read;
                        var delay = TimeSpan.FromSeconds((double)copied / BytesPerSecond) - clock.Elapsed;
                        if (delay > TimeSpan.FromMilliseconds(10)) await Task.Delay(delay, token).ConfigureAwait(false);
                        if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(100))
                        {
                            progress?.Report(total == 0 ? 0 : Math.Min(0.999, (double)copied / total));
                            lastReport = clock.Elapsed;
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                File.SetLastWriteTimeUtc(temporary, entry.LastWriteTimeUtc);
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        token.ThrowIfCancellationRequested();
        progress?.Report(1);
    }

    private static IEnumerable<FileSystemInfo> Entries(string directory, CancellationToken token)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            // Preserve existing copy semantics: copy file contents, but do not traverse directory junctions.
            if (entry is DirectoryInfo && (entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            yield return entry;
            if (entry is DirectoryInfo child)
                foreach (var nested in Entries(child.FullName, token)) yield return nested;
        }
    }
}
