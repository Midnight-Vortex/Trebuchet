using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Trebuchet.ViewModels;
using Trebuchet.ViewModels.InnerContainer;
using TrebuchetLib;
using TrebuchetLib.Services;

internal static class ProfileMenuChecks
{
    public static async Task<int> Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        var root = Path.Combine(Path.GetTempPath(), "TrebuchetProfileChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new TestFiles(root);
            var original = files.Ref("Default");
            files.Create(original).SyncURL = "https://example.invalid/mods.json";
            files.Get(original).SaveFile();
            var box = new DialogueBox();
            using (var menu = new FileMenuViewModel<SyncProfile, TestRef>("Sync", files, box, NullLogger.Instance, allowEmpty: true))
            {
                Check(menu.Selected?.Equals(original) == true, "Existing Default remains selected");
                Check(menu.List.Single().DisplayButton, "Selected profile exposes its actions without hover");
                var cancelled = menu.DeleteSelected.Execute().ToTask();
                Check(box.Popup is OnBoardingConfirmation, "Deletion requires confirmation");
                await ((OnBoardingConfirmation)box.Popup!).CancelCommand.Execute().ToTask();
                await cancelled.WaitAsync(TimeSpan.FromSeconds(3));
                Check(files.Exists(original) && menu.Selected?.Equals(original) == true, "Cancel preserves profile and selection");

                var selectionCleared = false;
                menu.FileSelected += (_, selected) => { selectionCleared = selected is null; return Task.CompletedTask; };
                var deletion = menu.DeleteSelected.Execute().ToTask();
                await ((OnBoardingConfirmation)box.Popup!).ConfirmCommand.Execute().ToTask();
                await deletion.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!files.Exists(original) && !files.Cache.ContainsKey(original), "Delete removes file and cached profile");
                Check(menu.Selected is null && menu.List.Count == 0 && selectionCleared, "Deleting last Sync profile clears selection and list");
                Check(!menu.IsLoading, "Deletion releases loading state");
            }
            using (var reopened = new FileMenuViewModel<SyncProfile, TestRef>("Sync", files, box, NullLogger.Instance, allowEmpty: true))
                Check(reopened.Selected is null && !files.GetList().Any(), "Reopening empty Sync does not recreate Default");

            var first = files.Ref("First");
            var second = files.Ref("Second");
            files.Create(first);
            files.Create(second);
            using (var switching = new FileMenuViewModel<SyncProfile, TestRef>("Sync", files, box, NullLogger.Instance, allowEmpty: true))
            {
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var seen = new List<TestRef?>();
                switching.FileSelected += (_, selected) =>
                {
                    seen.Add(selected);
                    return Equals(selected, second) ? pending.Task : Task.CompletedTask;
                };
                try
                {
                    switching.Selected = second;
                    switching.Selected = first;
                    Check(seen.Contains(first), "Selection changes must not be dropped during a pending profile load");
                    switching.Selected = null;
                    Check(seen.Contains(null), "Clearing selection must reach panel during a pending profile load");
                }
                finally { pending.TrySetResult(); }
            }
            using (var menu = new FileMenuViewModel<SyncProfile, TestRef>("Sync", files, box, NullLogger.Instance, allowEmpty: true))
            {
                menu.Selected = second;
                var otherDeletion = menu.List.Single(x => x.Reference.Equals(first)).Delete.Execute().ToTask();
                await ((OnBoardingConfirmation)box.Popup!).ConfirmCommand.Execute().ToTask();
                await otherDeletion.WaitAsync(TimeSpan.FromSeconds(3));
                Check(menu.Selected.Equals(second) && files.Exists(second), "Deleting another profile preserves active selection");
                Check(menu.List.Count == 1 && !files.Exists(first), "Delete updates menu immediately");

                using (var locked = new FileStream(files.GetPath(second), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var failedDeletion = menu.DeleteSelected.Execute().ToTask();
                    await ((OnBoardingConfirmation)box.Popup!).ConfirmCommand.Execute().ToTask();
                    for (var i = 0; i < 100 && !box.Active; i++) await Task.Delay(10);
                    Check(box.Active && box.Popup is not OnBoardingConfirmation, "Failed deletion displays an error");
                    box.Close();
                    await failedDeletion.WaitAsync(TimeSpan.FromSeconds(3));
                    Check(menu.Selected.Equals(second) && files.Cache.ContainsKey(second) && !menu.IsLoading,
                        "Failed deletion preserves selection/cache and releases loading state");
                }
            }
            files.Delete(second);
            using (var required = new FileMenuViewModel<SyncProfile, TestRef>("Required", files, box, NullLogger.Instance))
            {
                Check(required.Selected?.Name == "Default", "Other profile menus still create a required default");
                var notified = false;
                required.FileSelected += (_, _) => { notified = true; return Task.CompletedTask; };
                var deletion = required.DeleteSelected.Execute().ToTask();
                await ((OnBoardingConfirmation)box.Popup!).ConfirmCommand.Execute().ToTask();
                await deletion.WaitAsync(TimeSpan.FromSeconds(3));
                Check(required.Selected?.Name == "Default" && notified, "Recreated required default refreshes panel even with same name");
            }
        }
        finally
        {
            var fullPath = Path.GetFullPath(root);
            if (!fullPath.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullPath).StartsWith("TrebuchetProfileChecks-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid test cleanup path");
            Directory.Delete(fullPath, recursive: true);
        }
        return checks;
    }

    private sealed class TestFiles(string root) : IAppFileHandler<SyncProfile, TestRef>
    {
        public bool UseSubFolders => false;
        public Dictionary<TestRef, SyncProfile> Cache { get; } = [];
        public string GetBaseFolder() => root;
        public TestRef Ref(string name) => new(name, this);
    }

    private sealed record TestRef(string Name, IAppFileHandler<SyncProfile, TestRef> Handler) : IPRef<SyncProfile, TestRef>
    {
        public Uri Uri => new("trebuchet://sync/" + Uri.EscapeDataString(Name));
        public override string ToString() => Name;
    }
}
