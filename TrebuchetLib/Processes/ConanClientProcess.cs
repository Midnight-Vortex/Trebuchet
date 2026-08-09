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

    public Process Process { get; }
    public long MemoryUsage
    {
        get
        {
            if (Process.HasExited)
                return 0;
            return Process.WorkingSet64;
        }
    }

    public TimeSpan CpuTime
    {
        get
        {
            if(Process.HasExited)
                return TimeSpan.Zero;
            return Process.TotalProcessorTime;
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
        Process.Dispose();
    }

    public async Task RefreshAsync()
    {
        await Task.Run(() => Process.Refresh());
        switch (State)
        {
            case ProcessState.RUNNING:
                if (Process.HasExited)
                    State = ProcessState.CRASHED;
                break;
            case ProcessState.STOPPING:
                if (Process.HasExited)
                    State = ProcessState.STOPPED;
                break;
        }
    }

    public Task KillAsync()
    {
        State = ProcessState.STOPPING;
        Process.Kill();
        return Task.CompletedTask;
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