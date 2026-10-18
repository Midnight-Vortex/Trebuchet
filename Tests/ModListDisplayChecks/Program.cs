using System.Collections;
using Avalonia;
using System.Collections.ObjectModel;
using System.Text.Json;
using DynamicData.Binding;
using ReactiveUI.Builder;
using Trebuchet.ViewModels;
using Trebuchet.Services;
using TrebuchetLib;
using TrebuchetLib.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Runtime.CompilerServices;

RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
if (args.Contains("--console-checks"))
{
    AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
    var consoleChecks = ConsoleChecks.RunUi();
    SynchronizationContext.SetSynchronizationContext(null);
    consoleChecks += await ConsoleChecks.RunIo();
    Console.WriteLine($"Passed {consoleChecks} server console checks.");
    return;
}

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}

var day = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
var beta = new TestMod("Beta", "10", 200, day);
var alpha = new TestMod("alpha", "20", 100, day.AddDays(1));
var gamma = new TestMod("Gamma", "30", 300, null, "mods/decor.pak");
var tie = new TestMod("ALPHA", "40", 100, day.AddDays(1));
var source = new ObservableCollectionExtended<IModFile> { beta, alpha, gamma, tie };
var savedOrder = JsonSerializer.Serialize(source.Select(x => x.Export()));
var changes = 0;
source.CollectionChanged += (_, _) => changes++;
var view = new ModListDisplayViewModel(source);

void Order(params IModFile[] expected)
{
    Check(view.Items.SequenceEqual(expected), "Unexpected display order");
    Check(JsonSerializer.Serialize(source.Select(x => x.Export())) == savedOrder, "View must preserve serialized source order");
    Check(changes == 0, "View must not trigger persistence via source CollectionChanged");
}

Order(beta, alpha, gamma, tie);
Check(ReferenceEquals(view.Items, source) && view.CanReorder, "Original view supports existing drag reorder");
view.SortIndex = 1;
Order(beta, alpha, tie, gamma);
view.DirectionIndex = 1;
Order(alpha, tie, beta, gamma);
view.SortIndex = 2;
Order(gamma, beta, alpha, tie);
view.DirectionIndex = 0;
Order(alpha, tie, beta, gamma);
view.SortIndex = 3;
Order(alpha, tie, beta, gamma);
view.DirectionIndex = 1;
Order(gamma, beta, alpha, tie);
Check(!view.CanReorder && ((IList)view.Items).IsReadOnly, "Sorted view cannot change load order");
view.SortIndex = 0;
Order(beta, alpha, gamma, tie);
Check(!view.CanChooseDirection, "Load order ignores descending setting");

view.SearchPattern = "^alpha$|^10$";
Order(beta, alpha, tie);
Check(!view.CanReorder, "Searching alone disables drag reorder");
view.SortIndex = 2;
view.DirectionIndex = 0;
Order(alpha, tie, beta);
view.SearchPattern = "decor\\.pak$";
Order(gamma);
view.SearchPattern = "^20$";
Order(alpha);
view.SearchPattern = "no such mod";
Order();
Check(view.HasNoMatches && !view.HasSearchError, "No matches is distinct from an invalid regex");
view.SearchPattern = "[";
Order();
Check(view.HasSearchError && !view.HasNoMatches && !view.CanReorder, "Invalid regex shows an error and keeps source protected");
view.SearchPattern = "Beta";
Order(beta);
Check(!view.HasSearchError, "Correcting regex clears error");
view.ResetView.Execute().Subscribe();
Order(beta, alpha, gamma, tie);
Check(ReferenceEquals(view.Items, source) && view.CanReorder && view.SearchPattern == "" && view.DirectionIndex == 0,
    "Reset restores editable source and clears controls");
view.IsReadOnly = true;
Check(!view.CanReorder, "Read-only list cannot be reordered even after reset");
view.IsReadOnly = false;

view.SearchPattern = "^New";
var added = new TestMod("New mod", "50", 1, day);
source.Add(added);
Check(view.Items.SequenceEqual(new[] { added }), "Added mods appear in active search");
source[source.IndexOf(added)] = new TestMod("Renamed", "50", 1, day);
Check(!view.Items.Any(), "Metadata refresh reapplies active search");
source.Clear();
Check(!view.Items.Any() && view.HasNoMatches, "Profile switch clears old results");
source.Add(beta);
view.SearchPattern = "";
Check(view.Items.SequenceEqual(new[] { beta }), "New source appears after search reset");

