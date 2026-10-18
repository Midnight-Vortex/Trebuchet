using Microsoft.Extensions.Logging;

namespace TrebuchetLib.Sequences;

public class SequenceActionRConCommand : ISequenceAction
{
    public string RConCommand { get; set; } = string.Empty;
    public bool CancelOnFailure { get; set; }
    
    public async Task Execute(SequenceArgs args)
    {
        var process = args.Launcher.GetServerProcesses().FirstOrDefault(x => x.Infos.Instance == args.Instance);
        if (process is null)
        {
            if(CancelOnFailure)
                throw new OperationCanceledException("Server is not Running");
            args.Logger.LogError("Server is not Running");
            return;
        }

        if (process.RCon is null)
        {
            if (CancelOnFailure)
                throw new OperationCanceledException("RCon is not available on this server");
            args.Logger.LogError("RCon is not available on this server");
            return;
        }

        try
        {
            var command = args.Expand(RConCommand);
            await process.RCon.Send(command, args.CancellationToken);
        }
        catch (Exception ex)
        {
            if (CancelOnFailure)
                throw new OperationCanceledException("Failed to send RCon command", ex);
            args.Logger.LogError(ex, "Failed to send RCon command");
        }
    }
}