using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace TrebuchetLib;

/// <summary>Bounded, cancellable background I/O for save/profile migrations.</summary>
public static class DirectoryCopy
{
    private static readonly SemaphoreSlim CopyGate = new(1, 1);
    public const int BytesPerSecond = 300 * 1000 * 1000;
    public const int MaxConcurrentFiles = CopyConcurrencyController.Maximum;
    private const int BufferSize = 256 * 1024;

    // Enumeration and metadata access must also stay off the calling/UI thread.
    public static Task CopyAsync(string source, string destination, CancellationToken token, IProgress<double>? progress = null)
        => CopyWithLoadAsync(source, destination, token, progress, new CopySystemLoad().Read, TimeSpan.FromSeconds(1));

    internal static Task CopyWithLoadAsync(string source, string destination, CancellationToken token, IProgress<double>? progress,
        Func<CopyLoad> sampleLoad, TimeSpan sampleInterval)
        => Task.Run(async () =>
        {
            await CopyGate.WaitAsync(token).ConfigureAwait(false);
            try { await CopyCore(source, destination, token, progress, sampleLoad, sampleInterval).ConfigureAwait(false); }
            finally { CopyGate.Release(); }
        }, token);

    private static async Task CopyCore(string source, string destination, CancellationToken token, IProgress<double>? progress,
        Func<CopyLoad> sampleLoad, TimeSpan sampleInterval)
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
        var fileCount = 0;
        // Stream metadata; never retain one FileInfo per file in a potentially huge mod cache.
        foreach (var entry in Entries(source, token))
            if (entry is FileInfo file)
            {
                total = checked(total + file.Length);
                if (fileCount < MaxConcurrentFiles) fileCount++;
            }

        Directory.CreateDirectory(destination);
        var workers = Math.Max(1, fileCount);
        var pending = Channel.CreateBounded<(FileInfo File, string Target)>(new BoundedChannelOptions(workers * 4)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = stop.Token;
        Exception? failure = null;
        var budget = new TransferBudget();
        long copied = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var reportLock = new object();
        var concurrency = new CopyConcurrencyController();
        var ioLock = new object();
        double ioSeconds = 0;
        long ioSamples = 0;
        void RecordIo(long start)
        {
            lock (ioLock)
            {
                ioSeconds += Stopwatch.GetElapsedTime(start).TotalSeconds;
                ioSamples++;
            }
        }
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task MonitorLoad()
        {
            sampleLoad(); // Establish the CPU counter baseline before the first interval.
            using var timer = new PeriodicTimer(sampleInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(monitorStop.Token).ConfigureAwait(false))
                {
                    double? latency;
                    lock (ioLock)
                    {
                        latency = ioSamples == 0 ? null : 1000 * ioSeconds / ioSamples;
                        ioSeconds = 0;
                        ioSamples = 0;
                    }
                    concurrency.Update(sampleLoad(), latency);
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
        }

        async Task RunStage(Func<Task> stage)
        {
            try { await stage().ConfigureAwait(false); }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    Interlocked.CompareExchange(ref failure, ex, null);
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        async Task Produce()
        {
            try
            {
                foreach (var entry in Entries(source, ct))
                {
                    var target = Path.Combine(destination, Path.GetRelativePath(source, entry.FullName));
                    if ((File.Exists(target) || Directory.Exists(target))
                        && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"Cannot overwrite a linked destination: {target}");
                    if (entry is DirectoryInfo) Directory.CreateDirectory(target);
                    else await pending.Writer.WriteAsync(((FileInfo)entry, target), ct).ConfigureAwait(false);
                }
            }
            finally { pending.Writer.TryComplete(); }
        }

        async Task Consume(int worker)
        {
            // One reusable buffer per worker: at most 2 MiB of transfer buffers for the entire job.
            var buffer = new byte[BufferSize];
            while (await pending.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                // Lowering the limit lets in-flight files finish, then parks excess workers.
                while (worker >= concurrency.Limit)
                {
                    if (pending.Reader.Completion.IsCompleted) return;
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
                if (!pending.Reader.TryRead(out var item)) continue;
                var (file, target) = item;
                ct.ThrowIfCancellationRequested();
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".copying";
                try
                {
                    await using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                                     1, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                     1, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        while (true)
                        {
                            var ioStart = Stopwatch.GetTimestamp();
                            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                            RecordIo(ioStart);
                            if (read == 0) break;
                            await budget.Reserve(read, ct).ConfigureAwait(false);
                            ioStart = Stopwatch.GetTimestamp();
                            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            RecordIo(ioStart);
                            Interlocked.Add(ref copied, read);
                            lock (reportLock)
                            {
                                if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(100))
                                {
                                    progress?.Report(total == 0 ? 0 : Math.Min(0.999, (double)Interlocked.Read(ref copied) / total));
                                    lastReport = clock.Elapsed;
                                }
                            }
                        }
                    }
                    ct.ThrowIfCancellationRequested();
                    File.SetLastWriteTimeUtc(temporary, file.LastWriteTimeUtc);
                    File.Move(temporary, target, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        var stages = new List<Task> { RunStage(Produce) };
        var monitoring = RunStage(MonitorLoad);
        for (var i = 0; i < workers; i++)
        {
            var worker = i;
            stages.Add(RunStage(() => Consume(worker)));
        }
        try { await Task.WhenAll(stages).ConfigureAwait(false); }
        catch
        {
            // Await every worker's cleanup, but surface the I/O failure instead of a peer's cancellation.
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally
        {
            await monitorStop.CancelAsync().ConfigureAwait(false);
            await monitoring.ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        progress?.Report(1);
    }

    private sealed class TransferBudget
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _reservedUntil;

        public Task Reserve(int bytes, CancellationToken token)
        {
            double delay;
            lock (_gate)
            {
                var now = _clock.Elapsed.TotalSeconds;
                _reservedUntil = Math.Max(now, _reservedUntil) + (double)bytes / BytesPerSecond;
                // Shared across all workers. Allow a small burst without accumulating credit while idle.
                delay = _reservedUntil - now - (4.0 * 1024 * 1024 / BytesPerSecond);
            }
            return delay >= 0.010 ? Task.Delay(TimeSpan.FromSeconds(delay), token) : Task.CompletedTask;
        }
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
