namespace tot_lib.Git;

public class QueryRepositoryRootPath : GitCommand
{
    public QueryRepositoryRootPath(string path)
    {
        WorkingDirectory = path;
        Args = "rev-parse --show-toplevel";
    }
}