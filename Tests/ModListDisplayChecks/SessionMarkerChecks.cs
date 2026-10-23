using System.Diagnostics;
using System.Text.Json;
using Trebuchet.Utils;

/// <summary>
/// A hard end of Trebuchet leaves no crash report because no code runs: the marker file of the running session turns it
/// into a report at the next start. A normal exit leaves nothing behind.
/// </summary>
public static class SessionMarkerChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var folder = Path.Combine(Path.GetTempPath(), "TrebuchetSessionMarker-" + Guid.NewGuid().ToString("N"));
        try
        {
            var marker = Path.Combine(folder, SessionMarker.FileName);

            // First start ever: no marker, no report, and a marker of this process.
            SessionMarker.Begin(folder, "1.0");
            Check(SessionMarker.PreviousReport is null && File.Exists(marker)
                  && JsonDocument.Parse(File.ReadAllText(marker)).RootElement.GetProperty("Pid").GetInt32() == Environment.ProcessId,
                "The first start leaves a marker of this process and no report");

            // A normal exit removes it.
            SessionMarker.End();
            Check(!File.Exists(marker), "A normal exit removes the marker");
            SessionMarker.End();
            Check(true, "Removing it twice is fine");

            // The marker of a process that is gone, with a log of the last session: that is a hard end.
            var logLines = Enumerable.Range(1, 100).Select(i => $"2026-10-23 20:00:{i % 60:00} [INF] Launcher : line {i}").ToList();
            File.WriteAllLines(Path.Combine(folder, "app20261023.log"), logLines);
            File.WriteAllText(marker, JsonSerializer.Serialize(new { Pid = FreePid(), StartedUtc = DateTime.UtcNow.AddHours(-2), Version = "0.9.21" }));
            SessionMarker.Begin(folder, "0.9.22");
            var report = SessionMarker.PreviousReport;
            Check(report is not null && File.Exists(report) && Path.GetFileName(report).StartsWith("crash-unclean-", StringComparison.Ordinal),
                "A marker of a process that is gone is reported at the next start");
            using var json = JsonDocument.Parse(File.ReadAllText(report!));
            var lines = json.RootElement.GetProperty("LastLogLines").EnumerateArray().Select(x => x.GetString()).ToList();
            Check(lines.Count == 60 && lines[^1]!.EndsWith("line 100", StringComparison.Ordinal) && lines[0]!.EndsWith("line 41", StringComparison.Ordinal)
                  && json.RootElement.GetProperty("PreviousVersion").GetString() == "0.9.21"
                  && json.RootElement.GetProperty("Message").GetString()!.Contains("without a clean exit", StringComparison.Ordinal),
                "The report holds the last 60 lines of the log and the version of the session that ended");
            Check(JsonDocument.Parse(File.ReadAllText(marker)).RootElement.GetProperty("Pid").GetInt32() == Environment.ProcessId,
                "The new session replaces the marker");

            // A marker of a process that still runs (a Trebuchet that restarted itself) is no report.
            var reportsBefore = Directory.GetFiles(folder, "crash-unclean-*.json").Length;
            SessionMarker.Begin(folder, "0.9.22");
            Check(SessionMarker.PreviousReport is null && Directory.GetFiles(folder, "crash-unclean-*.json").Length == reportsBefore,
                "A marker of a process that still runs is no hard end");

            // A marker of another instance stays when this one exits.
            File.WriteAllText(marker, JsonSerializer.Serialize(new { Pid = Environment.ProcessId + 1, StartedUtc = DateTime.UtcNow, Version = "x" }));
            SessionMarker.End();
            Check(File.Exists(marker), "The exit of the old instance does not take the marker of the new one");

            // Damaged markers say nothing, and nothing ever throws.
            foreach (var damaged in new[] { "", "not json", "{", "{\"Pid\": \"x\"}", "null" })
            {
                File.WriteAllText(marker, damaged);
                SessionMarker.Begin(folder, "0.9.22");
                Check(SessionMarker.PreviousReport is null, $"A damaged marker ('{damaged}') is no report");
            }
            var unwritable = Path.Combine(folder, "app20261023.log");
            SessionMarker.Begin(unwritable, "0.9.22");
            Check(true, "A log folder that is a file does not stop the start");

            // Only the newest reports are kept.
            for (var i = 0; i < 30; i++)
            {
                File.WriteAllText(marker, JsonSerializer.Serialize(new { Pid = FreePid(), StartedUtc = DateTime.UtcNow, Version = "x" }));
                SessionMarker.Begin(folder, "0.9.22");
                Thread.Sleep(5);
            }
            Check(Directory.GetFiles(folder, "crash-unclean-*.json").Length <= 20, "Old reports are cleaned up");
        }
        finally
        {
            SessionMarker.End();
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }
        return count;
    }

    // A process id nobody has: far above any real one.
    private static int FreePid()
    {
        var pid = 2_000_000_000;
        while (true)
        {
            try
            {
                using var _ = Process.GetProcessById(pid);
                pid--;
            }
            catch (ArgumentException)
            {
                return pid;
            }
        }
    }
}
