using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// The launcher thread of a host without a window (<c>boulder bot</c>), in place of the UI thread. The tick and the bot
/// hand their launcher work in here: one piece at a time, in the order it was handed in, on one dedicated thread. That
/// thread is also the synchronization context of the work, so what the launcher starts and does not await (countdowns,
/// background starts, the end of a stop) continues there like it does on the UI thread: the plain dictionaries of the
/// <c>Launcher</c> are never used by two threads at once.
/// </summary>
/// <remarks>
/// Deadlock rule: work must never wait for other work of the same dispatcher, neither by awaiting
/// <see cref="InvokeAsync{T}"/> (itself or through a call that does) nor by blocking the thread (<c>Wait()</c>,
/// <c>Result</c>): it would wait for itself. Work may hand in more work without waiting for it (e.g. from a
/// <c>Task.Run</c>); that runs after the current work.
/// </remarks>
public sealed class SerialBotDispatcher : IBotDispatcher, IDisposable
{
    // One piece of work at a time. Its async waiters are served first come, first served.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Never disposed: late callers read its token.
    private readonly CancellationTokenSource _stop = new();
    private readonly LauncherThread _thread;

    public SerialBotDispatcher(ILogger<SerialBotDispatcher>? logger = null)
    {
        _thread = new LauncherThread(_stop.Token, logger);
    }

    /// <summary>The managed thread ID of the launcher thread.</summary>
    public int ThreadId => _thread.ThreadId;

    public bool IsStopped => _stop.IsCancellationRequested;

    /// <summary>
    /// Runs <paramref name="work"/> on the launcher thread once all work handed in before it has ended. Its exception
    /// reaches the caller as thrown.
    /// </summary>
    /// <exception cref="OperationCanceledException">The dispatcher was stopped before the work ran (it never runs then) or while it ran.</exception>
    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var token = _stop.Token;
        if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
        // In line now, in the order of the calls.
        var turn = _gate.WaitAsync(token);
        return RunAsync(turn, work, token);
    }

    private async Task<T> RunAsync<T>(Task turn, Func<Task<T>> work, CancellationToken token)
    {
        await turn.ConfigureAwait(false);
        try
        {
            // Stopped while it waited: queued work does not run.
            token.ThrowIfCancellationRequested();
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Stopped while it runs: the thread ends and the work can not finish, the caller is not left waiting.
            using var stopped = token.UnsafeRegister(static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(), result);
            _thread.Post(_ => Start(work, result, token), null);
            return await result.Task.ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // On the launcher thread.
    private static void Start<T>(Func<Task<T>> work, TaskCompletionSource<T> result, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            result.TrySetCanceled(token);
            return;
        }
        _ = CompleteAsync(work, result);
    }

    private static async Task CompleteAsync<T>(Func<Task<T>> work, TaskCompletionSource<T> result)
    {
        try
        {
            result.TrySetResult(await work());
        }
        catch (Exception ex)
        {
            result.TrySetException(ex);
        }
    }

    /// <summary>Stops: queued work never runs, callers waiting for work get a cancellation. The launcher thread ends.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
    }

    private sealed class LauncherThread : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly CancellationToken _stop;
        private readonly ILogger? _logger;
        private readonly Thread _thread;

        public LauncherThread(CancellationToken stop, ILogger? logger)
        {
            _stop = stop;
            _logger = logger;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Launcher thread" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        // Must never throw (a continuation that throws here ends the process): after the stop it is dropped.
        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                if (!_stop.IsCancellationRequested) _queue.TryAdd((d, state));
            }
            catch (Exception)
            {
                // Stopped.
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == ThreadId)
            {
                d(state);
                return;
            }
            // Not disposed: a stop can end the wait while the callback still runs.
            var done = new ManualResetEventSlim();
            ExceptionDispatchInfo? error = null;
            Post(_ =>
            {
                try { d(state); }
                catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
                finally { done.Set(); }
            }, null);
            done.Wait(_stop);
            error?.Throw();
        }

        public override SynchronizationContext CreateCopy() => this;

        private void Loop()
        {
            SetSynchronizationContext(this);
            try
            {
                foreach (var (callback, state) in _queue.GetConsumingEnumerable(_stop))
                {
                    if (Current != this) SetSynchronizationContext(this);
                    try
                    {
                        callback(state);
                    }
                    catch (Exception ex)
                    {
                        // E.g. an async void method of the launcher. Logged, the thread goes on (as on the UI thread).
                        _logger?.LogError(ex, "Unhandled error on the launcher thread");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        }
    }
}
