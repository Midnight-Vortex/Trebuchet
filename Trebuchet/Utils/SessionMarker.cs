using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TrebuchetLib.Processes;

namespace Trebuchet.Utils;

/// <summary>
/// A hard end of Trebuchet (killed from outside, a console event, power loss, a native crash) runs no code, so no crash
/// report and no log line can be written at that moment. This leaves a small marker file while Trebuchet runs and
/// removes it on every normal exit. A marker still there at the next start, from a process that is gone, is the sign
/// of such an end: it is turned into a report with the last lines of the log, and the start says so in the log.
/// </summary>
public static class SessionMarker
{
    public const string FileName = "running.session";
    private const int ReportLogLines = 60;
    private const int KeptReports = 20;

    private static string? _markerPath;

    private sealed record Marker(int Pid, DateTime StartedUtc, string Version);

    /// <summary>The report written at this start for the previous session, null when it ended normally.</summary>
    public static string? PreviousReport { get; private set; }

    /// <summary>Called once at the start of the running Trebuchet. Never throws: a diagnostic must not stop the program.</summary>
    public static void Begin(string logDirectory, string version)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, FileName);
            PreviousReport = ReportPrevious(logDirectory, path);
            File.WriteAllText(path, JsonSerializer.Serialize(new Marker(Environment.ProcessId, DateTime.UtcNow, version)));
            if (_markerPath is null) AppDomain.CurrentDomain.ProcessExit += (_, _) => End();
            _markerPath = path;
        }
        catch (Exception)
        {
            // No marker, no report: nothing else is affected.
        }
    }

    /// <summary>A normal exit: removes the marker of this process (a marker of a newer instance stays).</summary>
    public static void End()
    {
        try
        {
            var path = _markerPath;
            if (path is null || !File.Exists(path)) return;
            if (Read(path) is { } marker && marker.Pid != Environment.ProcessId) return;
            File.Delete(path);
        }
        catch (Exception)
        {
            // Left behind: the next start reports it, which is the safe side.
        }
    }

    private static string? ReportPrevious(string logDirectory, string markerPath)
    {
        if (!File.Exists(markerPath)) return null;
        var previous = Read(markerPath);
        // A damaged marker says nothing. A process that still runs is a Trebuchet that restarted itself (its marker is replaced).
        if (previous is null || ProcessSafety.IsPidAlive(previous.Pid)) return null;

        var reportPath = Path.Combine(logDirectory, $@"crash-unclean-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            Message = @"Trebuchet ended without a clean exit: killed from outside, a console event, power loss or a native crash. No exception was raised, so this report only holds the last lines of the log.",
            PreviousProcessId = previous.Pid,
            PreviousStartUtc = previous.StartedUtc,
            PreviousVersion = previous.Version,
            LastLogLines = LastLogLines(logDirectory)
        }, new JsonSerializerOptions { WriteIndented = true }));
        DeleteOldReports(logDirectory);
        return reportPath;
    }

    private static Marker? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Marker>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The log of the newest day: the session that ended wrote its last lines there.
    private static List<string> LastLogLines(string logDirectory)
    {
        try
        {
            var newest = new DirectoryInfo(logDirectory).GetFiles(@"app*.log").OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null) return [];
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line.Length > 600 ? line[..600] : line);
                if (lines.Count > ReportLogLines) lines.Dequeue();
            }
            return lines.ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void DeleteOldReports(string logDirectory)
    {
        try
        {
            foreach (var old in new DirectoryInfo(logDirectory).GetFiles(@"crash-unclean-*.json")
                         .OrderByDescending(x => x.LastWriteTimeUtc).Skip(KeptReports))
                old.Delete();
        }
        catch (Exception)
        {
            // Old reports stay: not worth a failure.
        }
    }
}
