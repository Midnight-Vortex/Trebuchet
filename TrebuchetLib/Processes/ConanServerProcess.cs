using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TrebuchetLib.Processes;

internal sealed class ConanServerProcess : IConanServerProcess
{
    public ConanServerProcess(Process process, LogReader logReader)
    {
        Process = process;
        _logReader = logReader;
        PId = Process.Id;
        State = ProcessState.RUNNING;
        _lastResponse = DateTime.UtcNow;
    }

    // Process is not thread safe: the refresh runs on the thread pool while the UI reads stats.
    private readonly object _processLock = new();
    private volatile bool _disposed;
    private DateTime _lastResponse;
    private DateTime _shutdownCallTime;
    private int _maxPlayers;
    private bool _online;
    private int _players;
    private ProcessState _state;
    private readonly LogReader _logReader;
    private bool _hasTriedRConShutdown;
    private bool _hasTriedHandleShutdown;
    private bool _requestRestart;

    public event EventHandler<ProcessState>? StateChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

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

    public bool KillZombies { get; set; }
    public int ZombieCheckSeconds { get; set; }
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

    public int MaxPlayers
    {
        get => _maxPlayers;
        private set => SetField(ref _maxPlayers, value);
    }

    public int Players
    {
        get => _players;
        private set => SetField(ref _players, value);
    }

    public bool Online
    {
        get => _online;
        private set => SetField(ref _online, value);
    }

    public bool RequestRestart
    {
        get => _requestRestart;
        private set => SetField(ref _requestRestart, value);
    }

    public Process Process { get; }
    public required ConanServerInfos Infos { get; init; }
    public required SourceQueryReader SourceQueryReader { get; init; }
    public IRcon? RCon { get; init; }

    public int Instance => Infos.Instance;
    public int Port => Infos.Port;
    public int QueryPort => Infos.QueryPort;
    public string RConPassword => Infos.RConPassword;
    public int RConPort => Infos.RConPort;
    public string Title => Infos.Title;
    public Exception? LastKillError { get; private set; }

    public void Dispose()
    {
        lock (_processLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { Process.Dispose(); } catch { /* best effort */ }
        try { SourceQueryReader.Dispose(); } catch { /* best effort */ }
        try { _logReader.Dispose(); } catch { /* best effort */ }
        // One RCon client per server run: without this every restart leaked an open socket.
        try { (RCon as IDisposable)?.Dispose(); } catch { /* best effort */ }
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        var (exited, responding) = await Task.Run(() =>
        {
            bool hasExited;
            lock (_processLock)
            {
                if (_disposed) return (true, false);
                Process.TryRefresh();
                hasExited = Process.HasExitedSafe(PId);
            }
            // Responding can block up to 5s (SendMessageTimeout): probe outside the lock on its own handle.
            return (hasExited, !hasExited && ProcessSafety.ProbeResponding(PId));
        });
        if (_disposed) return;

        if (responding)
            _lastResponse = DateTime.UtcNow;

        try
        {
            SourceQueryReader.Refresh();
            Online = SourceQueryReader.Online;
            MaxPlayers = SourceQueryReader.MaxPlayers;
            Players = SourceQueryReader.Players;
        }
        catch (ObjectDisposedException) { return; }

        switch (State)
        {
            case ProcessState.NEW:
                if(!exited)
                    State = ProcessState.RUNNING;
                break;
            case ProcessState.RUNNING:
                if (exited) State = ProcessState.CRASHED;
                else
                {
                    if (Online) State = ProcessState.ONLINE;
                    if (!responding) ZombieCheck();
                }
                break;
            case ProcessState.ONLINE:
                if (exited) State = ProcessState.CRASHED;
                else if (!responding) ZombieCheck();
                break;
            case ProcessState.STOPPING:
                if (exited) State = ProcessState.STOPPED;
                else SendShutdown();
                break;
            case ProcessState.FAILED:
                State = ProcessState.CRASHED;
                break;
        }
    }

    public void ZombieCheck()
    {
        if (_lastResponse + TimeSpan.FromSeconds(ZombieCheckSeconds) < DateTime.UtcNow)
        {
            State = ProcessState.CRASHED;
            if (KillZombies)
                KillProcess();
        }
    }

    public async Task KillAsync()
    {
        if (_disposed) return;
        if (State is ProcessState.STOPPED or ProcessState.CRASHED) return;
        State = ProcessState.STOPPING;
        // Make sure the regular shutdown path cannot fire again after a kill.
        _hasTriedRConShutdown = true;
        _hasTriedHandleShutdown = true;
        _shutdownCallTime = DateTime.UtcNow;
        await Task.Run(KillProcess);
    }

    public Task StopAsync()
    {
        if (State.IsStopping() || !State.IsRunning())
            return Task.CompletedTask;
        State = ProcessState.STOPPING;
        SendShutdown();
        return Task.CompletedTask;
    }

    public Task RestartAsync()
    {
        if (State.IsStopping() || !State.IsRunning())
            return Task.CompletedTask;
        State = ProcessState.STOPPING;
        RequestRestart = true;
        SendShutdown();
        return Task.CompletedTask;
    }

    public bool EnsureProcessSettings(ProcessPriorityClass priority, long affinity)
    {
        if (_disposed) return false;
        lock (_processLock)
        {
            if (_disposed || Process.HasExitedSafe(PId)) return false;
            var changed = false;
            try
            {
                if (Process.PriorityClass != priority)
                {
                    Process.PriorityClass = priority;
                    changed = true;
                }
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                {
                    var mask = (IntPtr)affinity;
                    if (affinity != 0 && Process.ProcessorAffinity != mask)
                    {
                        Process.ProcessorAffinity = mask;
                        changed = true;
                    }
                }
            }
            catch (Exception)
            {
                // Access denied or process exiting: nothing to enforce.
            }
            return changed;
        }
    }

    private void KillProcess()
    {
        lock (_processLock)
        {
            if (_disposed) return;
            if (!Process.TryKill(PId, out var error))
                LastKillError = error;
        }
    }

    private void SendShutdown()
    {
        if ((DateTime.UtcNow - _shutdownCallTime) < TimeSpan.FromMinutes(3)) return;
        _shutdownCallTime = DateTime.UtcNow;

        if (RCon is not null && !_hasTriedRConShutdown)
        {
            _hasTriedRConShutdown = true;
            _ = SendRConShutdown(RCon);
        }
        else if (!_hasTriedHandleShutdown)
        {
            _hasTriedHandleShutdown = true;
            lock (_processLock)
                if (!_disposed) Process.TryCloseMainWindow();
        }
        else
            KillProcess();
    }

    private static async Task SendRConShutdown(IRcon rcon)
    {
        try
        {
            await rcon.Send(@"shutdown", CancellationToken.None);
        }
        catch
        {
            // The kill fallback takes over after the shutdown delay.
        }
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
