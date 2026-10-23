using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Boulder;

/// <summary>
/// Ctrl+C, Ctrl+Break, SIGINT, SIGTERM and SIGQUIT end a long running command cleanly: the first one cancels
/// <see cref="Token"/> and keeps the process alive until the command returned, a second one ends the process at once.
/// </summary>
internal sealed class ShutdownSignals : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<PosixSignalRegistration> _registrations = [];
    private readonly ILogger _logger;
    private readonly bool _cancelKeyPress;
    private int _requested;

    public ShutdownSignals(ILogger logger)
    {
        _logger = logger;
        try
        {
            foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT })
                _registrations.Add(PosixSignalRegistration.Create(signal, OnSignal));
        }
        catch (PlatformNotSupportedException)
        {
            // Only Ctrl+C then.
            DisposeRegistrations();
            Console.CancelKeyPress += OnCancelKeyPress;
            _cancelKeyPress = true;
        }
    }

    public CancellationToken Token => _stop.Token;

    private void OnSignal(PosixSignalContext context) => context.Cancel = Request(context.Signal.ToString());

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) => e.Cancel = Request(e.SpecialKey.ToString());

    /// <returns>True the first time: the command ends by itself. Later the signal takes its normal way and ends the process.</returns>
    private bool Request(string signal)
    {
        if (Interlocked.Exchange(ref _requested, 1) == 1)
        {
            _logger.LogWarning("{Signal} again: ending at once", signal);
            return false;
        }
        _logger.LogInformation("{Signal}: stopping (once more ends at once)", signal);
        try
        {
            // The callbacks run elsewhere, not in the signal handler.
            _ = _stop.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // The command just ended.
        }
        return true;
    }

    private void DisposeRegistrations()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
    }

    public void Dispose()
    {
        DisposeRegistrations();
        if (_cancelKeyPress) Console.CancelKeyPress -= OnCancelKeyPress;
        _stop.Dispose();
    }
}
