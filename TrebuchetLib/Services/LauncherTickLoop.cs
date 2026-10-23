using Microsoft.Extensions.Logging;

namespace TrebuchetLib.Services;

/// <summary>
/// The launcher tick of a host without a window, timed like the window (TrebuchetApp): the next tick starts
/// <see cref="Interval"/> after the previous one ended. A failing tick is logged and the next one runs. The watchdog of
/// the window: a tick that does not end within <see cref="Timeout"/> is logged and no new tick starts until it ended, so
/// ticks never run twice at once and never pile up.
/// </summary>
public sealed class LauncherTickLoop(Func<Task> tick, ILogger logger)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultStopWait = TimeSpan.FromSeconds(10);

    private int _started;
    private int _failed;
    private int _skipped;

    public TimeSpan Interval { get; init; } = DefaultInterval;
    public TimeSpan Timeout { get; init; } = DefaultTimeout;
    /// <summary>How long the end of the loop waits for a tick that is still running. A hung one is not waited for.</summary>
    public TimeSpan StopWait { get; init; } = DefaultStopWait;

    public int Started => Volatile.Read(ref _started);
    public int Failed => Volatile.Read(ref _failed);
    /// <summary>Rounds without a tick because the previous one hung.</summary>
    public int Skipped => Volatile.Read(ref _skipped);

    /// <summary>Ticks until <paramref name="token"/> is cancelled. Never throws.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        Task? running = null;
        var hung = false;
        var failuresInARow = 0;
        while (!token.IsCancellationRequested)
        {
            if (running is { IsCompleted: false })
            {
                // Still hung: skipped, never a second tick next to it.
                Interlocked.Increment(ref _skipped);
            }
            else
            {
                if (hung)
                {
                    hung = false;
                    logger.LogWarning(running?.Exception?.GetBaseException(), "Launcher tick completed late ({Status}), ticking again", running?.Status);
                }
                running = StartTick();
                if (await EndsWithinAsync(running, Timeout, token).ConfigureAwait(false))
                {
                    failuresInARow = Report(running, failuresInARow);
                }
                else if (!token.IsCancellationRequested)
                {
                    hung = true;
                    logger.LogError("Launcher tick did not complete within {Timeout}, it is skipped until it does", Timeout);
                }
            }

            try
            {
                await Task.Delay(Interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // A tick that runs right now may end before the host goes on with its shutdown.
        if (running is { IsCompleted: false } && !hung)
            await EndsWithinAsync(running, StopWait, CancellationToken.None).ConfigureAwait(false);
    }

    private Task StartTick()
    {
        Interlocked.Increment(ref _started);
        Task task;
        try
        {
            task = tick();
        }
        catch (Exception ex)
        {
            // Thrown before its first await: a failed tick like any other.
            task = Task.FromException(ex);
        }
        // Read here, so the error of a tick that ends after the loop stopped waiting is not reported as unobserved.
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return task;
    }

    private int Report(Task task, int failuresInARow)
    {
        var error = task.IsFaulted ? task.Exception?.GetBaseException() : null;
        if (error is null or OperationCanceledException) return 0;
        Interlocked.Increment(ref _failed);
        failuresInARow++;
        logger.LogError(error, "Launcher tick failed ({Count} in a row)", failuresInARow);
        return failuresInARow;
    }

    /// <returns>True when the task ended within the limit (or <paramref name="token"/> was cancelled first: then false).</returns>
    public static async Task<bool> EndsWithinAsync(Task task, TimeSpan limit, CancellationToken token)
    {
        if (task.IsCompleted) return true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var first = await Task.WhenAny(task, Task.Delay(limit, cancel.Token)).ConfigureAwait(false);
        await cancel.CancelAsync().ConfigureAwait(false);
        return first == task;
    }
}
