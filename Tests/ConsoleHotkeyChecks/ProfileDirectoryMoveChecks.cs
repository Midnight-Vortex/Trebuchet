using TrebuchetLib;
using TrebuchetLib.Services;

/// <summary>
/// Moving the saves and profiles to another directory: which folders belong to an edition, what stands against a
/// target, the copy (extracted mods left out, links refused, compared, removed again when it fails, marked as not in
/// use until the settings are switched), removing the old folders after the restart and the directory the settings
/// name. All in temporary folders.
/// </summary>
static class ProfileDirectoryMoveChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        var root = Path.Combine(Path.GetTempPath(), "TrebuchetProfileMoveChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var edition = GameEdition.Enhanced;
            var source = Directory.CreateDirectory(Path.Combine(root, "Documents")).FullName;
            var target = Directory.CreateDirectory(Path.Combine(root, "Other drive")).FullName;
            var game = Directory.CreateDirectory(Path.Combine(root, "Game")).FullName;
            void Write(string folder, string relative, string text)
            {
                var path = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            bool HasItems(string folder) => ProfileDirectoryMove.GetItems(edition).Any(x => Directory.Exists(Path.Combine(folder, x)));
            string Marker(string folder) => Path.Combine(folder, "Enhanced", ".trebuchet-move-unfinished");
            async Task<Exception?> Copy(string from, string to, IProgress<double>? progress = null, CancellationToken token = default)
            {
                try { await ProfileDirectoryMove.CopyAsync(from, to, edition, progress, token); }
                catch (Exception ex) { return ex; }
                return null;
            }

            Check(ProfileDirectoryMove.GetItems(edition).SequenceEqual(new[] { "ClientProfiles", "ServerProfiles", "Modlists", "Sync", "Backups" }
                      .Select(x => Path.Combine("Enhanced", x))),
                "The saves and profiles of Enhanced are five folders below its edition folder");
            Check(ProfileDirectoryMove.GetItems(GameEdition.Legacy).Last() == "Backups"
                  && ProfileDirectoryMove.GetItems(GameEdition.Legacy).First() == Path.Combine("Live", "ClientProfiles"),
                "The backups of Legacy lie next to its edition folder");

            Write(source, @"Enhanced\ClientProfiles\Main\profile.json", "{}");
            Write(source, @"Enhanced\ClientProfiles\Main\Logs\ConanSandbox.log", "log line");
            Write(source, @"Enhanced\ClientProfiles\Main\ExtractedMods\Big-Windows.pak", new string('x', 5000));
            Write(source, @"Enhanced\ClientProfiles\Main\Config\ExtractedMods\keep.txt", "not the game's folder");
            Write(source, @"Enhanced\ServerProfiles\Dev\game_0.db", "database");
            Write(source, @"Enhanced\ServerProfiles\Dev\Engine.ini", "[protected]");
            File.SetAttributes(Path.Combine(source, @"Enhanced\ServerProfiles\Dev\Engine.ini"), FileAttributes.ReadOnly);
            Write(source, @"Enhanced\ServerProfiles\Dev\ExtractedMods\Big-WindowsServer.pak", new string('y', 7000));
            Write(source, @"Enhanced\Modlists\RP.json", "[1,2]");
            Write(source, @"Enhanced\Backups\Server.Dev\backup.zip", "zip");
            Write(source, @"Live\ClientProfiles\Old\profile.json", "another edition");
            var size = ProfileDirectoryMove.Measure(source, edition);
            Check(size.Files == 7 && size.Bytes == "{}".Length + "log line".Length + "not the game's folder".Length + "database".Length
                      + "[protected]".Length + "[1,2]".Length + "zip".Length,
                "The extracted mods of a save and other editions do not count: " + size);

            ProfileDirectoryCheck CheckTarget(string to) => ProfileDirectoryMove.Check(source, to, edition, [game, ""]);
            Check(CheckTarget(Path.Combine(root, "missing")) == ProfileDirectoryCheck.Invalid && CheckTarget("") == ProfileDirectoryCheck.Invalid,
                "A missing directory is refused");
            Check(CheckTarget(source + Path.DirectorySeparatorChar) == ProfileDirectoryCheck.Same, "The directory in use is told as such");
            Check(CheckTarget(Path.Combine(source, @"Enhanced\ClientProfiles\Main")) == ProfileDirectoryCheck.Overlap
                  && CheckTarget(Directory.CreateDirectory(Path.Combine(game, "Sub")).FullName) == ProfileDirectoryCheck.Overlap,
                "A directory inside a save or inside the game is refused");
            Check(ProfileDirectoryMove.Check(source, @"\\server\share\saves", edition, []) == ProfileDirectoryCheck.Network,
                "A network path is refused without asking the network");
            Write(target, @"Enhanced\ServerInstances\Instance_0\server.exe", "downloaded data may share the directory");
            Check(CheckTarget(target) == ProfileDirectoryCheck.Ok, "Other folders in the target (server installations) do not stand against it");
            Write(target, @"Enhanced\Modlists\Theirs.json", "[9]");
            Check(CheckTarget(target) == ProfileDirectoryCheck.HasData, "Saves or profiles in the target are found");
            Check(await Copy(source, target) is IOException && File.ReadAllText(Path.Combine(target, @"Enhanced\Modlists\Theirs.json")) == "[9]"
                  && !Directory.Exists(Path.Combine(target, @"Enhanced\ClientProfiles")) && !File.Exists(Marker(target)),
                "Nothing is copied into a target that holds profiles, and they stay");
            File.Delete(Path.Combine(target, @"Enhanced\Modlists\Theirs.json"));
            Directory.Delete(Path.Combine(target, @"Enhanced\Modlists"));

            // Legacy keeps its backups beside the edition folder: a Backups folder of someone else is not taken for saves.
            var legacyTarget = Directory.CreateDirectory(Path.Combine(root, "Legacy target")).FullName;
            Write(legacyTarget, @"Backups\holiday.zip", "not from Trebuchet");
            Check(ProfileDirectoryMove.Check(source, legacyTarget, GameEdition.Legacy, []) == ProfileDirectoryCheck.Occupied,
                "A foreign Backups folder is not offered as saves that are there");
            Write(legacyTarget, @"Live\Modlists\Old.json", "[]");
            Check(ProfileDirectoryMove.Check(source, legacyTarget, GameEdition.Legacy, []) == ProfileDirectoryCheck.HasData,
                "Backups next to saves of the edition are saves that are there");

            // A save that is a link to another folder: the copy would leave it out, so the move is refused.
            var outside = Directory.CreateDirectory(Path.Combine(root, "Outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "precious.txt"), "do not touch");
            var link = Path.Combine(source, @"Enhanced\ServerProfiles\OnOtherDrive");
            if (OperatingSystem.IsWindows())
            {
                tot_lib.OsSpecific.JunctionPoint.Create(link, outside, false);
                Check(ProfileDirectoryMove.FindLinks(source, edition).SequenceEqual([@"Enhanced\ServerProfiles\OnOtherDrive"])
                      && ProfileDirectoryMove.Measure(source, edition) == size,
                    "A save that is a link is found; what it points to does not count");
                Check(await Copy(source, target) is IOException && !HasItems(target) && !File.Exists(Marker(target)),
                    "Saves with a link are not moved, and nothing is written");
                Directory.Delete(link, false);
            }
            Check(ProfileDirectoryMove.FindLinks(source, edition).Count == 0 && File.Exists(Path.Combine(outside, "precious.txt")),
                "Without the link there is nothing against the move");

            // A copy that fails (a file in use by a server) removes what it copied and leaves the source alone.
            using (new FileStream(Path.Combine(source, @"Enhanced\ServerProfiles\Dev\game_0.db"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failed = await Copy(source, target);
                Check(failed is IOException || !OperatingSystem.IsWindows(), "A save in use stops the copy");
                if (failed is not null)
                    Check(!HasItems(target) && !File.Exists(Marker(target))
                          && File.Exists(Path.Combine(target, @"Enhanced\ServerInstances\Instance_0\server.exe"))
                          && ProfileDirectoryMove.Measure(source, edition) == size,
                        "A failed copy leaves no half copy, keeps the other folders of the target and the source");
            }
            if (!OperatingSystem.IsWindows()) ProfileDirectoryMove.Discard(target, edition);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Check(await Copy(source, target, null, cancelled.Token) is OperationCanceledException && !HasItems(target),
                    "A cancelled copy leaves nothing behind");
            }

            // Something writes into the saves while they are copied (a backup of a server that just stopped).
            var backup = Path.Combine(source, @"Enhanced\Backups\Server.Dev\backup.zip");
            var stamp = File.GetLastWriteTimeUtc(backup);
            var written = 0;
            var changed = await Copy(source, target, new SyncProgress(_ =>
            {
                if (Interlocked.Exchange(ref written, 1) == 0) File.SetLastWriteTimeUtc(backup, stamp.AddMinutes(5));
            }));
            Check(changed is IOException && changed.Message.Contains("changed") && !HasItems(target) && !File.Exists(Marker(target)),
                "Saves that change during the copy are not moved: " + changed?.Message);

            // What a move left behind that never finished (power loss): known by its note, removed before a new copy.
            Write(target, @"Enhanced\ClientProfiles\Main\half.json", "{");
            File.WriteAllText(Marker(target), "unfinished");
            Check(CheckTarget(target) == ProfileDirectoryCheck.Unfinished, "A copy that was never finished is not taken for saves");
            Check(ProfileDirectoryMove.Discard(target, edition) && CheckTarget(target) == ProfileDirectoryCheck.Ok
                  && File.Exists(Path.Combine(target, @"Enhanced\ServerInstances\Instance_0\server.exe")),
                "The leftover is removed, the other folders of the target stay");

            var reported = new List<double>();
            Check(await Copy(source, target, new SyncProgress(reported.Add)) is null, "The copy runs through");
            Check(ProfileDirectoryMove.Measure(target, edition) == size
                  && File.ReadAllText(Path.Combine(target, @"Enhanced\ServerProfiles\Dev\game_0.db")) == "database"
                  && File.ReadAllText(Path.Combine(target, @"Enhanced\ClientProfiles\Main\Config\ExtractedMods\keep.txt")) == "not the game's folder",
                "The copy holds every file of the saves and profiles");
            Check(!Directory.Exists(Path.Combine(target, @"Enhanced\ClientProfiles\Main\ExtractedMods"))
                  && !Directory.Exists(Path.Combine(target, @"Enhanced\ServerProfiles\Dev\ExtractedMods"))
                  && !Directory.Exists(Path.Combine(target, "Live")),
                "Extracted mods and other editions are not copied");
            Check((File.GetAttributes(Path.Combine(target, @"Enhanced\ServerProfiles\Dev\Engine.ini")) & FileAttributes.ReadOnly) != 0
                  && (File.GetAttributes(Path.Combine(target, @"Enhanced\ServerProfiles\Dev\game_0.db")) & FileAttributes.ReadOnly) == 0,
                "A write protected file stays write protected");
            Check(ProfileDirectoryMove.Measure(source, edition) == size && reported.Count > 0 && reported.All(x => x is >= 0 and <= 1) && reported[^1] == 1,
                "The source is untouched and the progress ends at 1");
            Check(File.Exists(Marker(target)) && CheckTarget(target) == ProfileDirectoryCheck.Unfinished,
                "Until the settings are switched the copy is marked as not in use");

            Check(ProfileDirectoryMove.MapPath(Path.Combine(source, @"Enhanced\ClientProfiles\Main"), source, target, edition)
                      == Path.Combine(target, @"Enhanced\ClientProfiles\Main")
                  && ProfileDirectoryMove.MapPath(Path.Combine(source, @"Live\ClientProfiles\Old"), source, target, edition) is null
                  && ProfileDirectoryMove.MapPath(game, source, target, edition) is null,
                "A Saved link into a moved save follows it, other links stay");

            // After the restart: the settings name the new directory, the old folders go.
            var settings = Path.Combine(root, "settings.json");
            var config = Config.CreateConfig(settings);
            var setup = new AppSetup(config, edition, false, false);
            Check(setup.GetDataDirectory().FullName == AppSetup.GetDataDirectoryDefault().FullName && setup.IsDataDirectoryAvailable()
                  && ProfileDirectoryMove.FinishCleanup(setup) is null,
                "Without a directory in the settings the Documents folder is used and nothing is cleaned up");
            config.ProfileDirectory = Path.Combine(root, "gone");
            config.ProfileDirectoryCleanup = source;
            Check(!setup.IsDataDirectoryAvailable() && setup.GetDataDirectory().FullName == Path.Combine(root, "gone"),
                "A directory of the settings that is not there is not replaced by the Documents folder");
            Check(ProfileDirectoryMove.FinishCleanup(setup) is not null && ProfileDirectoryMove.Measure(source, edition) == size
                  && config.ProfileDirectoryCleanup == source,
                "While the new directory is not there the old saves are kept and the clean up waits");
            config.ProfileDirectory = source;
            Check(ProfileDirectoryMove.FinishCleanup(setup) is not null && ProfileDirectoryMove.Measure(source, edition) == size
                  && config.ProfileDirectoryCleanup == "",
                "The directory in use is never cleaned up");

            config.ProfileDirectory = target;
            config.ProfileDirectoryCleanup = source;
            var lost = Path.Combine(target, @"Enhanced\Modlists\RP.json");
            File.Delete(lost);
            Check(ProfileDirectoryMove.FinishCleanup(setup) is { } kept && kept.Contains("holds less")
                  && ProfileDirectoryMove.Measure(source, edition) == size && config.ProfileDirectoryCleanup == "",
                "The old saves are kept when the new directory holds less than they do");
            File.WriteAllText(lost, "[1,2]");

            // A link that appeared in the old folders after the copy is removed as a link.
            if (OperatingSystem.IsWindows()) tot_lib.OsSpecific.JunctionPoint.Create(link, outside, false);
            File.WriteAllText(Marker(target), "unfinished");
            config.ProfileDirectoryCleanup = source;
            config.SaveFile();
            Check(setup.IsDataDirectoryAvailable() && ProfileDirectoryMove.FinishCleanup(setup) is { } done && done.Contains("are removed"),
                "The old folders are removed after the move");
            Check(!Directory.Exists(Path.Combine(source, "Enhanced")) && File.Exists(Path.Combine(source, @"Live\ClientProfiles\Old\profile.json")),
                "The old folders of the edition are gone, with its edition folder and the write protected file; another edition stays");
            Check(File.ReadAllText(Path.Combine(outside, "precious.txt")) == "do not touch", "What a link in the old folders pointed to is still there");
            Check(ProfileDirectoryMove.Measure(target, edition) == size && File.Exists(Path.Combine(target, @"Enhanced\ServerInstances\Instance_0\server.exe"))
                  && !File.Exists(Marker(target)),
                "The new directory is untouched by the clean up and no longer marked as not in use");
            var reloaded = Config.LoadConfig(settings);
            Check(reloaded.ProfileDirectory == target && reloaded.ProfileDirectoryCleanup == "", "The settings keep the directory and forget the clean up");
            Check(System.Text.Json.JsonSerializer.Deserialize<Config>("{}")?.ProfileDirectory == "", "Settings of before have no directory: Documents");

            Check(ProfileDirectoryMove.Delete(target, edition) && Directory.Exists(Path.Combine(target, @"Enhanced\ServerInstances"))
                  && !Directory.Exists(Path.Combine(target, @"Enhanced\ClientProfiles")),
                "Removing the saves keeps an edition folder that holds other things");
        }
        finally
        {
            var resolvedRoot = Path.GetFullPath(root);
            if (!resolvedRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolvedRoot).StartsWith("TrebuchetProfileMoveChecks-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup path.");
            Clean(new DirectoryInfo(resolvedRoot));
        }

        return count;
    }

    // A link is removed as a link (never what it points to), write protection is lifted.
    private static void Clean(DirectoryInfo folder)
    {
        if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            folder.Delete(false);
            return;
        }
        foreach (var child in folder.EnumerateDirectories()) Clean(child);
        foreach (var file in folder.EnumerateFiles())
        {
            file.Attributes = FileAttributes.Normal;
            file.Delete();
        }
        folder.Delete(false);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        private readonly object _gate = new();
        public void Report(double value) { lock (_gate) report(value); }
    }
}