source.Clear();
source.Add(new TestMod(new string('a', 30000) + "!", "60", 1, day));
view.SearchPattern = "^(a+)+$";
Check(view.HasSearchError && !view.Items.Any() && source.Count == 1, "Expensive regex stops safely without touching source");
view.ResetView.Execute().Subscribe();
Check(!view.HasSearchError && view.Items.Count() == 1, "Reset recovers from regex timeout");

// Operations is only an event publisher here: avoid starting its filesystem watcher.
var operations = (Operations)RuntimeHelpers.GetUninitializedObject(typeof(Operations));
var setup = new AppSetup(new Config(), GameEdition.Enhanced, false, false);
var model = new ModListViewModel(operations, NullLogger<ModListViewModel>.Instance,
    new ModFileFactory(setup, null!), null!, setup, new TestProgress(), null!);
var localPaths = new[] { Path.GetFullPath("first-test.pak"), Path.GetFullPath("second-test.pak") };
model.AddRange(localPaths);
var oldRemoveAction = model.List[0].Actions.Single(x => x.Icon == "mdi-delete").Action;
var setReadOnly = typeof(ModListViewModel).GetMethod("SetReadOnly", BindingFlags.Instance | BindingFlags.NonPublic)!;
await (Task)setReadOnly.Invoke(model, null)!;
Check(model.List.Select(x => x.Export()).SequenceEqual(localPaths), "Read-only transition preserves all entries and order");
Check(model.IsReadOnly && !model.Display.CanReorder, "Read-only transition disables reordering");
Check(model.List.All(x => !x.Actions.Single(a => a.Icon == "mdi-delete").Action.CanExecute(null)), "Read-only rows disable delete");
await model.RemoveModFile(model.List[0]);
Check(model.List.Count == 2, "Direct removal must respect read-only mode");
oldRemoveAction.Execute(null);
Check(model.List.Count == 2, "Previously captured delete action cannot remove from read-only list");

checks += await ProfileMenuChecks.Run();

// Sequence editor: every offered action type can be added; internal actions (restart countdown) are hidden.
{
    var sequenceEditor = new Trebuchet.ViewModels.Sequences.SequenceViewModel(new TrebuchetLib.Sequences.Sequence());
    var offered = sequenceEditor.ActionTypes.Select(x => x.Type).ToList();
    Check(offered.Contains(typeof(TrebuchetLib.Sequences.SequenceActionBroadcast)) && offered.All(x => x.IsPublic)
          && offered.All(x => !x.Name.Contains("Countdown")), "Sequence editor offers public editable actions only");
    foreach (var actionType in offered)
        sequenceEditor.AddAction.Execute(actionType).Subscribe();
    Check(sequenceEditor.Actions.Count == offered.Count, "Every offered sequence action can be added without error");
    Check(sequenceEditor.ActionTypes.All(x => x.Label != "INVALID" && x.Label != x.Type.Name), "Every sequence action has a translated label");
}

// 24/7 console buffer: bounded, keeps the newest text, appends without rebuilding the whole text each time.
{
    var writer = new Trebuchet.ConsoleWriter(500, 1000);
    var flushed = 0;
    writer.TextFlushed += (_, text) => flushed += text.Length;
    for (var i = 0; i < 300; i++) writer.Write($"line {i:D4} ");
    writer.Flush();
    Check(writer.Text.Length == 1000 && writer.Text.EndsWith("line 0299 ", StringComparison.Ordinal), "Console text keeps the newest characters only");
    Check(flushed == 300 * 10, "Every written character is flushed once to the view");
    writer.Write(new string('x', 1234));
    writer.Flush();
    Check(writer.Text.Length == 1000 && writer.Text.EndsWith("xxx", StringComparison.Ordinal), "Strings longer than the buffer are written in chunks");
    writer.Clear();
    Check(writer.Text.Length == 0, "Clear empties the console");
}

