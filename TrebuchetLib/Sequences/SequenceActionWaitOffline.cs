using Microsoft.Extensions.Logging;

namespace TrebuchetLib.Sequences;

public class SequenceActionWaitOffline : ISequenceAction
{
    public bool CancelOnFailure { get; set; }
    public TimeSpan TimeOut { get; set; } = TimeSpan.FromMinutes(5);
    public async Task Execute(SequenceArgs args)
    {
        var start = DateTime.UtcNow;
        while (args.Launcher.GetServerProcesses().Any(x => x.Infos.Instance == args.Instance) 
               && (DateTime.UtcNow - start) < TimeOut)
            await Task.Delay(500, args.CancellationToken);
        if(CancelOnFailure && args.Launcher.GetServerProcesses().Any(x => x.Infos.Instance == args.Instance) )
            throw new OperationCanceledException("Server failed to get offline");
    }
}