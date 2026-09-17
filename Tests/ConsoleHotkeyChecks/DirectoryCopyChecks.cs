using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using TrebuchetLib;

static class DirectoryCopyChecks
{
    public static async Task<int> Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrebuchetCopyChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        try
        {
            var source = Path.Combine(root, "source");
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
            var large = Path.Combine(source, "large.pak");
            var block = new byte[128 * 1024];
            Random.Shared.NextBytes(block);
            await using (var stream = File.Create(large))
                for (var i = 0; i < 256; i++) await stream.WriteAsync(block);
            File.SetLastWriteTimeUtc(large, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            for (var i = 0; i < 2000; i++)
                await File.WriteAllTextAsync(Path.Combine(source, "nested", $"config-{i}.ini"), "Grüße 😀\n");
            var reports = new ConcurrentQueue<double>();
            var started = Stopwatch.StartNew();
            var allocatedBefore = GC.GetTotalAllocatedBytes();
            var copy = Tools.DeepCopyAsync(source, target, CancellationToken.None, new CallbackProgress(reports.Enqueue));
            var returnedAfter = started.Elapsed;
            await copy;
            var allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
            Check(returnedAfter < TimeSpan.FromMilliseconds(500), "Copy returns promptly without enumerating on the caller thread");
            Check(started.Elapsed >= TimeSpan.FromMilliseconds(900), "Large-file writes respect the 32 MiB/s budget");
            Check(await Hash(large) == await Hash(Path.Combine(target, "large.pak")), "Large file copied without corruption");
            Check(File.GetLastWriteTimeUtc(Path.Combine(target, "large.pak")).Year == 2020, "Copy preserves last-write timestamps");
            Check(Directory.Exists(Path.Combine(target, "nested", "empty")), "Empty directories survive migration");
            Check(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 2001, "All small files copied");
            Check(await File.ReadAllTextAsync(Path.Combine(target, "nested", "config-1999.ini")) == "Grüße 😀\n", "Unicode file contents preserved");
            var values = reports.ToArray();
            Check(values.Length > 1 && values[^1] == 1 && values.SequenceEqual(values.Order()), "Progress updates within large files and completes monotonically");
            Check(values.Length <= started.Elapsed.TotalSeconds * 11 + 2, "Progress reporting is throttled instead of flooding the UI");
            Console.WriteLine($"Copy fixture: 32 MiB + 2000 small files; {started.Elapsed.TotalSeconds:F2}s; caller returned in {returnedAfter.TotalMilliseconds:F1}ms; managed allocations {allocated / 1048576.0:F1} MiB (not peak RAM).");

            var cancelTarget = Path.Combine(root, "cancel-target");
            Directory.CreateDirectory(cancelTarget);
            await File.WriteAllTextAsync(Path.Combine(cancelTarget, "large.pak"), "keep existing destination");
            using (var cancel = new CancellationTokenSource())
            {
                var cancelled = false;
                try { await Tools.DeepCopyAsync(source, cancelTarget, cancel.Token, new CallbackProgress(p => { if (p > 0) cancel.Cancel(); })); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Cancellation interrupts an individual large file");
            }
            Check(await File.ReadAllTextAsync(Path.Combine(cancelTarget, "large.pak")) == "keep existing destination", "Cancellation preserves pre-existing destination file");
            Check(!Directory.GetFiles(cancelTarget, "*.copying", SearchOption.AllDirectories).Any(), "Cancellation removes temporary partial files");
            Check(new FileInfo(large).Length == 32 * 1024 * 1024, "Cancellation never alters the original save");

            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                var path = Path.Combine(root, "pre-cancelled");
                try { await Tools.DeepCopyAsync(source, path, cancel.Token); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { Check(!Directory.Exists(path), "Pre-cancelled operations create no destination"); }
            }
            foreach (var badTarget in new[] { source, Path.Combine(source, "child"), root })
            {
                try { await Tools.DeepCopyAsync(source, badTarget, CancellationToken.None); throw new Exception("Expected overlap rejection"); }
                catch (IOException) { Check(true, "Overlapping source/destination rejected before writing"); }
            }

            using (var blocked = new ManualResetEventSlim())
            using (var cancel = new CancellationTokenSource())
            {
                var firstProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var first = Tools.DeepCopyAsync(source, Path.Combine(root, "serialized-first"), cancel.Token,
                    new CallbackProgress(p => { if (p > 0 && p < 1) { firstProgress.TrySetResult(); blocked.Wait(TimeSpan.FromSeconds(5)); } }));
                try
                {
                    await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    using var queuedCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                    var secondTarget = Path.Combine(root, "serialized-second");
                    try { await Tools.DeepCopyAsync(source, secondTarget, queuedCancel.Token); throw new Exception("Expected queued cancellation"); }
                    catch (OperationCanceledException) { Check(!Directory.Exists(secondTarget), "Concurrent copies queue without extra disk scans or writes"); }
                }
                finally { cancel.Cancel(); blocked.Set(); }
                try { await first; }
                catch (OperationCanceledException) { Check(true, "Active serialized copy cancels and releases the gate"); }
            }

            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            var completion = 0.0;
            await Tools.DeepCopyAsync(empty, Path.Combine(root, "empty-copy"), CancellationToken.None, new CallbackProgress(p => completion = p));
            Check(completion == 1, "Empty copy completes and gate remains usable after cancellations");

            if (OperatingSystem.IsWindows())
            {
                var loop = Path.Combine(source, "nested", "loop");
                tot_lib.OsSpecific.JunctionPoint.Create(loop, source, false);
                try
                {
                    var expectedSize = 32L * 1024 * 1024 + 2000 * System.Text.Encoding.UTF8.GetByteCount("Grüße 😀\n");
                    Check(Tools.DirectorySize(source) == expectedSize, "Profile sizing skips recursive junctions without recounting files");
                }
                finally { tot_lib.OsSpecific.JunctionPoint.Delete(loop); }

                var saved = Path.Combine(root, "Saved");
                Directory.CreateDirectory(saved);
                await File.WriteAllTextAsync(Path.Combine(saved, "game.db"), "original save");
                var os = new tot_lib.OsSpecific.OsPlatformWindows();
                try
                {
                    SavedDirectorySwitch.ReplaceWithLink(saved, Path.Combine(root, "missing-link-target"), os);
                    throw new Exception("Expected link creation failure");
                }
                catch (IOException) { Check(await File.ReadAllTextAsync(Path.Combine(saved, "game.db")) == "original save", "Failed junction creation restores the complete original Saved directory"); }
                var managed = Path.Combine(root, "managed");
                Directory.CreateDirectory(managed);
                await File.WriteAllTextAsync(Path.Combine(managed, "game.db"), "managed save");
                Check(SavedDirectorySwitch.ReplaceWithLink(saved, managed, os) is null, "Successful switch cleans up its original backup");
                try
                {
                    Check(os.IsSymbolicLink(saved) && await File.ReadAllTextAsync(Path.Combine(saved, "game.db")) == "managed save", "Successful management switch points at the managed data");
                }
                finally { os.RemoveSymbolicLink(saved); }
                Check(!Directory.EnumerateDirectories(root, "Saved.trebuchet-backup-*").Any(), "Successful switch leaves no obsolete backup");
            }
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("TrebuchetCopyChecks-", StringComparison.Ordinal))
                throw new IOException("Unexpected test cleanup directory");
            Directory.Delete(resolved, true);
        }
        return count;
    }

    private static async Task<string> Hash(string file)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
    private sealed class CallbackProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
