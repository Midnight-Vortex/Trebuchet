using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using DynamicData.Binding;
using ReactiveUI.Builder;
using Trebuchet.ViewModels;

RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

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

Console.WriteLine($"Passed {checks} mod list display checks.");

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
