using System.Text.RegularExpressions;
using tot_lib.Git.Models;

namespace tot_lib.Git
{
    public partial class QueryLocalChanges : GitCommand
    {
        [GeneratedRegex(@"^(\s?[\w\?]{1,4})\s+(.+)$")]
        private static partial Regex REG_FORMAT();
        private static readonly string[] UNTRACKED = ["no", "all"];

        public QueryLocalChanges(string repo, bool includeUntracked = true)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"--no-optional-locks status -u{UNTRACKED[includeUntracked ? 1 : 0]} --ignore-submodules=dirty --porcelain";
        }

        public List<Change> Result()
        {
            Exec();
            return _changes;
        }

        protected override void OnReadline(string line)
        {
            var match = REG_FORMAT().Match(line);
            if (!match.Success)
                return;

            var change = new Change() { Path = match.Groups[2].Value };
            var status = match.Groups[1].Value;

            switch (status)
            {
                case " M":
                    change.Set(ChangeState.None, ChangeState.Modified);
                    break;
                case " T":
                    change.Set(ChangeState.None, ChangeState.TypeChanged);
                    break;
                case " A":
                    change.Set(ChangeState.None, ChangeState.Added);
                    break;
                case " D":
                    change.Set(ChangeState.None, ChangeState.Deleted);
                    break;
                case " R":
                    change.Set(ChangeState.None, ChangeState.Renamed);
                    break;
                case " C":
                    change.Set(ChangeState.None, ChangeState.Copied);
                    break;
                case "M":
                    change.Set(ChangeState.Modified);
                    break;
                case "MM":
                    change.Set(ChangeState.Modified, ChangeState.Modified);
                    break;
                case "MT":
                    change.Set(ChangeState.Modified, ChangeState.TypeChanged);
                    break;
                case "MD":
                    change.Set(ChangeState.Modified, ChangeState.Deleted);
                    break;
                case "T":
                    change.Set(ChangeState.TypeChanged);
                    break;
                case "TM":
                    change.Set(ChangeState.TypeChanged, ChangeState.Modified);
                    break;
                case "TT":
                    change.Set(ChangeState.TypeChanged, ChangeState.TypeChanged);
                    break;
                case "TD":
                    change.Set(ChangeState.TypeChanged, ChangeState.Deleted);
                    break;
                case "A":
                    change.Set(ChangeState.Added);
                    break;
                case "AM":
                    change.Set(ChangeState.Added, ChangeState.Modified);
                    break;
                case "AT":
                    change.Set(ChangeState.Added, ChangeState.TypeChanged);
                    break;
                case "AD":
                    change.Set(ChangeState.Added, ChangeState.Deleted);
                    break;
                case "D":
                    change.Set(ChangeState.Deleted);
                    break;
                case "R":
                    change.Set(ChangeState.Renamed);
                    break;
                case "RM":
                    change.Set(ChangeState.Renamed, ChangeState.Modified);
                    break;
                case "RT":
                    change.Set(ChangeState.Renamed, ChangeState.TypeChanged);
                    break;
                case "RD":
                    change.Set(ChangeState.Renamed, ChangeState.Deleted);
                    break;
                case "C":
                    change.Set(ChangeState.Copied);
                    break;
                case "CM":
                    change.Set(ChangeState.Copied, ChangeState.Modified);
                    break;
                case "CT":
                    change.Set(ChangeState.Copied, ChangeState.TypeChanged);
                    break;
                case "CD":
                    change.Set(ChangeState.Copied, ChangeState.Deleted);
                    break;
                case "DR":
                    change.Set(ChangeState.Deleted, ChangeState.Renamed);
                    break;
                case "DC":
                    change.Set(ChangeState.Deleted, ChangeState.Copied);
                    break;
                case "DD":
                    change.Set(ChangeState.Deleted, ChangeState.Deleted);
                    break;
                case "AU":
                    change.Set(ChangeState.Added, ChangeState.Unmerged);
                    break;
                case "UD":
                    change.Set(ChangeState.Unmerged, ChangeState.Deleted);
                    break;
                case "UA":
                    change.Set(ChangeState.Unmerged, ChangeState.Added);
                    break;
                case "DU":
                    change.Set(ChangeState.Deleted, ChangeState.Unmerged);
                    break;
                case "AA":
                    change.Set(ChangeState.Added, ChangeState.Added);
                    break;
                case "UU":
                    change.Set(ChangeState.Unmerged, ChangeState.Unmerged);
                    break;
                case "??":
                    change.Set(ChangeState.Untracked, ChangeState.Untracked);
                    break;
                default:
                    return;
            }

            _changes.Add(change);
        }

        private readonly List<Change> _changes = new List<Change>();
    }
}
