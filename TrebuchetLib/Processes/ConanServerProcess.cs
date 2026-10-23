using Microsoft.Extensions.Logging;
using TrebuchetLib.DiscordBot;
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
    private int _shutdownStep;
    private volatile bool _shutdownStepFailed;
    private bool _requestRestart;
    // Time a server gets after a stop request to save and exit before the next, harder way is tried.
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromMinutes(3);
    // Ctrl+C is answered at once or not at all: a server that ignores it (no console to receive it) must not hold up
    // the other ways for minutes.
    private static readonly TimeSpan CtrlCGrace = TimeSpan.FromSeconds(45);

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
    public ILogger? Logger { get; init; }

    public int Instance => Infos.Instance;

    /// <summary>How the log names the server: "0", or "0 (Name of the server)" when its save has a name of its own.</summary>
    private string Label
    {
        get
        {
            var title = BotInputRules.SanitizeForLog(Infos.Title);
            return title.Length == 0 || string.Equals(title, ServerProfile.ServerNameDefault, StringComparison.Ordinal)
                ? Instance.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{Instance} ({title})";
        }
    }
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
        {
            _lastResponse = DateTime.UtcNow;
            if (_hangReported)
                Logger?.LogInformation("Server {Server} responds again", Label);
            _hangReported = false;
        }

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
        if (_lastResponse + TimeSpan.FromSeconds(ZombieCheckSeconds) >= DateTime.UtcNow) return;
        if (!KillZombies)
        {
            // Without the kill a hung server keeps running: reporting it as crashed made Trebuchet start a second
            // server on the same save. It is only reported.
            if (!_hangReported)
                Logger?.LogWarning("Server {Server} does not respond for {Seconds} s, it is not killed (kill zombies is off)",
                    Label, ZombieCheckSeconds);
            _hangReported = true;
            return;
        }
        State = ProcessState.CRASHED;
        KillProcess();
    }

    private bool _hangReported;

    public async Task KillAsync()
    {
        if (_disposed) return;
        if (State is ProcessState.STOPPED or ProcessState.CRASHED) return;
        State = ProcessState.STOPPING;
        // Make sure the regular shutdown path cannot fire again after a kill (only the kill is repeated).
        _shutdownStep = ShutdownStepKill - 1;
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

    private const int ShutdownStepKill = 4;

    /// <summary>
    /// Stops the server, from the gentlest way to the hardest: Ctrl+C on its console (it saves and exits), the RCon
    /// "shutdown" command, closing its window, then kill. Each way that was delivered gets <see cref="ShutdownGrace"/>
    /// (Ctrl+C <see cref="CtrlCGrace"/>); a way that could not be delivered hands over at once (it used to wait 3 minutes each time, so a server without
    /// RCon or a console window only stopped after 3 to 6 minutes, and then by a kill).
    /// </summary>
    private void SendShutdown()
    {
        if (_shutdownStep > 0 && !_shutdownStepFailed && DateTime.UtcNow - _shutdownCallTime < (_shutdownStep == 1 ? CtrlCGrace : ShutdownGrace)) return;
        _shutdownCallTime = DateTime.UtcNow;
        _shutdownStepFailed = false;

        while (true)
        {
            switch (++_shutdownStep)
            {
                case 1:
                    if (!ConsoleControl.IsAvailable) continue;
                    // Logged before it is sent: if anything about this step ever goes wrong, the log shows where.
                    Logger?.LogInformation("Stopping server {Server}: sending Ctrl+C to its console", Label);
                    _ = SendCtrlCAsync();
                    return;
                case 2:
                    if (RCon is null) continue;
                    Logger?.LogInformation("Stopping server {Server}: RCon shutdown sent", Label);
                    _ = SendRConShutdown(RCon);
                    return;
                case 3:
                    bool closed;
                    lock (_processLock)
                        closed = !_disposed && Process.TryCloseMainWindow();
                    // Console servers have no main window: nothing was sent.
                    if (!closed) continue;
                    Logger?.LogInformation("Stopping server {Server}: window close sent", Label);
                    return;
                default:
                    _shutdownStep = ShutdownStepKill;
                    Logger?.LogWarning("Stopping server {Server}: it did not exit, killing it", Label);
                    KillProcess();
                    return;
            }
        }
    }

    // By a helper process, in the background: the launcher itself never touches the console of a server.
    private async Task SendCtrlCAsync()
    {
        var sent = false;
        try
        {
            sent = await ConsoleControl.TrySendCtrlCAsync(PId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Ctrl+C for server {Server} failed", Label);
        }
        if (sent)
        {
            Logger?.LogInformation("Stopping server {Server}: Ctrl+C delivered", Label);
            return;
        }
        // Not delivered: the next refresh goes on with the next way instead of waiting.
        Logger?.LogInformation("Stopping server {Server}: Ctrl+C could not be delivered, trying the next way", Label);
        _shutdownStepFailed = true;
    }

    private async Task SendRConShutdown(IRcon rcon)
    {
        try
        {
            var response = await rcon.Send(@"shutdown", CancellationToken.None);
            if (response.Exception is null) return;
            Logger?.LogWarning(response.Exception, "RCon shutdown of server {Server} failed", Label);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "RCon shutdown of server {Server} failed", Label);
        }
        // Not delivered: the next refresh goes on with the next way instead of waiting.
        _shutdownStepFailed = true;
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
