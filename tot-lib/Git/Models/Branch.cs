namespace tot_lib.Git.Models;

public class BranchTrackStatus
{
    public List<string> Ahead { get; set; } = [];
    public List<string> Behind { get; set; } = [];

    public bool IsVisible => Ahead.Count > 0 || Behind.Count > 0;

    public override string ToString()
    {
        if (Ahead.Count == 0 && Behind.Count == 0)
            return string.Empty;

        var track = "";
        if (Ahead.Count > 0)
            track += $"{Ahead.Count}↑";
        if (Behind.Count > 0)
            track += $" {Behind.Count}↓";
        return track.Trim();
    }
}

public class Branch
{
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Head { get; set; } = string.Empty;
    public bool IsLocal { get; set; }
    public bool IsCurrent { get; set; }
    public bool IsDetachedHead { get; set; }
    public string Upstream { get; set; } = string.Empty;
    public BranchTrackStatus? TrackStatus { get; set; }
    public string Remote { get; set; } = string.Empty;
    public bool IsUpsteamGone { get; set; }

    public string FriendlyName => IsLocal ? Name : $"{Remote}/{Name}";
}