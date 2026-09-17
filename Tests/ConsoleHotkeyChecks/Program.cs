using System.Text;
using System.Text.Json;
using TrebuchetLib;
using TrebuchetLib.Services;
using TrebuchetLib.YuuIni;

const string section = "[/Script/Engine.InputSettings]";
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}

var command = Boulder.Commands.RootCommandFactory.Create();
foreach (var edition in Enum.GetValues<GameEdition>())
{
    var editionArgs = Constants.GetCliArg(edition).Split(' ');
    Check(command.Parse(["lamb", "client", ..editionArgs]).Errors.Count == 0,
        "Boulder accepts edition flags after nested command");
    Check(command.Parse([..editionArgs, "lamb", "client"]).Errors.Count == 0,
        "Boulder accepts edition flags before command");
    Check(Constants.ParseEditionArgs(Constants.GetCliArg(edition).Split(' ')) == edition,
        "Edition survives restart/autostart command line round trip");
    var setup = new AppSetup(new Config(), edition, false, false);
    Check(setup.IsEnhanced == (edition is GameEdition.Enhanced or GameEdition.EnhancedPtc), "Enhanced engine selection");
    Check(setup.IsPtc == (edition == GameEdition.EnhancedPtc), "Only Enhanced has a PTC channel");
    var launchArgs = new ClientProfile().GetClientArgs("mods with spaces.txt", true, edition);
    Check(launchArgs.Contains(Constants.GameArgsExt) == (edition == GameEdition.EnhancedPtc), "Only Enhanced PTC uses external service argument");
    Check(launchArgs.Contains("-modlist=\"mods with spaces.txt\"") && launchArgs.Contains(Constants.GameArgsContinueSession), "Keep mod list quoting and auto-connect");
}
Check(Constants.ParseEditionArgs(["--ptc"]) == GameEdition.EnhancedPtc, "PTC alone selects Enhanced PTC");
Check(Constants.ParseEditionArgs(["--ptc", "--live"]) is null, "Conflicting edition arguments do not select a build");
Check(command.Parse(["lamb", "client", "--testlive"]).Errors.Count > 0, "Removed CLI alias is rejected");
Check(!Enum.IsDefined(typeof(GameEdition), 2) && (int)GameEdition.EnhancedPtc == 3, "Removed enum value is not reused");
Check(Constants.ParseEditionArgs([]) is null, "No edition arguments retains selector");
Check(command.Parse(["lamb", "client", "--live", "--ptc"]).Errors.Count > 0,
    "Boulder rejects incompatible channel options");
Check(command.Parse(["lamb", "client", "--enhanced", "--ptc", "--experiment"]).Errors.Count == 0,
    "Boulder accepts Enhanced PTC and experiment options");
Check(command.Parse(["lamb", "client", "--enhanced", "--ptc", "--unknown-option"]).Errors.Count == 1,
    "Boulder still rejects unknown options without rejecting valid edition flags");
Check(Enum.GetValues<GameEdition>().Select(Constants.GetVersionFolder).Distinct().Count() == 3, "Exactly three editions have isolated profile folders");
Check(Constants.GetFileIniUser(GameEdition.EnhancedPtc) == Constants.FileIniUserEnhanced, "Enhanced PTC uses UE5 INI directory");
Check(Constants.GetClientBin(GameEdition.EnhancedPtc) == Constants.FileClientEnhancedBin, "Enhanced PTC uses UE5 executable");
Check(!Constants.IsWorkshopModCompatible(GameEdition.EnhancedPtc, ["legacy"], Constants.AppIDLiveClient), "Enhanced PTC rejects legacy mods");
Check(Constants.IsWorkshopModCompatible(GameEdition.EnhancedPtc, ["Enhanced"], Constants.AppIDLiveClient), "Enhanced PTC accepts Enhanced workshop mods");

foreach (var binding in new[] { "ConsoleKeys=Insert", "+ConsoleKeys=Insert", ".ConsoleKeys=Insert", " +consolekeys = \"insert\" " })
{
    var original = section + "\r\n" + binding + "\r\n[/Script/Engine.Console]\r\nHistoryBuffer=test\r\n";
    Check(ConsoleHotkeySettings.EnsureBinding(original, "Insert") == original.Replace("+consolekeys", "consolekeys").Replace("+ConsoleKeys", "ConsoleKeys").Replace(".ConsoleKeys", "ConsoleKeys"), "Normalize existing binding without changing surrounding content");
    var normalized = ConsoleHotkeySettings.EnsureBinding(original, "Insert");
    Check(ConsoleHotkeySettings.EnsureBinding(normalized, "Insert") == normalized, "Converted bindings stay unchanged on subsequent launches");
}
var otherKeys = section + "\n+ConsoleKeys=F1\n+ConsoleKeys=Insert\n[Other]\n+ConsoleKeys=Insert\n";
Check(ConsoleHotkeySettings.EnsureBinding(otherKeys, "Insert") == section + "\n+ConsoleKeys=F1\nConsoleKeys=Insert\n[Other]\n+ConsoleKeys=Insert\n", "Only normalize the selected hotkey in InputSettings");

