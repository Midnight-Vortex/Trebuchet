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
    
    public async Task Execute(SequenceArgs args)
    {
        try
        {
            var process = new Process()
            {
                StartInfo = new()
            };
            process.StartInfo.Arguments = args.Expand(Arguments);
            process.StartInfo.FileName = Filename;
            process.StartInfo.CreateNoWindow = CreateNoWindow;
            process.StartInfo.UseShellExecute = UseShellExecute;
            process.Start();
            if (WaitForProcessToExit)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0 && CancelIfExitCodeIsError)
                    throw new OperationCanceledException(
                        $"Process exited with an error code {Filename} ({process.ExitCode})");
            }
        }
        catch (Exception ex)
        {
            if(CancelOnFailure)
                throw new OperationCanceledException($"Failed to start process {Filename}", ex);
            args.Logger.LogError(ex, $"Failed to start process {Filename}");
        }
    }
}