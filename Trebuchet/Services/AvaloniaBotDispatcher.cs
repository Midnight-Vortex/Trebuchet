using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using TrebuchetLib.DiscordBot;

namespace Trebuchet.Services;

/// <summary>
/// Runs the work of the Discord bot on the UI thread, where the 2 second tick uses the tables of the launcher.
/// <list type="bullet">
/// <item>Called on the UI thread, the work runs at once: queueing it behind the current job and waiting for it there
/// would deadlock.</item>
/// <item>From any other thread it is queued; the caller continues on the thread pool afterwards, not on the UI thread.</item>
/// <item>After <see cref="Close"/> (Trebuchet exits) nothing runs any more: queued work is cancelled instead of reaching a
/// launcher that is being disposed, and the bot can stop without waiting for the UI thread.</item>
/// </list>
/// </summary>
public sealed class AvaloniaBotDispatcher : IBotDispatcher
{
    private readonly Func<bool> _isUiThread;
    private readonly Action<Func<Task>> _post;
    private readonly object _lock = new();
    private readonly HashSet<Action> _queued = [];
    private bool _closed;

    public AvaloniaBotDispatcher()
        : this(() => Dispatcher.UIThread.CheckAccess(), job => _ = Dispatcher.UIThread.InvokeAsync(job))
    {
    }

    /// <summary>Another "UI thread" (tests): how to tell it is the current thread, and how to queue a job on it.</summary>
    public AvaloniaBotDispatcher(Func<bool> isUiThread, Action<Func<Task>> post)
    {
        _isUiThread = isUiThread;
        _post = post;
    }

    public bool IsClosed
    {
        get { lock (_lock) return _closed; }
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (IsClosed) return Cancelled<T>();
        if (_isUiThread()) return RunInline(work);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action cancel = () => completion.TrySetCanceled();
        lock (_lock)
        {
            if (_closed) return Cancelled<T>();
            _queued.Add(cancel);
        }
        try
        {
            _post(() => RunQueuedAsync(work, completion, cancel));
        }
        catch (Exception ex)
        {
            Dequeue(cancel);
            completion.TrySetException(ex);
        }
        return completion.Task;
    }

    /// <summary>Trebuchet exits: no more work. Work that waits in the queue is cancelled, running work finishes.</summary>
    public void Close()
    {
        List<Action> queued;
        lock (_lock)
        {
            _closed = true;
            queued = _queued.ToList();
            _queued.Clear();
        }
        foreach (var cancel in queued)
            cancel();
    }

    /// <summary>Never throws: the job of the UI thread has nobody to report to.</summary>
    private async Task RunQueuedAsync<T>(Func<Task<T>> work, TaskCompletionSource<T> completion, Action cancel)
    {
        // Cancelled by Close while it waited in the queue: the work must not run any more.
        if (!Dequeue(cancel)) return;
        try
        {
            completion.TrySetResult(await work());
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private static Task<T> RunInline<T>(Func<Task<T>> work)
    {
        try
        {
            return work();
        }
        catch (OperationCanceledException ex)
        {
            var cancelled = new TaskCompletionSource<T>();
            cancelled.TrySetCanceled(ex.CancellationToken);
            return cancelled.Task;
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    /// <returns>False when it was not queued any more (cancelled by <see cref="Close"/>).</returns>
    private bool Dequeue(Action cancel)
    {
        lock (_lock) return _queued.Remove(cancel);
    }

    private static Task<T> Cancelled<T>() => Task.FromCanceled<T>(new CancellationToken(true));
}
