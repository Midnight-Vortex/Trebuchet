using Microsoft.Extensions.Logging;

namespace TrebuchetLib.Sequences;

public class SequenceActionWaitOnline : ISequenceAction
{
    public bool CancelOnFailure { get; set; }
    public TimeSpan TimeOut { get; set; } = TimeSpan.FromMinutes(10);
    public async Task Execute(SequenceArgs args)
    {
        var start = DateTime.UtcNow;
        var process = args.Launcher.GetServerProcesses().FirstOrDefault(x => x.Infos.Instance == args.Instance);
        if (process is null)
        {
            if (CancelOnFailure)
                throw new OperationCanceledException("Server is not launched");
            args.Logger.LogError("Server is not launched");
            return;
        }

        // 500 ms is plenty for a state refreshed every 2 s; stop early if the server died while starting.
        while (process.State != ProcessState.ONLINE && process.State.IsRunning() && (DateTime.UtcNow - start) < TimeOut)
            await Task.Delay(500, args.CancellationToken);
        
        if(CancelOnFailure && process.State != ProcessState.ONLINE)
            throw new OperationCanceledException("Server failed to get online");
    }
}