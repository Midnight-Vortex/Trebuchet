using TrebuchetLib;

/// <summary>
/// The server mod list in the mod mismatch lines of the client log (ConanSandbox.log): names with brackets and commas,
/// the three lines of one refusal, the server and time, and reading the newest log while the game still writes it.
/// </summary>
static class ModMismatchLogChecks
{
    // From a real Enhanced client log (2026-11-06), shortened to five of the 64 mods.
    private const string CloseLine = "[2026.11.06-16.08.52:113][996]LogNet: NetConnection::Close() [PendingNetDriver] [NoPC] [No Owner] from NMT_Failure ModMismatch: "
        + "ModControlPanel (Enhanced) (3722388367), Grit & Grease (Weapon Infusions) (3801774752), Cookies Bits & Bobs Enhanced | 1.0.7 (3738419983), "
        + "Ostavar: Salon (3734199233), Tot ! Enhanced Module Death 1.0.19 (3769293165).";
    private const string BroadcastLine = "[2026.11.06-16.08.52:114][996]LogNet: Error: UEngine::BroadcastNetworkFailure: FailureType = PendingConnectionFailure, "
        + "FailureDetails = ModMismatch, ErrorString = ModControlPanel (Enhanced) (3722388367), Grit & Grease (Weapon Infusions) (3801774752)., "
        + "Driver = Name:PendingNetDriver Def:GameNetDriver IpNetDriver_2147480524";
    private const string FailureLine = "[2026.11.06-16.08.52:114][996]LogNet: NetworkFailure: PendingConnectionFailure, Details: ModMismatch Error: "
        + "'ModControlPanel (Enhanced) (3722388367), Grit & Grease (Weapon Infusions) (3801774752).'";

    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        var five = ModMismatchLog.ParseList(ModMismatchLog.GetListText(CloseLine) ?? string.Empty);
        Check(five.Select(x => x.WorkshopId).SequenceEqual([3722388367UL, 3801774752UL, 3738419983UL, 3734199233UL, 3769293165UL]),
            "The mismatch line gives every workshop id in the order of the server");
        Check(five[0].Name == "ModControlPanel (Enhanced)" && five[1].Name == "Grit & Grease (Weapon Infusions)"
              && five[2].Name == "Cookies Bits & Bobs Enhanced | 1.0.7" && five[4].Name == "Tot ! Enhanced Module Death 1.0.19",
            "Brackets, colons and bars in a mod name stay in the name, the dot at the end goes");

        var broadcast = ModMismatchLog.ParseList(ModMismatchLog.GetListText(BroadcastLine) ?? string.Empty);
        Check(broadcast.Count == 2 && broadcast[1] == new ModMismatchEntry("Grit & Grease (Weapon Infusions)", 3801774752),
            "The error line of the refusal is read without the driver behind the list");
        var failure = ModMismatchLog.ParseList(ModMismatchLog.GetListText(FailureLine) ?? string.Empty);
        Check(failure.SequenceEqual(broadcast), "The quoted list of the network failure line gives the same mods");

        Check(ModMismatchLog.GetListText("[2026.11.06-16.06.46:383][  0]LogMaterial: Error: MaterialInstance [MI_Potato] clearing bHasStaticPermutationResource to avoid shadermap/blendmode mismatch with default material.") is null
              && ModMismatchLog.GetListText("[2026.11.06-16.08.52:114][996]LogNet: Warning: Network Failure: PendingNetDriver[PendingConnectionFailure]: Server full") is null,
            "Other mismatch and network failure lines are not a mod list");

        var comma = ModMismatchLog.ParseList("Swords, Shields and More (123456789), Lamps (987654321).");
        Check(comma.Count == 2 && comma[0] == new ModMismatchEntry("Swords, Shields and More", 123456789) && comma[1].Name == "Lamps",
            "A comma inside a mod name does not split the mod");

        var noId = ModMismatchLog.ParseList("Lamps (987654321), My local mod");
        Check(noId.Count == 2 && noId[1] == new ModMismatchEntry("My local mod", 0), "Text without a workshop id is kept as an entry without id");
        var report = new ModMismatchReport("ConanSandbox.log", null, "", [new("A", 2), new("B", 1), new("A again", 2), .. noId]);
        Check(report.GetModlist().SequenceEqual(["2", "1", "987654321"]), "The new mod list keeps the order, drops repeated ids and entries without id");
        Check(report.GetEntriesWithoutId().SequenceEqual(["My local mod"]), "Entries without id are named for the user");
        Check(ModMismatchLog.ParseList("").Count == 0 && ModMismatchLog.ParseList(" . ").Count == 0, "An empty list has no entries");