// Default notification texts follow the UI language, custom and empty texts stay.
{
    var previousCulture = Trebuchet.Assets.Resources.Culture;
    try
    {
        Trebuchet.Assets.Resources.Culture = System.Globalization.CultureInfo.GetCultureInfo("de");
        var config = new Config { NotificationServerOnline = "Mein eigener Text", NotificationServerStop = string.Empty };
        Check(Trebuchet.Services.LocalizedDefaults.LocalizeConfig(config), "Untouched English defaults are localized");
        Check(config.NotificationServerCrash == "Server {serverName} ist abgestürzt", "German crash notification");
        Check(config.NotificationModUpdateDetected.StartsWith("Mod-Updates gefunden: {modList}", StringComparison.Ordinal),
            "Mod update announcement to players is localized");
        Check(config.NotificationTimedRestart.Contains("{minutes} Min. neu", StringComparison.Ordinal)
              && config.NotificationTimedStop.Contains("{minutes} Min. heruntergefahren", StringComparison.Ordinal),
            "Countdown messages of a delayed stop/restart are localized");
        Check(config.NotificationServerOnline == "Mein eigener Text" && config.NotificationServerStop == string.Empty, "Custom and empty templates are kept");
        Trebuchet.Assets.Resources.Culture = System.Globalization.CultureInfo.GetCultureInfo("en");
        Check(Trebuchet.Services.LocalizedDefaults.LocalizeConfig(config) && config.NotificationServerCrash == Config.NotificationServerCrashDefault,
            "Switching back to English restores the English defaults");
        Trebuchet.Assets.Resources.Culture = System.Globalization.CultureInfo.GetCultureInfo("de");
        var profile = new ServerProfile();
        Check(Trebuchet.Services.LocalizedDefaults.LocalizeProfile(profile) && profile.MessageRestarted.StartsWith("Der Server wird neu gestartet", StringComparison.Ordinal)
              && profile.RestartWarnings[0].Message.StartsWith("Der Server startet in 5 Minuten", StringComparison.Ordinal), "Profile default messages are localized");
    }
    finally
    {
        Trebuchet.Assets.Resources.Culture = previousCulture;
    }
}

// Every English UI text has a German translation (the language setting must switch the whole UI).
{
    var manager = Trebuchet.Assets.Resources.ResourceManager;
    var neutral = manager.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true)!
        .Cast<System.Collections.DictionaryEntry>().Select(x => (string)x.Key).ToList();
    var german = manager.GetResourceSet(System.Globalization.CultureInfo.GetCultureInfo("de"), true, false);
    Check(german is not null, "German satellite resources are deployed");
    var germanKeys = german!.Cast<System.Collections.DictionaryEntry>().Select(x => (string)x.Key).ToHashSet();
    var missingGerman = neutral.Where(x => !germanKeys.Contains(x)).ToList();
    Check(missingGerman.Count == 0, "German translation is missing: " + string.Join(", ", missingGerman.Take(10)));
    Check(manager.GetString(nameof(Trebuchet.Assets.Resources.CatBackupAutomation), System.Globalization.CultureInfo.GetCultureInfo("de")) == "Backup und Automatisierung",
        "German texts are resolved for the German culture");
}
// Test buttons (e.g. RCon test message) show the result of their action, and the error instead of crashing.
{
    var working = new Trebuchet.ViewModels.SettingFields.ActionButtonField("Test", "mdi-send", () => Task.FromResult("sent"));
    working.Command.Execute().Subscribe();
    Check(working.Status == "sent", "Test button shows the result of its action");
    var failing = new Trebuchet.ViewModels.SettingFields.ActionButtonField("Test", "mdi-send", () => throw new InvalidOperationException("no rcon"));
    failing.Command.Execute().Subscribe();
    var canRunAgain = false;
    using (failing.Command.CanExecute.Subscribe(x => canRunAgain = x)) { }
    Check(failing.Status == "no rcon" && canRunAgain, "A failing test button shows the error and stays usable");
}

