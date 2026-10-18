namespace tot_lib.Git.Models
{
    public class Worktree
    {
        public string Branch { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Head { get; set; } = string.Empty;
        public bool IsBare { get; set; } = false;
        public bool IsDetached { get; set; } = false;
        public bool IsLocked { get; set; } = false;

        public string Name
        {
            get
            {
                if (IsDetached)
                    return $"deteched HEAD at {Head.Substring(10)}";

                if (Branch.StartsWith("refs/heads/", System.StringComparison.Ordinal))
                    return Branch.Substring(11);

                if (Branch.StartsWith("refs/remotes/", System.StringComparison.Ordinal))
                    return Branch.Substring(13);

                return Branch;
            }
        }
        

    }
}