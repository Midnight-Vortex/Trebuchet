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
                try
                {
                    // Cancelling the sequence (stop, kill, pause) stops waiting for the process.
                    await process.WaitForExitAsync(args.CancellationToken).WaitAsync(MaxWait, args.CancellationToken);
                }
                catch (TimeoutException)
                {
                    // A hung script could still hold the save or backup files the next steps need.
                    try { process.Kill(entireProcessTree: true); } catch { /* exited meanwhile */ }
                    throw new OperationCanceledException(
                        $"Process {Filename} did not exit within {MaxWait.TotalMinutes} minutes and was ended");
                }
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
            // Our own failures (error exit code, timeout) keep their message, others failed to start the process.
            var message = ex is OperationCanceledException ? ex.Message : $"Failed to start process {Filename}";
            if(CancelOnFailure)
                throw new OperationCanceledException(message, ex);
            args.Logger.LogError(ex, message);
        }
    }
}
