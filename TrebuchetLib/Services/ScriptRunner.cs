using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.Services;

/// <summary>
/// Runs the user scripts of the DSL style automation (startup/shutdown/update scripts): .bat/.cmd through cmd,
/// .ps1 through PowerShell, anything else directly. Waits for the script and never throws.
/// </summary>
public static class ScriptRunner
{
    public static async Task<bool> Run(string path, string arguments, ILogger logger, string purpose, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!File.Exists(path))
        {
            logger.LogError("{Purpose} script not found: {Path}", purpose, path);
            return false;
        }

        var info = CreateStartInfo(path, arguments);
        logger.LogInformation("Running {Purpose} script: {File} {Arguments}", purpose, info.FileName, info.Arguments);
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                logger.LogError("{Purpose} script could not be started: {Path}", purpose, path);
                return false;
            }

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("{Purpose} script still running after timeout, continuing: {Path}", purpose, path);
                return false;
            }

            if (process.ExitCode != 0)
            {
                logger.LogWarning("{Purpose} script exited with code {Code}: {Path}", purpose, process.ExitCode, path);
                return false;
            }
            logger.LogInformation("{Purpose} script finished: {Path}", purpose, path);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Purpose} script failed: {Path}", purpose, path);
            return false;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string path, string arguments)
    {
        var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var info = extension switch
        {
            ".bat" or ".cmd" => new ProcessStartInfo("cmd.exe", $"/c \"\"{path}\" {arguments}\""),
            ".ps1" => new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\" {arguments}"),
            _ => new ProcessStartInfo(path, arguments)
        };
        info.WorkingDirectory = directory;
        info.UseShellExecute = false;
        info.CreateNoWindow = false;
        return info;
    }
}