// Stop/restart delay dialogue: "now", the presets, minutes typed in, cancel.
{
    var dialogue = new Trebuchet.ViewModels.InnerContainer.ServerActionDelayDialogue("Stop", "When?", [5, 60]);
    Check(dialogue.Presets.Count == 3 && dialogue.Presets[0].IsNow
          && dialogue.Presets[2].Label == Trebuchet.ViewModels.InnerContainer.ServerActionDelayDialogue.FormatMinutes(60),
        "Delay dialogue offers now and the presets");
    dialogue.Presets[1].Command.Execute().Subscribe();
    Check(dialogue.Result == TimeSpan.FromMinutes(5), "A preset chooses its delay");
    var custom = new Trebuchet.ViewModels.InnerContainer.ServerActionDelayDialogue("Restart", "When?", [5]) { CustomMinutes = 42 };
    custom.ConfirmCustom.Execute().Subscribe();
    Check(custom.Result == TimeSpan.FromMinutes(42), "Minutes typed in choose the delay");
    var now = new Trebuchet.ViewModels.InnerContainer.ServerActionDelayDialogue("Restart", "When?", [5]);
    now.Presets[0].Command.Execute().Subscribe();
    Check(now.Result == TimeSpan.Zero, "Now chooses no delay");
    var cancelled = new Trebuchet.ViewModels.InnerContainer.ServerActionDelayDialogue("Restart", "When?", [5]);
    cancelled.CancelCommand.Execute().Subscribe();
    Check(cancelled.Result is null, "Cancel chooses nothing");
}

// Unreal log lines: only real verbosities are taken as level, "Join succeeded:" & co. stay in the message.
{
    Check(TrebuchetLib.LogReader.TryParseLine("[2026.10.18-14.56.39:205][162]LogNet: Join succeeded: Blackhearts [MVX]#24594", out var logSource, out var level, out var content)
          && logSource == "LogNet" && level == Microsoft.Extensions.Logging.LogLevel.Information && content.Trim() == "Join succeeded: Blackhearts [MVX]#24594",
        "Join line keeps its text");
    Check(TrebuchetLib.LogReader.TryParseLine("[2026.10.18-14.55.17:520][106]LogNet: Player disconnected: Unknown", out _, out _, out content)
          && content.Trim() == "Player disconnected: Unknown", "Disconnect line keeps its text");
    Check(TrebuchetLib.LogReader.TryParseLine("[2026.10.18-14.53.32:000][  1]LogTemp: Error: Character 'X' is in an unsafe movement", out logSource, out level, out content)
          && logSource == "LogTemp" && level == Microsoft.Extensions.Logging.LogLevel.Error && content.Trim().StartsWith("Character", StringComparison.Ordinal),
        "Verbosity is still taken as level");
    Check(TrebuchetLib.LogReader.TryParseLine("[2026.10.18-14.56.39:204][162]Login: Display: [FunCombat_PlayerController_C] User logged in.", out logSource, out _, out content)
          && logSource == "Login" && content.Trim().StartsWith("[FunCombat", StringComparison.Ordinal), "Display verbosity is removed from the message");
    var listed = MixedConsoleViewModel.ParseListPlayers(
        "Idx | Char name |             Player name |     User ID |       Platform ID | Platform Name\n  0 |      test | Blackhearts [MVX]#24594 | A-2XNIEBERC | 76561190000000000 |         STEAM\n");
    Check(listed.SequenceEqual(["Blackhearts [MVX]#24594"]), "RCon ListPlayers table gives the player names");
    Check(MixedConsoleViewModel.ParseListPlayers("Idx | Char name | Player name | User ID | Platform ID | Platform Name\n").Count == 0, "Empty ListPlayers gives no players");
}