        string[] session =
        [
            "[2026.11.06-16.06.47:768][  0]LogNet: Browse: /Game/Maps/Startup?Name=Player",
            "[2026.11.06-16.08.51:459][981]LogGlobalStatus: UEngine::Browse Started Browse: \"54.38.92.192/Game/Maps/Startup?Name=Player?Password=\"",
            "[2026.11.06-16.08.51:459][981]LogNet: Browse: 54.38.92.192/Game/Maps/Startup?Name=Player?Password=",
            "[2026.11.06-16.08.52:113][996]LogNet: NMT_CloseReason: (Server Disconnect Reasons) 54.38.92.192:7777",
            CloseLine,
            BroadcastLine,
            FailureLine,
            "[2026.11.06-16.08.53:001][997]LogNet: Browse: /Game/Maps/MainMenu/MainMenuLevel",
        ];
        var found = ModMismatchLog.FindLast(session, "ConanSandbox.log");
        Check(found is { Server: "54.38.92.192:7777", Mods.Count: 5, LogFile: "ConanSandbox.log" },
            "The longest list of one refusal is kept with the server the client tried to join");
        Check(found?.TimeUtc == new DateTime(2026, 11, 6, 16, 8, 52, DateTimeKind.Utc) && found.TimeUtc?.Kind == DateTimeKind.Utc,
            "The time of the refusal is the UTC time stamp of the log line");

        var later = ModMismatchLog.FindLast([.. session, "[2026.11.06-17.00.00:000][  5]LogNet: Browse: 1.2.3.4:7779/Game/Maps/Startup?Name=Player", FailureLine], "x.log");
        Check(later is { Server: "1.2.3.4:7779", Mods.Count: 2 }, "A later join attempt replaces the earlier refusal, also with a shorter list");
        Check(ModMismatchLog.FindLast(["[2026.11.06-16.08.51:459][981]LogNet: Browse: 1.2.3.4/Game"], "x.log") is null, "A log without refusal has no report");

        var root = Path.Combine(Path.GetTempPath(), "TrebuchetMismatchChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check(ModMismatchLog.FindNewest(Path.Combine(root, "missing")) is null, "A missing log folder has no report");

            var backup = Path.Combine(root, "ConanSandbox-backup-2026.11.06-16.09.14.log");
            File.WriteAllLines(backup, session);
            File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddHours(-2));
            var current = Path.Combine(root, "ConanSandbox.log");
            File.WriteAllLines(current, ["[2026.11.07-10.00.00:000][  0]LogInit: Display: Starting Game."]);
            File.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddHours(-1));
            File.WriteAllText(Path.Combine(root, "notes.txt"), CloseLine);

            var fromBackup = ModMismatchLog.FindNewest(root);
            Check(fromBackup?.LogFile == backup && fromBackup.Mods.Count == 5,
                "Without a refusal in the current log the backup of the session before is read");
            Check(ModMismatchLog.FindNewest(root, maxFiles: 1) is null, "Only the newest logs are read");

            // The game keeps its log open for writing while it runs (it shares it for reading).
            using (var game = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(game))
            {
                writer.WriteLine("[2026.11.07-10.05.00:000][ 50]LogNet: Browse: 9.8.7.6:7777/Game/Maps/Startup?Name=Player");
                writer.WriteLine(FailureLine);
                writer.Flush();
                var fromCurrent = ModMismatchLog.FindNewest(root);
                Check(fromCurrent is { Server: "9.8.7.6:7777", Mods.Count: 2 } && fromCurrent.LogFile == current,
                    "The log the running game writes is read and its refusal comes first");
            }

            var logFolder = ModMismatchLog.GetClientLogFolder(root);
            Check(logFolder == Path.Combine(root, "ConanSandbox", "Saved", "Logs"), "The client logs are in ConanSandbox\\Saved\\Logs of the client folder");
        }
        finally
        {
            var resolvedRoot = Path.GetFullPath(root);
            if (!resolvedRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolvedRoot).StartsWith("TrebuchetMismatchChecks-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup path.");
            Directory.Delete(resolvedRoot, recursive: true);
        }

        return count;
    }
}
