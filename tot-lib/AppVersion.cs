using System.Diagnostics;
using System.Text.RegularExpressions;

namespace tot_lib;

public partial class AppVersion
{
    public AppVersion(int major, int minor, int build, int revision)
    {
        Major = major;
        Minor = minor;
        Build = build;
        Revision = revision;
    }

    public AppVersion(string label)
    {
        var result = VersionRegex().Match(label);
        Major = int.TryParse(result.Groups[1].Value, out var major) ? major : -1;
        Minor = int.TryParse(result.Groups[2].Value, out var minor) ? minor : -1;
        Build = int.TryParse(result.Groups[3].Value, out var build) ? build : -1;
        Revision = int.TryParse(result.Groups[4].Value, out var revision) ? revision : -1;
    }

    public AppVersion(FileVersionInfo fvi)
    {
        Major = fvi.FileMajorPart;
        Minor = fvi.FileMinorPart;
        Build = fvi.FileBuildPart;
        Revision = fvi.FilePrivatePart;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Build { get; }
    public int Revision { get; }

    public static AppVersion Default => new AppVersion(1, 0, 0, 0);

    [GeneratedRegex("[vV]?([0-9]+)\\.([0-9]+)(?:\\.([0-9]+))?(?:\\.([0-9]+))?")]
    private static partial Regex VersionRegex();

    public override string ToString()
    {
        return $"{(Major < 0 ? 0 : Major)}.{(Minor < 0 ? 0 : Minor)}.{(Build < 0 ? 0 : Build)}.{(Revision < 0 ? 0 : Revision)}";
    }

    public int CompareTo(AppVersion b)
    {
        var compare = Major.CompareTo(b.Major);
        if (compare != 0) return compare;
        compare = Minor.CompareTo(b.Minor);
        if (compare != 0) return compare;
        compare = Build.CompareTo(b.Build);
        if (compare != 0) return compare;
        return Revision.CompareTo(b.Revision);
    }
}