var mappings = ";METADATA=(Diff=true, UseCommands=true)\r\n" + section + "\r\nActionMappings=(Key=E)\r\nConsoleKeys=Tilde\r\n";
var history = "[/Script/Engine.Console]\r\nHistoryBuffer=test\r\n";
var updated = ConsoleHotkeySettings.EnsureBinding(mappings + history, "Insert");
Check(updated == mappings + "ConsoleKeys=Insert\r\n" + history, "Preserve mappings, other keys, metadata and history");
Check(ConsoleHotkeySettings.EnsureBinding(updated, "Insert") == updated, "Repeated launches must not duplicate keys");
Check(ConsoleHotkeySettings.EnsureBinding("", "Insert") == section + "\nConsoleKeys=Insert\n", "Create missing section");
Check(ConsoleHotkeySettings.EnsureBinding("[Other]\nConsoleKeys=Insert", "Insert") == "[Other]\nConsoleKeys=Insert\n" + section + "\nConsoleKeys=Insert\n", "Ignore matching keys in other sections");
Check(ConsoleHotkeySettings.EnsureBinding(section + "\n;ConsoleKeys=Insert", "Insert").EndsWith("\nConsoleKeys=Insert\n"), "Ignore commented binding and handle missing final newline");
foreach (var removal in new[] { "-ConsoleKeys=Insert", "!ConsoleKeys=ClearArray", "ConsoleKeys=F1" })
{
    var original = section + "\n+ConsoleKeys=Insert\n" + removal + "\n";
    Check(ConsoleHotkeySettings.EnsureBinding(original, "Insert") == original.Replace("+ConsoleKeys", "ConsoleKeys") + "ConsoleKeys=Insert\n", "Respect later removal/reset");
}
var repeated = section + "\n+ConsoleKeys=Insert\n[Other]\nx=1\n" + section + "\n!ConsoleKeys=ClearArray\n";
Check(ConsoleHotkeySettings.EnsureBinding(repeated, "Insert") == repeated.Replace("+ConsoleKeys", "ConsoleKeys") + "ConsoleKeys=Insert\n", "Repair after last repeated section");
try
{
    ConsoleHotkeySettings.EnsureBinding("", "Insert\nInjected=1");
    throw new Exception("Invalid key was accepted");
}
catch (ArgumentException) { checks++; }

var oldProfile = JsonSerializer.Deserialize<ClientProfile>("{}")!;
Check(!oldProfile.EnableConsoleOnHotkey && oldProfile.ConsoleHotkey == "Insert", "Old profiles receive safe defaults");
var profile = JsonSerializer.Deserialize<ClientProfile>(JsonSerializer.Serialize(new ClientProfile { EnableConsoleOnHotkey = true, ConsoleHotkey = "F10" }))!;
Check(profile.EnableConsoleOnHotkey && profile.ConsoleHotkey == "F10", "Persist option and key");

var testRoot = Path.Combine(Path.GetTempPath(), "TrebuchetConsoleChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
try
{
    foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode })
    {
        var path = Path.Combine(testRoot, "Input.ini");
        await File.WriteAllTextAsync(path, mappings + history, encoding);
        await ConsoleHotkeySettings.EnsureFile(path, "Insert");
        var expected = encoding.GetPreamble().Concat(encoding.GetBytes(updated));
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(expected), "Preserve encoding and original content");
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await ConsoleHotkeySettings.EnsureFile(path, "Insert");
        Check(File.GetLastWriteTimeUtc(path).Year == 2020, "Do not rewrite a file with existing binding");
    }

    foreach (var edition in Enum.GetValues<GameEdition>())
    {
        var clientPath = Path.Combine(testRoot, edition.ToString());
        var setup = new AppSetup(new Config { ClientPath = clientPath }, edition, false, false);
        var inputPath = Path.Combine(clientPath, string.Format(Constants.GetFileIniUser(edition), "Input"));
        await setup.WriteIni(new ClientProfile());
        Check(!File.Exists(inputPath), "Disabled option must not create Input.ini");
        await setup.WriteIni(profile);
        Check((await File.ReadAllTextAsync(inputPath)).Split('\n').Any(line => line.Trim() == "ConsoleKeys=F10"), "Use edition-specific Input.ini path");
        var before = await File.ReadAllBytesAsync(inputPath);
        await setup.WriteIni(new ClientProfile());
        Check((await File.ReadAllBytesAsync(inputPath)).SequenceEqual(before), "Disabling must preserve existing bindings");
    }

    if (args.Length > 0)
    {
        var sample = await File.ReadAllTextAsync(args[0]);
        Check(ConsoleHotkeySettings.EnsureBinding(sample, "Insert") == sample.Replace("+ConsoleKeys=Insert", "ConsoleKeys=Insert"), "Supplied Input.ini changes only the old console operator");
    }
}
finally
{
    var resolvedRoot = Path.GetFullPath(testRoot);
    var tempFolder = Path.GetFullPath(Path.GetTempPath());
    if (!resolvedRoot.StartsWith(tempFolder, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(resolvedRoot).StartsWith("TrebuchetConsoleChecks-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected test cleanup path.");
    Directory.Delete(resolvedRoot, recursive: true);
}
checks += await DirectoryCopyChecks.Run();
Console.WriteLine($"Passed {checks} console hotkey and background copy checks.");
