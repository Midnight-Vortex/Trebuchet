using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.Sequences;

public class SequenceActionExecuteProcess : ISequenceAction
{
    public string Filename { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool WaitForProcessToExit { get; set; }
    public bool CancelIfExitCodeIsError { get; set; }
    public bool CreateNoWindow { get; set; }
    public bool UseShellExecute { get; set; }
    public bool CancelOnFailure { get; set; }
    
    // A script that never ends kept the sequence (and the stop or restart behind it) waiting forever.
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    public async Task Execute(SequenceArgs args)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.Arguments = args.Expand(Arguments);
            process.StartInfo.FileName = Filename;
            process.StartInfo.CreateNoWindow = CreateNoWindow;
            process.StartInfo.UseShellExecute = UseShellExecute;
            process.Start();
            if (WaitForProcessToExit)
            {
                // Cancelling the sequence (stop, kill, pause) stops waiting for the process.
                await process.WaitForExitAsync(args.CancellationToken).WaitAsync(MaxWait, args.CancellationToken);
                if (process.ExitCode != 0 && CancelIfExitCodeIsError)
                    throw new OperationCanceledException(
                        $"Process exited with an error code {Filename} ({process.ExitCode})");
            }
        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if(CancelOnFailure)
                throw new OperationCanceledException($"Failed to start process {Filename}", ex);
            args.Logger.LogError(ex, $"Failed to start process {Filename}");
        }
    }
}
