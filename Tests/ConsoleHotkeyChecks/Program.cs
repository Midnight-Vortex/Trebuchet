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

foreach (var binding in new[] { "ConsoleKeys=Insert", "+ConsoleKeys=Insert", ".ConsoleKeys=Insert", " +consolekeys = \"insert\" " })
{
    var original = section + "\r\n" + binding + "\r\n[/Script/Engine.Console]\r\nHistoryBuffer=test\r\n";
    Check(ConsoleHotkeySettings.EnsureBinding(original, "Insert") == original, "Existing binding must remain byte-for-byte unchanged");
}

var mappings = ";METADATA=(Diff=true, UseCommands=true)\r\n" + section + "\r\nActionMappings=(Key=E)\r\nConsoleKeys=Tilde\r\n";
var history = "[/Script/Engine.Console]\r\nHistoryBuffer=test\r\n";
var updated = ConsoleHotkeySettings.EnsureBinding(mappings + history, "Insert");
Check(updated == mappings + "+ConsoleKeys=Insert\r\n" + history, "Preserve mappings, other keys, metadata and history");
Check(ConsoleHotkeySettings.EnsureBinding(updated, "Insert") == updated, "Repeated launches must not duplicate keys");
Check(ConsoleHotkeySettings.EnsureBinding("", "Insert") == section + "\n+ConsoleKeys=Insert\n", "Create missing section");
Check(ConsoleHotkeySettings.EnsureBinding("[Other]\nConsoleKeys=Insert", "Insert") == "[Other]\nConsoleKeys=Insert\n" + section + "\n+ConsoleKeys=Insert\n", "Ignore matching keys in other sections");
Check(ConsoleHotkeySettings.EnsureBinding(section + "\n;ConsoleKeys=Insert", "Insert").EndsWith("\n+ConsoleKeys=Insert\n"), "Ignore commented binding and handle missing final newline");
foreach (var removal in new[] { "-ConsoleKeys=Insert", "!ConsoleKeys=ClearArray", "ConsoleKeys=F1" })
{
    var original = section + "\n+ConsoleKeys=Insert\n" + removal + "\n";
    Check(ConsoleHotkeySettings.EnsureBinding(original, "Insert") == original + "+ConsoleKeys=Insert\n", "Respect later removal/reset");
}
var repeated = section + "\n+ConsoleKeys=Insert\n[Other]\nx=1\n" + section + "\n!ConsoleKeys=ClearArray\n";
Check(ConsoleHotkeySettings.EnsureBinding(repeated, "Insert") == repeated + "+ConsoleKeys=Insert\n", "Repair after last repeated section");
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

    foreach (var edition in new[] { GameEdition.Enhanced, GameEdition.Legacy, GameEdition.TestLive })
    {
        var clientPath = Path.Combine(testRoot, edition.ToString());
        var setup = new AppSetup(new Config { ClientPath = clientPath }, edition, false, false);
        var inputPath = Path.Combine(clientPath, string.Format(Constants.GetFileIniUser(edition), "Input"));
        await setup.WriteIni(new ClientProfile());
        Check(!File.Exists(inputPath), "Disabled option must not create Input.ini");
        await setup.WriteIni(profile);
        Check((await File.ReadAllTextAsync(inputPath)).Contains("+ConsoleKeys=F10"), "Use edition-specific Input.ini path");
        var before = await File.ReadAllBytesAsync(inputPath);
        await setup.WriteIni(new ClientProfile());
        Check((await File.ReadAllBytesAsync(inputPath)).SequenceEqual(before), "Disabling must preserve existing bindings");
    }

    if (args.Length > 0)
    {
        var sample = await File.ReadAllTextAsync(args[0]);
        Check(ConsoleHotkeySettings.EnsureBinding(sample, "Insert") == sample, "Supplied Input.ini must remain unchanged");
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
Console.WriteLine($"Passed {checks} console hotkey checks.");
