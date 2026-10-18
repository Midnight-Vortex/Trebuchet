using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using tot_lib.Git;

namespace tot_lib;

public static class ProcessUtil
{
    public static async Task<GitCommand.ReadToEndResult> ReadToEnd(Process proc)
    {
        return await Task.Run(() =>
        {
            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return new GitCommand.ReadToEndResult()
                {
                    IsSuccess = false,
                    StdOut = string.Empty,
                    StdErr = e.Message,
                };
            }

            var rs = new GitCommand.ReadToEndResult()
            {
                StdOut = proc.StandardOutput.ReadToEnd(),
                StdErr = proc.StandardError.ReadToEnd(),
            };

            proc.WaitForExit();
            rs.IsSuccess = proc.ExitCode == 0;
            proc.Close();

            return rs;
        });
    }
    
    public static string GetStringVersion()
    {
        var fvi = GetFileVersion();
        if (fvi is null) return string.Empty;
#if DEBUG
        string tag = " [Dev]";
#else
            string tag = string.Empty;
#endif
        return $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}{tag}";
    }

    public static FileVersionInfo? GetFileVersion()
    {
        if (string.IsNullOrEmpty(Environment.ProcessPath))
            return null;
        return FileVersionInfo.GetVersionInfo(Environment.ProcessPath);
    }

    public static AppVersion GetAppVersion()
    {
        var fvi = GetFileVersion();
        if(fvi is null) return AppVersion.Default;
        return new AppVersion(fvi);
    }
}