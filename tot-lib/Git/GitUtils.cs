using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace tot_lib.Git;

public static partial class GitUtils
{
    public static Version GitVersion
    {
        get;
        private set;
    } = new Version(0, 0, 0);

    public static string GitBinary
    {
        get;
        set;
    } = "git";
    
    [GeneratedRegex(@"^git version[\s\w]*(\d+)\.(\d+)[\.\-](\d+).*$")]
    private static partial Regex REG_GIT_VERSION();

    private static string _gitExecutable = string.Empty;
    
    
    /// <summary>
    /// Removes redundent leading namespaces (regarding the kind of
    /// reference being wrapped) from the canonical name.
    /// </summary>
    /// <returns>The friendly shortened name</returns>
    public static string ShortenBranchName(this string branchName)
    {
        if (branchName.LooksLikeLocalBranch())
        {
            return branchName.Substring(LocalBranchPrefix.Length);
        }

        if (branchName.LooksLikeRemoteTrackingBranch())
        {
            return branchName.Substring(RemoteTrackingBranchPrefix.Length);
        }

        throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
            "'{0}' does not look like a valid branch name.",
            branchName));
    }
    
    public static bool LooksLikeRemoteTrackingBranch(this string canonicalName)
    {
        return canonicalName.IsPrefixedBy(RemoteTrackingBranchPrefix);
    }

    public static bool LooksLikeTag(this string canonicalName)
    {
        return canonicalName.IsPrefixedBy(TagPrefix);
    }

    public static bool LooksLikeNote(this string canonicalName)
    {
        return canonicalName.IsPrefixedBy(NotePrefix);
    }
    
    public static bool LooksLikeLocalBranch(this string canonicalName)
    {
        return canonicalName.IsPrefixedBy(LocalBranchPrefix);
    }
    
    public static bool IsPrefixedBy(this string input, string prefix)
    {
        return input.StartsWith(prefix, StringComparison.Ordinal);
    }
        
    internal static string LocalBranchPrefix
    {
        get { return "refs/heads/"; }
    }

    internal static string RemoteTrackingBranchPrefix
    {
        get { return "refs/remotes/"; }
    }

    internal static string TagPrefix
    {
        get { return "refs/tags/"; }
    }

    internal static string NotePrefix
    {
        get { return "refs/notes/"; }
    }
    
    private static void UpdateGitVersion()
    {
        var start = new ProcessStartInfo();
        start.FileName = "git";
        start.Arguments = "--version";
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;

        var proc = new Process() { StartInfo = start };
        try
        {
            proc.Start();

            var rs = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(rs))
            {
                var gitVersionString = rs.Trim();

                var match = REG_GIT_VERSION().Match(gitVersionString);
                if (match.Success)
                {
                    var major = int.Parse(match.Groups[1].Value);
                    var minor = int.Parse(match.Groups[2].Value);
                    var build = int.Parse(match.Groups[3].Value);
                    GitVersion = new Version(major, minor, build);
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        proc.Close();
    }

    public static async Task UpdateVersionIfNeededAsync()
    {
        if(GitVersion is {Major: 0, Minor: 0, Build: 0})
            await Task.Run(UpdateGitVersion);
    }

    
    public static async Task StageChanges(string repo, List<Models.Change> changes)
    {
        var count = changes.Count;
        if (count == 0)
            return;
        
        await UpdateVersionIfNeededAsync();

        if (GitVersion >= Models.GitVersions.ADD_WITH_PATHSPECFILE)
        {
            var paths = new List<string>();
            foreach (var c in changes)
                paths.Add(c.Path);

            var tmpFile = Path.GetTempFileName();
            await File.WriteAllLinesAsync(tmpFile, paths);
            await Task.Run(() => new Add(repo, tmpFile).Exec());
            File.Delete(tmpFile);
        }
        else
        {
            var paths = new List<string>();
            foreach (var c in changes)
                paths.Add(c.Path);

            for (int i = 0; i < count; i += 10)
            {
                var step = paths.GetRange(i, Math.Min(10, count - i));
                await Task.Run(() => new Add(repo, step).Exec());
            }
        }
    }

    public static async Task UnstageChanges(string repo, List<Models.Change> changes)
    {
        if (changes.Count == 0)
            return;
        
        for (int i = 0; i < changes.Count; i += 10)
        {
            var count = Math.Min(10, changes.Count - i);
            var step = changes.GetRange(i, count);
            await Task.Run(() => new Reset(repo, step).Exec());
        }
    }
}