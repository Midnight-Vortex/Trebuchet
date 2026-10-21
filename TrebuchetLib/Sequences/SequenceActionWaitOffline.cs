using Microsoft.Extensions.Logging;

namespace TrebuchetLib.Sequences;

public class SequenceActionWaitOffline : ISequenceAction
{
    public bool CancelOnFailure { get; set; }
    public TimeSpan TimeOut { get; set; } = TimeSpan.FromMinutes(5);
    public async Task Execute(SequenceArgs args)
    {
        var start = DateTime.UtcNow;
        while (IsServerRunning(args) && (DateTime.UtcNow - start) < TimeOut)
            await Task.Delay(500, args.CancellationToken);
        if(CancelOnFailure && IsServerRunning(args))
            throw new OperationCanceledException("Server failed to get offline");
    }

    /// <summary>
    /// Offline means the process ended. A stopped server stays in the launcher's list while its sequence runs (it is
    /// cleaned up afterwards), so checking only the list waited the whole timeout on every stop.
    /// </summary>
    internal static bool IsServerRunning(SequenceArgs args)
    {
        return args.Launcher.GetServerProcesses()
            .Any(x => x.Infos.Instance == args.Instance && x.State.IsRunning());
    }
}
