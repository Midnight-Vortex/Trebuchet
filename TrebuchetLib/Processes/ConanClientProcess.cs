using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TrebuchetLib.Processes;

internal class EmptyConanProcess : IConanProcess
{
    public void Dispose()
    {
    }

    public Process Process { get; } = new ();
    public int PId { get; } = 0;
    public long MemoryUsage { get; } = 0;
    public TimeSpan CpuTime { get; } = TimeSpan.Zero;
    public DateTime StartUtc { get; } = DateTime.MinValue;
    public ProcessState State { get; } = ProcessState.STOPPED;
    
    public event EventHandler<ProcessState>? StateChanged; 
    public event PropertyChangedEventHandler? PropertyChanged;

    public Task RefreshAsync()
    {
        return Task.CompletedTask;
    }

    public Task KillAsync()
    {
        return Task.CompletedTask;
    }
    
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    
    private void OnStateChanged(ProcessState args)
    {
        StateChanged?.Invoke(this, args);
    }
}

public static class ConanProcess
{
    public static IConanProcess Empty => new EmptyConanProcess();
}

internal sealed class ConanClientProcess : IConanProcess
{
    public ConanClientProcess(Process process)
    {
        Process = process;
        PId = Process.Id;
        State = ProcessState.RUNNING;
    }

    
    private ProcessState _state;

    public event EventHandler<ProcessState>? StateChanged; 
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly object _processLock = new();
    private volatile bool _disposed;

    public Process Process { get; }
    public long MemoryUsage
    {
        get
        {
            if (_disposed) return 0;
            lock (_processLock)
                return _disposed ? 0 : Process.WorkingSetSafe(PId);
        }
    }

    public TimeSpan CpuTime
    {
        get
        {
            if (_disposed) return TimeSpan.Zero;
            lock (_processLock)
                return _disposed ? TimeSpan.Zero : Process.TotalProcessorTimeSafe(PId);
        }
    }
    
    public int PId { get; }
    public DateTime StartUtc { get; init; }

    public ProcessState State
    {
        get => _state;
        private set
        {
            if (SetField(ref _state, value))
                OnStateChanged(_state);
        }
    }

    public void Dispose()
    {
        lock (_processLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { Process.Dispose(); } catch { /* best effort */ }
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        var exited = await Task.Run(() =>
        {
            lock (_processLock)
            {
                if (_disposed) return true;
                Process.TryRefresh();
                return Process.HasExitedSafe(PId);
            }
        });
        if (_disposed) return;
        switch (State)
        {
            case ProcessState.RUNNING:
                if (exited)
                    State = ProcessState.CRASHED;
                break;
            case ProcessState.STOPPING:
                if (exited)
                    State = ProcessState.STOPPED;
                break;
        }
    }

    public async Task KillAsync()
    {
        if (_disposed || State is ProcessState.STOPPED or ProcessState.CRASHED) return;
        State = ProcessState.STOPPING;
        await Task.Run(() =>
        {
            lock (_processLock)
                if (!_disposed) Process.TryKill(PId, out _);
        });
    }
    
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    
    private void OnStateChanged(ProcessState args)
    {
        StateChanged?.Invoke(this, args);
    }
}