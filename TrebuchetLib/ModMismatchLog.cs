using System.Globalization;
using System.Text.RegularExpressions;

namespace TrebuchetLib;

/// <summary>A mod the server named when it refused the client. <see cref="WorkshopId"/> is 0 when the server gave none.</summary>
public sealed record ModMismatchEntry(string Name, ulong WorkshopId);

/// <summary>The server mod list from the last mod mismatch found in a client log, in the server's load order.</summary>
public sealed record ModMismatchReport(
    string LogFile,
    DateTime? TimeUtc,
    string Server,
    IReadOnlyList<ModMismatchEntry> Mods)
{
    /// <summary>Workshop ids in server order, duplicates removed, entries without an id left out.</summary>
    public List<string> GetModlist() => Mods
        .Where(x => x.WorkshopId != 0)
        .Select(x => x.WorkshopId.ToString(CultureInfo.InvariantCulture))
        .Distinct()
        .ToList();

    /// <summary>Entries the server sent without a workshop id (they cannot be put in a Trebuchet mod list).</summary>
    public IEnumerable<string> GetEntriesWithoutId() => Mods.Where(x => x.WorkshopId == 0).Select(x => x.Name);
}

/// <summary>
/// Reads the mod list a server sends when it refuses a client with a mod mismatch. The client writes it to
/// ConanSandbox.log as "LogNet: NetConnection::Close() ... from NMT_Failure ModMismatch: Name (id), Name (id), ...".
/// Every mod of the server is listed with its workshop id, in the server's load order.
/// </summary>
public static class ModMismatchLog
{
    private const string CloseMarker = "from NMT_Failure ModMismatch:";
    /// <summary>The same list on an Error line, so it is still there when LogNet is filtered down to errors.</summary>
    private const string BroadcastMarker = "FailureDetails = ModMismatch, ErrorString = ";
    private const string BroadcastEnd = ", Driver = ";
    private const string FailureMarker = "Details: ModMismatch Error: '";
    private const string CloseReasonMarker = "NMT_CloseReason: (Server Disconnect Reasons) ";
    private const string BrowseMarker = "LogNet: Browse: ";
    private const string ClientLogPattern = "ConanSandbox*.log";

    /// <summary>Name, then the id in brackets, then a comma or the end. The lazy name keeps brackets that belong to it ("Grit (Weapons) (123)").</summary>
    private static readonly Regex Entry = new(@"\G\s*(?<name>.*?)\s*\((?<id>\d{1,20})\)\s*(?:,|$)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    /// <summary>Unreal log time stamp "[2026.11.06-16.08.52:113]", written in UTC.</summary>
    private static readonly Regex TimeStamp = new(@"^\[(?<t>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}):\d{3}\]",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>The mod list text of a mismatch line, or null when the line is not one.</summary>
    public static string? GetListText(string line)
    {
        var index = line.IndexOf(CloseMarker, StringComparison.Ordinal);
        if (index >= 0) return line[(index + CloseMarker.Length)..];

        index = line.IndexOf(BroadcastMarker, StringComparison.Ordinal);
        if (index >= 0)
        {
            var list = line[(index + BroadcastMarker.Length)..];
            var end = list.LastIndexOf(BroadcastEnd, StringComparison.Ordinal);
            return end >= 0 ? list[..end] : list;
        }

        index = line.IndexOf(FailureMarker, StringComparison.Ordinal);
        if (index < 0) return null;
        var text = line[(index + FailureMarker.Length)..].TrimEnd();
        return text.EndsWith('\'') ? text[..^1] : text;
    }

    /// <summary>Splits "Name (id), Name (id), ...". Text that has no id is kept as one entry with id 0.</summary>
    public static List<ModMismatchEntry> ParseList(string text)
    {
        var list = text.Trim();
        if (list.EndsWith('.')) list = list[..^1].TrimEnd();

        var entries = new List<ModMismatchEntry>();
        var position = 0;
        foreach (Match match in Entry.Matches(list))
        {
            if (match.Index != position || match.Length == 0) break;
            position = match.Index + match.Length;
            var name = match.Groups["name"].Value.Trim();
            ulong.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id);
            entries.Add(new ModMismatchEntry(name, id));
        }

        var rest = list[position..].Trim().Trim(',').Trim();
        if (rest.Length > 0) entries.Add(new ModMismatchEntry(rest, 0));
        return entries;
    }

    /// <summary>
    /// The last mod mismatch in the given log lines, or null. <paramref name="logFile"/> is only reported back.
    /// One refusal writes the list on up to three lines; of those the longest list is kept. A new join attempt
    /// (a "Browse" or close reason line) starts a new refusal.
    /// </summary>
    public static ModMismatchReport? FindLast(IEnumerable<string> lines, string logFile)
    {
        ModMismatchReport? last = null;
        var server = string.Empty;
        var newAttempt = true;
        foreach (var line in lines)
        {
            var server2 = GetServer(line);
            if (server2 is not null)
            {
                server = server2;
                newAttempt = true;
                continue;
            }

            var text = GetListText(line);
            if (text is null) continue;
            var mods = ParseList(text);
            if (mods.Count == 0) continue;
            if (last is null || newAttempt || mods.Count > last.Mods.Count)
                last = new ModMismatchReport(logFile, GetTimeUtc(line), server, mods);
            newAttempt = false;
        }
        return last;
    }

    /// <summary>
    /// The log folder of the game client: ClientPath\ConanSandbox\Saved\Logs. Trebuchet links Saved to the client profile
    /// that was started last, so the junctions are resolved for the file access.
    /// </summary>
    public static string GetClientLogFolder(string clientPath)
    {
        return Tools.ResolveReparsePointsInPath(Path.Combine(clientPath, Constants.FolderGameSave, Constants.FolderGameSaveLog));
    }

    /// <summary>
    /// The newest mod mismatch in the client logs of <paramref name="logDirectory"/>: ConanSandbox.log first, then the
    /// backups of earlier sessions, newest first. At most <paramref name="maxFiles"/> files are read (the game keeps about
    /// ten; a client log reaches 100 MB and more, so call this off the UI thread).
    /// The running client keeps its log open, so the files are read with write sharing.
    /// </summary>
    public static ModMismatchReport? FindNewest(string logDirectory, int maxFiles = 10, CancellationToken token = default)
    {
        if (!Directory.Exists(logDirectory)) return null;
        var files = new DirectoryInfo(logDirectory).GetFiles(ClientLogPattern)
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .Take(maxFiles);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var report = FindLast(ReadLines(file.FullName, token), file.FullName);
            if (report is not null) return report;
        }
        return null;
    }

    private static IEnumerable<string> ReadLines(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    /// <summary>The server the client tried to join: "NMT_CloseReason: ... 1.2.3.4:7777" or "LogNet: Browse: 1.2.3.4/Game/...".</summary>
    private static string? GetServer(string line)
    {
        var index = line.IndexOf(CloseReasonMarker, StringComparison.Ordinal);
        if (index >= 0) return line[(index + CloseReasonMarker.Length)..].Trim();

        index = line.IndexOf(BrowseMarker, StringComparison.Ordinal);
        if (index < 0) return null;
        var target = line[(index + BrowseMarker.Length)..].Trim();
        var slash = target.IndexOf('/');
        // "Browse: /Game/Maps/..." loads a local map, not a server.
        return slash == 0 ? string.Empty : slash > 0 ? target[..slash] : target;
    }

    private static DateTime? GetTimeUtc(string line)
    {
        var match = TimeStamp.Match(line);
        if (!match.Success) return null;
        return DateTime.TryParseExact(match.Groups["t"].Value, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;
    }
}