// Example server save: created once in the UI language, never overwritten, never picked as default save.
{
    var previousCulture = Trebuchet.Assets.Resources.Culture;
    var root = Path.Combine(Path.GetTempPath(), "TrebuchetExampleChecks-" + Guid.NewGuid().ToString("N"));
    try
    {
        Trebuchet.Assets.Resources.Culture = System.Globalization.CultureInfo.GetCultureInfo("de");
        var serverFiles = new ExampleServerFiles(root);
        var created = Trebuchet.Services.ExampleServerProfile.CreateIfMissing(serverFiles);
        Check(created?.Name == "Beispiel-Alle-Optionen", "German example save is created");
        serverFiles.Cache.Clear();
        var example = serverFiles.Get(created!);
        Check(example.StartingSequence.Actions.Count == 4
              && example.StartingSequence.Actions[2] is TrebuchetLib.Sequences.SequenceActionBroadcast { SendRCon: true, SendDiscord: false }
              && example.StartingSequence.Actions[3] is TrebuchetLib.Sequences.SequenceActionDiscordWebHook hook && hook.Message.Contains("\nSpieler: {players}"),
            "Example starting sequence greets in game and announces on Discord");
        Check(example.RestartWarnings.Count == 4 && example.RestartWarnings[0].Before == TimeSpan.FromMinutes(15) && example.EnableRCon, "Example warnings reach players over RCon");
        Check(example.LogFilters.Contains("LogOnlineSubsystemFls=Fatal") && File.Exists(example.ScriptPath)
              && File.Exists(Path.Combine(root, created!.Name, "LIESMICH.txt")), "Example save brings its script, readme and log filters");
        Check(!example.GetDiscordWebHooks().Any(x => DiscordWebHooks.DiscordWebHooksRegex().IsMatch(x)), "Example webhook placeholder is never called");
        Check(Trebuchet.Services.ExampleServerProfile.CreateIfMissing(serverFiles) is null, "Existing example save is never overwritten");
        Trebuchet.Assets.Resources.Culture = System.Globalization.CultureInfo.GetCultureInfo("en");
        Check(Trebuchet.Services.ExampleServerProfile.CreateIfMissing(serverFiles) is null, "An example save in another language counts as existing");
        Check(serverFiles.GetDefault().Name == "Default", "The example save is never picked as default save");
        foreach (var culture in Trebuchet.Utils.AppConstants.UICultureList)
            Check(Constants.ExampleServerProfileNames.Contains(Trebuchet.Assets.Resources.ResourceManager.GetString(nameof(Trebuchet.Assets.Resources.ExampleProfileName),
                System.Globalization.CultureInfo.GetCultureInfo(culture))), "Example save name of every UI language is excluded from the default save");
    }
    finally
    {
        Trebuchet.Assets.Resources.Culture = previousCulture;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

// The voice error flood filter is in every server save: added once when missing, a level set by the user stays.
{
    var filters = new List<string> { "LogNet=Error" };
    Check(LogFilterRules.EnsureRequiredServerFilters(filters) && filters.Contains("LogOnlineSubsystemFls=Fatal"), "Missing voice log filter is added");
    Check(!LogFilterRules.EnsureRequiredServerFilters(filters) && filters.Count == 2, "Voice log filter is added only once");
    var custom = new List<string> { "LogOnlineSubsystemFls = Error" };
    Check(!LogFilterRules.EnsureRequiredServerFilters(custom) && custom.Count == 1, "A voice log level set by the user is kept");
    Check(ServerProfile.LogFiltersDefault.Contains("LogOnlineSubsystemFls=Fatal") && new ServerProfile().LogFilters.Contains("LogOnlineSubsystemFls=Fatal"),
        "New server saves start with the voice log filter");
}
Console.WriteLine($"Passed {checks} mod list and profile menu checks.");

sealed class ExampleServerFiles(string root) : IAppFileHandler<ServerProfile, ServerProfileRef>
{
    public bool UseSubFolders => true;
    public Dictionary<ServerProfileRef, ServerProfile> Cache { get; } = [];
    public string GetBaseFolder() => root;
    public ServerProfileRef Ref(string name) => new(name, this);
}

sealed class TestProgress : IProgressCallback<DepotDownloader.Progress>
{
    public event EventHandler<DepotDownloader.Progress>? ProgressChanged;
    public void Report(DepotDownloader.Progress value) => ProgressChanged?.Invoke(this, value);
    public Task<DepotDownloader.Progress> GetProgressAsync() => Task.FromResult(default(DepotDownloader.Progress)!);
}

sealed class TestMod(string title, string id, long size, DateTime? updated, string path = "") : IModFile
{
    public string Title => title;
    public string FilePath => path;
    public long FileSize => size;
    public DateTime? UpdatedAtUtc => updated;
    public string LastUpdate => string.Empty;
    public string IconToolTip => string.Empty;
    public ObservableCollection<string> StatusClasses { get; } = [];
    public ObservableCollection<string> IconClasses { get; } = [];
    public ObservableCollection<ModFileAction> Actions { get; } = [];
    public ModProgressViewModel Progress { get; } = new();
    public string Export() => id;
}
