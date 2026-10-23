using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Trebuchet.Services;
using Trebuchet.ViewModels.DiscordBot;
using Trebuchet.ViewModels.SettingFields;
using TrebuchetLib.DiscordBot;

/// <summary>
/// The logic behind the "Discord Bot" panel, without Avalonia: nothing invalid is ever saved, IDs are checked, the
/// super admin and the admin channel follow their rules, the token never shows up, every text is translated, the bot
/// is started and stopped safely and its launcher work runs on the UI thread without deadlocks.
/// </summary>
public static class DiscordBotPanelChecks
{
    // Made up, put together at run time so secret scanners do not take the source for a real token.
    private static readonly string SampleToken = string.Join(".", "MTIzNDU2Nzg5MDEyMzQ1Njc4", "GabcDE", "abcdefghijklmnopqrstuvwxyz0123456789ABCD");
    private const ulong Guild = 100000000000000001;
    private const ulong Admin = 200000000000000001;
    private const ulong User = 200000000000000002;
    private const ulong Role = 300000000000000001;
    private const ulong Channel = 400000000000000001;

    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Discord bot panel: " + message);
            checks++;
        }

        var root = Path.Combine(Path.GetTempPath(), "TrebuchetDiscordPanelChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousCulture = Trebuchet.Assets.Resources.Culture;
        try
        {
            Trebuchet.Assets.Resources.Culture = CultureInfo.GetCultureInfo("en");
            EditorChecks(root, Check);
            LimitChecks(Check);
            SettingsFileChecks(root, Check);
            TextChecks(Check);
            ListChecks(Check);
            SecretChecks(root, Check);
            HostChecks(root, Check);
            DispatcherChecks(Check);
        }
        finally
        {
            Trebuchet.Assets.Resources.Culture = previousCulture;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return checks;
    }

    private static DiscordBotConfig ValidConfig(string path)
    {
        var config = DiscordBotConfig.CreateConfig(path);
        config.GuildId = Guild;
        config.SuperAdminUserId = Admin;
        config.AllowedUserIds = [User];
        config.AllowedRoleIds = [Role];
        return config;
    }

    private static string Json(DiscordBotConfig config) => JsonSerializer.Serialize(config, DiscordBotConfig._jsonOptions);

    private static void EditorChecks(string root, Action<bool, string> check)
    {
        // A new installation: nothing set, nothing can be saved, and nothing is written.
        var freshPath = Path.Combine(root, "fresh.json");
        var fresh = DiscordBotConfig.CreateConfig(freshPath);
        var editor = new DiscordBotSettingsEditor();
        editor.Load(fresh);
        check(!editor.IsDirty, "Freshly loaded settings have no edits");
        var kinds = editor.Validate(fresh).Issues.Select(x => x.Kind).ToList();
        check(kinds.Contains(BotConfigIssueKind.GuildMissing) && kinds.Contains(BotConfigIssueKind.SuperAdminMissing)
              && kinds.Contains(BotConfigIssueKind.NoRoles), "Missing server, super admin and roles are reported");
        editor.AutoStart = true;
        check(editor.IsDirty, "An edit is visible as unsaved");
        check(editor.Save(fresh).Count > 0 && !File.Exists(freshPath) && !fresh.AutoStart,
            "Incomplete settings are not saved, not even the parts that are fine");

        // Filled in: saved, read back, clean again.
        check(editor.SetGuildIdText(" " + Guild + " "), "A server ID is taken");
        check(editor.SetSuperAdmin(Admin), "A super admin is set");
        check(editor.TryAddRole(Role.ToString()) == BotIdInputResult.Added, "A role is added");
        check(editor.Validate(fresh).IsValid, "Complete settings are valid");
        check(editor.Save(fresh).Count == 0 && File.Exists(freshPath) && !editor.IsDirty, "Valid settings are saved and the edits are clean");
        var reread = DiscordBotConfig.LoadConfig(freshPath);
        check(reread.GuildId == Guild && reread.SuperAdminUserId == Admin && reread.AllowedRoleIds.SequenceEqual([Role]) && reread.AutoStart,
            "The saved file holds the edits");
        check(BotPolicy.Create(reread).IsValid, "What was saved is a valid policy");

        // IDs: digits only, 17 to 20 of them.
        var path = Path.Combine(root, "ids.json");
        var config = ValidConfig(path);
        config.SaveFile();
        editor.Load(config);
        foreach (var bad in new[] { "", "   ", "123", "-100000000000000001", "+100000000000000001", "1000000000000000001x",
                     "100000000000000000000", "１００００００００００００００００１", "100000000000000001 2", "abc" })
            check(editor.TryAddUser(bad) == BotIdInputResult.Invalid, $"'{bad}' is not a user ID");
        check(editor.TryAddUser("10000000000000000") == BotIdInputResult.Added, "17 digits are an ID");
        check(editor.TryAddUser("18446744073709551615") == BotIdInputResult.Added, "20 digits are an ID");
        check(editor.TryAddUser(User.ToString()) == BotIdInputResult.Duplicate, "A user is only once on the list");
        check(editor.TryAddUser(Admin.ToString()) == BotIdInputResult.Duplicate, "The super admin is on the list already");
        check(editor.UserIds.Count == 3, "Only valid IDs reached the list");

        // The super admin can not be removed, only replaced.
        check(!editor.RemoveUser(Admin) && editor.SuperAdminUserId == Admin, "The super admin can not be removed");
        check(editor.RemoveUser(User) && !editor.UserIds.Contains(User), "Another user can be removed");
        check(editor.CheckSuperAdmin(Admin.ToString(), out _) == BotIdInputResult.Duplicate, "The same super admin is no change");
        check(editor.CheckSuperAdmin("12", out _) == BotIdInputResult.Invalid && !editor.SetSuperAdmin(12),
            "A super admin needs a valid ID");
        check(editor.CheckSuperAdmin(User.ToString(), out var newAdmin) == BotIdInputResult.Added && newAdmin == User
              && editor.SuperAdminUserId == Admin, "Checking a new super admin does not set it yet (the panel asks first)");

        // A pasted token is recognized everywhere and never kept.
        check(editor.TryAddUser(SampleToken) == BotIdInputResult.LooksLikeToken
              && editor.TryAddRole("Bot " + SampleToken) == BotIdInputResult.LooksLikeToken
              && editor.CheckSuperAdmin(SampleToken, out _) == BotIdInputResult.LooksLikeToken, "A pasted token is recognized");
        check(!editor.SetGuildIdText(SampleToken) && editor.GuildIdText.Length == 0
              && !editor.SetAuditChannelIdText(SampleToken) && editor.AuditChannelIdText.Length == 0, "A token typed into an ID field is not kept");
        var tokenIssues = editor.Validate(config).Issues;
        check(tokenIssues.All(x => !x.Detail.Contains(SampleToken) && !DiscordBotTexts.Issue(x).Contains(SampleToken)),
            "No issue repeats a token");

        // @everyone is refused, also when the server ID comes later.
        editor.Load(config);
        check(editor.TryAddRole(Guild.ToString()) == BotIdInputResult.EveryoneRole && !editor.RoleIds.Contains(Guild), "@everyone is refused");
        var other = new DiscordBotSettingsEditor();
        var everyoneConfig = DiscordBotConfig.CreateConfig(Path.Combine(root, "everyone.json"));
        other.Load(everyoneConfig);
        other.SetSuperAdmin(Admin);
        check(other.TryAddRole(Guild.ToString()) == BotIdInputResult.Added, "Without a server ID the role can not be recognized yet");
        other.SetGuildIdText(Guild.ToString());
        var everyone = other.Save(everyoneConfig);
        check(everyone.Any(x => x.Kind == BotConfigIssueKind.EveryoneRole) && everyoneConfig.GuildId == 0
              && !File.Exists(everyoneConfig.FilePath), "Saving refuses @everyone that came in before the server ID");

        // Text that is no ID: named by its field, not repeated, not reported twice.
        editor.Load(config);
        editor.SetGuildIdText("abc");
        editor.SetAuditChannelIdText("12");
        var issues = editor.Validate(config).Issues;
        check(issues.Count(x => x.Kind == BotConfigIssueKind.InvalidId) == 2 && issues.All(x => x.Kind != BotConfigIssueKind.GuildMissing),
            "An unreadable server ID is reported once, as not valid");
        check(issues.All(x => !DiscordBotTexts.Issue(x).Contains("abc")), "Typed text is not repeated in the issues");
        check(issues.Any(x => DiscordBotTexts.Issue(x).Contains(Trebuchet.Assets.Resources.DiscordBotGuild))
              && issues.Any(x => DiscordBotTexts.Issue(x).Contains(Trebuchet.Assets.Resources.DiscordBotAuditChannel)),
            "Issues name the field as the panel shows it");
        editor.SetAuditChannelIdText("");
        editor.SetGuildIdText(Guild.ToString());
        check(editor.Validate(config).IsValid, "An empty audit channel means none");

        // Nothing invalid is ever saved: neither the object in memory nor the file changes.
        var before = Json(config);
        var fileBefore = File.ReadAllText(path);
        editor.CooldownSeconds = 0;
        editor.SetCommandEnabled(BotCommand.RestartNow, true);
        check(editor.Save(config).Any(x => x.Kind == BotConfigIssueKind.InvalidLimit), "A limit out of range is refused");
        check(Json(config) == before && File.ReadAllText(path) == fileBefore && !config.IsEnabled(BotCommand.RestartNow),
            "A refused save changes neither the settings nor the file");
        check(editor.IsDirty, "Refused edits stay as unsaved edits");
        editor.Load(config);
        check(!editor.IsDirty && editor.CooldownSeconds == config.CooldownSeconds, "Revert drops the edits");

        // Commands: switched per command, unknown names from the file are dropped on save.
        config.EnabledCommands.Add("Kill");
        editor.Load(config);
        check(!editor.Validate(config).Issues.Any(), "Unknown command names of the file are not taken over");
        editor.SetCommandEnabled(BotCommand.Stop, true);
        editor.SetCommandEnabled(BotCommand.Status, false);
        check(editor.Save(config).Count == 0 && config.IsEnabled(BotCommand.Stop) && !config.IsEnabled(BotCommand.Status)
              && !config.EnabledCommands.Contains("Kill"), "Commands are switched on and off by saving");

        // The admin channel: Discord narrows it, only the panel widens it, the panel never undoes a channel set meanwhile.
        config.SetAdminChannel(Channel);
        config.StatusMessageIds[0] = 900000000000000001;
        config.SaveFile();
        editor.Load(config);
        editor.LockoutAttempts = 7;
        config.SetAdminChannel(Channel + 1);
        config.StatusMessageIds[1] = 900000000000000002;
        check(editor.Save(config).Count == 0 && config.AllowedChannelIds.SequenceEqual([Channel + 1]) && config.LockoutAttempts == 7
              && config.StatusMessageIds.Count == 1, "Saving keeps the admin channel and status messages the bot set meanwhile");
        editor.RefreshAdminChannels(config);
        check(editor.AdminChannelIds.SequenceEqual([Channel + 1]), "The panel shows the channel the bot set");
        editor.ClearAdminChannel();
        check(editor.ClearAdminChannelPending && editor.IsDirty && config.AllowedChannelIds.Count == 1,
            "Allowing all channels waits for the save");
        check(editor.Save(config).Count == 0 && config.AllowedChannelIds.Count == 0 && !editor.ClearAdminChannelPending,
            "Saving allows all channels again");

        // Postponement choices: whole minutes only, never longer than the longest postponement.
        editor.Load(config);
        editor.PostponeChoicesText = "15, abc";
        var choiceIssues = editor.Validate(config).Issues;
        check(choiceIssues.Count == 1 && choiceIssues[0].Kind == BotConfigIssueKind.InvalidPostponeChoices, "Text in the choices is refused once");
        editor.PostponeChoicesText = "";
        check(editor.Validate(config).Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidPostponeChoices), "No choices are refused");
        editor.PostponeChoicesText = "30 ; 15,15";
        editor.MaxPauseMinutes = 20;
        check(editor.Validate(config).Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidPostponeChoices), "A choice longer than the longest postponement is refused");
        editor.MaxPauseMinutes = 30;
        check(editor.Save(config).Count == 0 && config.PostponeChoices.SequenceEqual([15, 30]), "Choices are saved sorted and once each");
        editor.PostponeChoicesText = "15,30";
        check(!editor.IsDirty, "Spaces in the choices are no edit");
        check(DiscordBotSettingsEditor.TryParseChoices("15 30\t60", out var parsed) && parsed.SequenceEqual([15, 30, 60])
              && !DiscordBotSettingsEditor.TryParseChoices("15,-5", out _) && !DiscordBotSettingsEditor.TryParseChoices("999999", out _),
            "Choices are parsed strictly");

        // A failing write leaves the settings in memory as they were.
        var detached = ValidConfig(string.Empty);
        var detachedBefore = Json(detached);
        editor.Load(detached);
        editor.LockoutMinutes = 99;
        var threw = false;
        try { editor.Save(detached); } catch (Exception) { threw = true; }
        check(threw && Json(detached) == detachedBefore, "When the file can not be written the settings in memory are restored");
    }

    private static void LimitChecks(Action<bool, string> check)
    {
        // The input ranges of the panel are the ones BotPolicy accepts: nothing in range is refused, nothing outside saved.
        var limits = new (BotLimitRange Range, Action<DiscordBotConfig, int> Set, string Name)[]
        {
            (DiscordBotLimits.Cooldown, (c, v) => c.CooldownSeconds = v, nameof(DiscordBotConfig.CooldownSeconds)),
            (DiscordBotLimits.CriticalLimit, (c, v) => c.CriticalLimit = v, nameof(DiscordBotConfig.CriticalLimit)),
            (DiscordBotLimits.CriticalWindow, (c, v) => c.CriticalWindowMinutes = v, nameof(DiscordBotConfig.CriticalWindowMinutes)),
            (DiscordBotLimits.LockoutAttempts, (c, v) => c.LockoutAttempts = v, nameof(DiscordBotConfig.LockoutAttempts)),
            (DiscordBotLimits.LockoutMinutes, (c, v) => c.LockoutMinutes = v, nameof(DiscordBotConfig.LockoutMinutes)),
            (DiscordBotLimits.ActionDelay, (c, v) => c.ActionDelayMinutes = v, nameof(DiscordBotConfig.ActionDelayMinutes)),
            (DiscordBotLimits.MaxPause, (c, v) =>
            {
                c.MaxPauseMinutes = v;
                c.PostponeChoices = [1];
            }, nameof(DiscordBotConfig.MaxPauseMinutes)),
        };
        foreach (var (range, set, name) in limits)
        {
            foreach (var (value, valid) in new[] { (range.Min, true), (range.Max, true), (range.Min - 1, false), (range.Max + 1, false) })
            {
                var config = ValidConfig(string.Empty);
                set(config, value);
                check(BotPolicy.Create(config).IsValid == valid, $"{name} = {value} is {(valid ? "" : "not ")}accepted like the panel offers it");
            }
            check(DiscordBotTexts.FieldLabel(name) is { Length: > 0 }, $"{name} has a label for its issues");
        }
        check(DiscordBotLimits.MaxPause.Max == DiscordBotConfig.HardMaxPauseMinutes, "The longest postponement ends at the hard limit");
        check(DiscordBotTexts.LocalizeDetail("CooldownSeconds: 0 (1-300)") == Trebuchet.Assets.Resources.DiscordBotCooldown + ": 0 (1-300)",
            "Limit issues show the label of the field");
    }

    private static void SettingsFileChecks(string root, Action<bool, string> check)
    {
        // Missing file: defaults, which allow nothing (the bot does not start).
        var missing = DiscordBotSettingsFile.Load(Path.Combine(root, "missing.json"), Path.Combine(root, "missing.token"), NullLogger.Instance);
        check(missing.IsLoaded && !BotPolicy.Create(missing.Config).IsValid && !missing.Config.AutoStart, "No settings file: off and fail-closed");

        // A file that can not be read (here: written by a newer version) must not stop Trebuchet and is never overwritten.
        var newer = Path.Combine(root, "newer.json");
        const string newerJson = "{ \"GuildId\": { \"Future\": true }, \"AutoStart\": true }";
        File.WriteAllText(newer, newerJson);
        var unreadable = DiscordBotSettingsFile.Load(newer, Path.Combine(root, "newer.token"), NullLogger.Instance);
        check(!unreadable.IsLoaded && !string.IsNullOrEmpty(unreadable.LoadError), "Unreadable settings are reported");
        check(!unreadable.Config.AutoStart && !BotPolicy.Create(unreadable.Config).IsValid, "Unreadable settings allow nothing and do not start the bot");
        check(unreadable.Config.FilePath != newer && unreadable.Config.FilePath.EndsWith(".unreadable", StringComparison.Ordinal),
            "The defaults would never be saved over the unreadable file");
        check(File.ReadAllText(newer) == newerJson, "The unreadable file stays untouched");

        // A damaged file is NOT repaired from its backup: the backup may hold users that were removed on purpose.
        var damagedPath = Path.Combine(root, "damaged.json");
        var good = ValidConfig(damagedPath);
        good.AutoStart = true;
        good.SaveFile();
        File.WriteAllText(damagedPath + ".bak", File.ReadAllText(damagedPath));
        foreach (var broken in new[] { "{ \"GuildId\": ", "   ", "[]" })
        {
            File.WriteAllText(damagedPath, broken);
            var damaged = DiscordBotSettingsFile.Load(damagedPath, Path.Combine(root, "damaged.token"), NullLogger.Instance);
            check(!damaged.IsLoaded && !damaged.Config.AutoStart && !BotPolicy.Create(damaged.Config).IsValid
                  && File.ReadAllText(damagedPath) == broken && !Directory.GetFiles(root, "damaged.json.corrupt-*").Any(),
                $"A damaged settings file ('{broken.Trim()}') is not repaired from the backup: the bot stays off and the file stays as it is");
        }

        // Null lists from a hand edited file are "nothing", not a crash.
        var nullsPath = Path.Combine(root, "nulls.json");
        File.WriteAllText(nullsPath, "{ \"GuildId\": \"" + Guild + "\", \"AllowedUserIds\": null, \"AllowedRoleIds\": null, \"AllowedChannelIds\": null,"
                                     + " \"EnabledCommands\": null, \"PostponeChoices\": null, \"StatusMessageIds\": null, \"Language\": null }");
        var nulls = DiscordBotSettingsFile.Load(nullsPath, Path.Combine(root, "nulls.token"), NullLogger.Instance);
        var nullEditor = new DiscordBotSettingsEditor();
        nullEditor.Load(nulls.Config);
        check(nulls.IsLoaded && !BotPolicy.Create(nulls.Config).IsValid && !nulls.Config.IsEnabled(BotCommand.Status) && nulls.Config.Language == "de",
            "Null lists in the settings file allow nothing and do not crash");

        // Another server: the status messages of the old one are dropped, the admin channel stays (an empty list would allow every channel).
        var movePath = Path.Combine(root, "move.json");
        var moved = ValidConfig(movePath);
        moved.SetAdminChannel(Channel);
        moved.StatusMessageIds[0] = 123456789012345678UL;
        moved.SaveFile();
        var moveEditor = new DiscordBotSettingsEditor();
        moveEditor.Load(moved);
        moveEditor.SetGuildIdText("100000000000000009");
        check(moveEditor.Save(moved).Count == 0 && moved.GuildId == 100000000000000009UL && moved.AllowedChannelIds.SequenceEqual([Channel]) && moved.StatusMessageIds.Count == 0,
            "Saving another server drops the status messages of the old one but keeps the admin channel (fail closed)");
    }

    private static void TextChecks(Action<bool, string> check)
    {
        var manager = Trebuchet.Assets.Resources.ResourceManager;
        var keys = manager.GetResourceSet(CultureInfo.InvariantCulture, true, true)!
            .Cast<DictionaryEntry>().Select(x => (string)x.Key).Where(x => x.StartsWith("DiscordBot", StringComparison.Ordinal)).ToList();
        check(keys.Count > 150, "The panel texts are in the resources");
        foreach (var culture in new[] { "de", "fr" })
        {
            var set = manager.GetResourceSet(CultureInfo.GetCultureInfo(culture), true, false);
            var translated = set!.Cast<DictionaryEntry>().Select(x => (string)x.Key).ToHashSet();
            var missing = keys.Where(x => !translated.Contains(x)).ToList();
            check(missing.Count == 0, $"Panel texts without {culture} translation: " + string.Join(", ", missing.Take(10)));
        }
        foreach (var culture in new[] { "en", "de", "fr" })
        {
            Trebuchet.Assets.Resources.Culture = CultureInfo.GetCultureInfo(culture);
            // English texts may equal the enum name ("Stopped"); German and French ones never do, so a missing
            // translation that falls back to the name is caught there.
            void Text(string text, Enum fallback, string what) =>
                check(!string.IsNullOrWhiteSpace(text) && (culture == "en" || text != fallback.ToString()), $"{what} has a {culture} text");
            foreach (var kind in Enum.GetValues<BotConfigIssueKind>())
                Text(DiscordBotTexts.Issue(new BotConfigIssue(kind)), kind, $"Issue {kind}");
            foreach (var state in Enum.GetValues<DiscordBotState>())
                Text(DiscordBotTexts.State(state), state, $"State {state}");
            foreach (var problem in Enum.GetValues<DiscordBotProblem>().Where(x => x != DiscordBotProblem.None))
                Text(DiscordBotTexts.Problem(problem), problem, $"Problem {problem}");
            check(DiscordBotTexts.Problem(DiscordBotProblem.None).Length == 0, "No problem, no text");
            var checkTexts = new HashSet<string>();
            foreach (var kind in Enum.GetValues<BotCheckKind>())
            foreach (var status in Enum.GetValues<BotCheckStatus>())
            {
                var text = DiscordBotTexts.Check(kind, status);
                Text(text.Contains(kind + ":", StringComparison.Ordinal) ? "" : text, kind, $"Check {kind} {status}");
                checkTexts.Add(text);
            }
            check(checkTexts.Count == Enum.GetValues<BotCheckKind>().Length * Enum.GetValues<BotCheckStatus>().Length,
                "Every check result has its own text");
            foreach (var command in Enum.GetValues<BotCommand>())
            {
                Text(DiscordBotTexts.Command(command), command, $"Command {command}");
                Text(DiscordBotTexts.CommandDescription(command), command, $"Command {command} description");
            }
            foreach (var level in Enum.GetValues<BotLevel>())
            {
                Text(DiscordBotTexts.Level(level), level, $"Level {level}");
                Text(DiscordBotTexts.LevelDescription(level), level, $"Level {level} description");
            }
            foreach (var result in Enum.GetValues<BotAuditResult>())
                Text(DiscordBotTexts.AuditResult(result), result, $"Audit result {result}");
            foreach (var result in Enum.GetValues<BotIdInputResult>().Where(x => x != BotIdInputResult.Added))
                Text(DiscordBotTexts.IdInput(result), result, $"ID input {result}");
            check(DiscordBotTexts.SetupSteps().Count == 9 && DiscordBotTexts.SetupSteps().All(x => x.Length > 10), "The setup checklist is complete");
        }
        Trebuchet.Assets.Resources.Culture = CultureInfo.GetCultureInfo("de");
        check(DiscordBotTexts.Check(BotCheckKind.Roles, BotCheckStatus.Error) != manager.GetString("DiscordBotCheckRolesError", CultureInfo.InvariantCulture)
              && Trebuchet.Assets.Resources.DiscordBotSecurityNotice.Contains("UND", StringComparison.Ordinal), "German texts are German");
        Trebuchet.Assets.Resources.Culture = CultureInfo.GetCultureInfo("en");

        // The token never reaches a text of the panel, wherever it comes from.
        check(!DiscordBotTexts.CheckLine(new BotCheckItem(BotCheckKind.Token, BotCheckStatus.Error, "login with " + SampleToken)).Contains(SampleToken),
            "A check detail never shows the token");
        var row = new AuditEntryRow(new BotAuditEntry(DateTime.UtcNow, BotAuditResult.Denied, User, "name\nfake line " + SampleToken,
            Guild, Channel, BotCommand.Stop, "p=" + SampleToken, "detail " + SampleToken));
        check(!row.Text.Contains(SampleToken) && !row.Text.Contains('\n') && row.IsError && row.Text.Contains(User.ToString()),
            "An audit line never shows the token and stays one line");
        check(new SecurityCheckRow(new BotCheckItem(BotCheckKind.Guild, BotCheckStatus.Warning)).IsWarning
              && new SecurityCheckRow(new BotCheckItem(BotCheckKind.Guild, BotCheckStatus.Ok)).Icon == "mdi-check-circle",
            "Check rows show their traffic light");
        check(Trebuchet.ViewModels.Panels.DiscordBotPanel.SetupGuideUrl == "https://github.com/Midnight-Vortex/Trebuchet/blob/master/docs/INSTALLME.md",
            "The setup button opens the guide of the repository");
    }

    private static void ListChecks(Action<bool, string> check)
    {
        var editor = new DiscordBotSettingsEditor();
        editor.Load(ValidConfig(string.Empty));
        var changes = 0;
        var users = new BotIdListViewModel("Users", "", editor.TryAddUser, editor.RemoveUser,
            () => new[] { new BotIdRow(editor.SuperAdminUserId, true, "Super admin") }
                .Concat(editor.UserIds.Where(x => x != editor.SuperAdminUserId).Select(x => new BotIdRow(x, false, ""))),
            () => changes++);
        check(users.Items.Count == 2 && users.Items[0].IsLocked && !users.Items[0].CanRemove, "The super admin heads the list, locked");
        users.Input = "1234";
        users.Add.Execute().Subscribe();
        check(users.HasError && users.Items.Count == 2 && changes == 0 && users.Input == "1234", "An invalid ID is refused with a message");
        users.Input = SampleToken;
        users.Add.Execute().Subscribe();
        check(users.HasError && users.Input.Length == 0 && changes == 0, "A pasted token is cleared from the box at once");
        users.Input = "200000000000000009";
        users.Add.Execute().Subscribe();
        check(!users.HasError && users.Items.Count == 3 && users.Input.Length == 0 && changes == 1, "A valid ID is added");
        users.Remove.Execute(users.Items[0]).Subscribe();
        check(users.Items.Count == 3 && editor.SuperAdminUserId == Admin && changes == 1, "The locked row can not be removed");
        users.Remove.Execute(users.Items[2]).Subscribe();
        check(users.Items.Count == 2 && changes == 2, "Another row can be removed");
        users.IsEnabled = false;
        var canAdd = true;
        users.Input = "200000000000000008";
        using (users.Add.CanExecute.Subscribe(x => canAdd = x)) { }
        check(!canAdd, "A list that can not be edited can not add");
        users.Add.Execute().Subscribe();
        users.Remove.Execute(users.Items[1]).Subscribe();
        check(users.Items.Count == 2 && editor.UserIds.Contains(User) && !editor.UserIds.Contains(200000000000000008UL) && changes == 2,
            "A list that can not be edited changes nothing, even when its commands are called directly");
    }

    private static void SecretChecks(string root, Action<bool, string> check)
    {
        var path = Path.Combine(root, "bot.token");
        string? environment = null;
        var tokens = new BotTokenStore(path, new FlipProtector(), _ => environment);
        var field = new SecretField(new BotTokenSecretStore(tokens, _ => environment))
            .SetTexts("saved", "from environment", "no storage", "invalid", "failed", "paste");
        var removeAnswer = false;
        field.ConfirmRemove = () => Task.FromResult(removeAnswer);
        check(!field.IsSet && field.IsEditing && field.CanSave && !field.HasNote, "Without a token the input is shown");
        field.Submit.Execute("not a token").Subscribe();
        check(field.Error == "invalid" && !File.Exists(path) && field.IsEditing, "Something that is no token is refused, nothing written");
        field.Submit.Execute("  Bot " + SampleToken + "\n").Subscribe();
        check(field.IsSet && !field.IsEditing && !field.HasError && File.Exists(path) && tokens.Load() == SampleToken,
            "The token is stored and the input closes");
        check(File.ReadAllText(path).IndexOf(SampleToken, StringComparison.Ordinal) < 0, "The token file holds no plain token");
        var texts = typeof(SecretField).GetProperties().Where(x => x.PropertyType == typeof(string) && x.GetIndexParameters().Length == 0)
            .Select(x => x.GetValue(field) as string ?? "").ToList();
        check(texts.All(x => !x.Contains(SampleToken) && !x.Contains("GabcDE")), "No property of the field holds the token");
        field.StartReplace.Execute().Subscribe();
        check(field.IsEditing && field.CanReplace, "A saved token can be replaced");
        field.CancelReplace.Execute().Subscribe();
        check(!field.IsEditing, "Replacing can be cancelled");
        field.Remove.Execute().Subscribe();
        check(field.IsSet && File.Exists(path), "Removing asks first, no means no");
        removeAnswer = true;
        field.Remove.Execute().Subscribe();
        check(!field.IsSet && !File.Exists(path) && field.IsEditing, "The token can be removed");

        environment = SampleToken;
        field.Refresh();
        check(field.IsSet && field.FromEnvironment && !field.CanReplace && !field.IsEditing && field.Note == "from environment",
            "A token from the environment variable is shown as such and is not managed here");
        environment = null;

        var unsafeStore = new FakeSecretStore();
        var noStorage = new SecretField(unsafeStore).SetTexts("saved", "env", "no storage", "invalid", "failed", "");
        check(!noStorage.IsEditing && noStorage.Note == "no storage", "Without safe storage the field says to use the environment variable");
        noStorage.Submit.Execute(SampleToken).Subscribe();
        check(unsafeStore.Saves == 0, "Nothing is stored where it can not be stored safely");
    }

    private static void HostChecks(string root, Action<bool, string> check)
    {
        (DiscordBotHost Host, FakeBot Bot, AvaloniaBotDispatcher Dispatcher) Create(DiscordBotSettingsFile settings, bool broken = false)
        {
            var bot = new FakeBot();
            var services = new ServiceCollection();
            if (broken) services.AddSingleton<IDiscordBotService>(_ => throw new InvalidOperationException("broken"));
            else services.AddSingleton<IDiscordBotService>(bot);
            var dispatcher = new AvaloniaBotDispatcher(() => false, job => _ = job());
            var host = new DiscordBotHost(services.BuildServiceProvider(), settings, dispatcher, NullLogger<DiscordBotHost>.Instance)
            {
                TokenInUseRetryDelay = TimeSpan.FromMilliseconds(1)
            };
            return (host, bot, dispatcher);
        }

        DiscordBotSettingsFile Settings(string name, bool autoStart, bool valid)
        {
            var path = Path.Combine(root, name + ".json");
            var config = valid ? ValidConfig(path) : DiscordBotConfig.CreateConfig(path);
            config.AutoStart = autoStart;
            config.SaveFile();
            return DiscordBotSettingsFile.Load(path, Path.ChangeExtension(path, ".token"), NullLogger.Instance);
        }

        var (off, offBot, _) = Create(Settings("auto-off", false, true));
        off.AutoStartAsync().GetAwaiter().GetResult();
        check(offBot.Starts == 0 && !off.AutoStartSkipped, "The bot does not start with Trebuchet by default");

        var (invalid, invalidBot, _) = Create(Settings("auto-invalid", true, false));
        invalid.AutoStartAsync().GetAwaiter().GetResult();
        check(invalidBot.Starts == 0 && invalid.AutoStartSkipped, "Invalid settings are not started with Trebuchet, and the panel can say so");

        var (auto, autoBot, _) = Create(Settings("auto-on", true, true));
        auto.AutoStartAsync().GetAwaiter().GetResult();
        check(autoBot.Starts == 1, "Valid settings with \"start with Trebuchet\" start the bot");

        var (busy, busyBot, _) = Create(Settings("auto-busy", true, true));
        busyBot.Problem = DiscordBotProblem.TokenInUse;
        busy.AutoStartAsync().GetAwaiter().GetResult();
        check(busyBot.Starts == busy.TokenInUseAttempts, "A token still held by the previous Trebuchet is tried again a few times");

        var newer = Path.Combine(root, "auto-unreadable.json");
        File.WriteAllText(newer, "{ \"AutoStart\": true, \"GuildId\": [1] }");
        var (unreadable, unreadableBot, _) = Create(DiscordBotSettingsFile.Load(newer, newer + ".token", NullLogger.Instance));
        unreadable.AutoStartAsync().GetAwaiter().GetResult();
        check(unreadableBot.Starts == 0 && !unreadable.IsAvailable, "Unreadable settings never start the bot");
        var refused = false;
        try { unreadable.StartAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException) { refused = true; }
        check(refused && unreadableBot.Starts == 0, "Unreadable settings can not be started by hand either");

        var (broken, _, _) = Create(Settings("broken", true, true), broken: true);
        broken.AutoStartAsync().GetAwaiter().GetResult();
        check(broken.Service is null && broken.CreationFailed && !broken.IsAvailable, "A bot that can not be created leaves Trebuchet running");
        var failed = false;
        try { broken.StartAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException) { failed = true; }
        check(failed, "Starting a bot that could not be created reports it");

        var (manual, manualBot, manualDispatcher) = Create(Settings("manual", false, true));
        manual.StartAsync().GetAwaiter().GetResult();
        manual.ApplySettingsAsync().GetAwaiter().GetResult();
        check(manualBot.Starts == 1 && manualBot.Applies == 1 && manual.IsAvailable, "Start and apply reach the bot");
        manual.Shutdown(TimeSpan.FromSeconds(2));
        manual.Shutdown(TimeSpan.FromSeconds(2));
        check(manualBot.Disposals == 1 && manualDispatcher.IsClosed && manual.IsShutDown && !manual.IsAvailable,
            "Exiting disposes the bot once and closes its way into the launcher");
        var afterExit = false;
        try { manual.StartAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException) { afterExit = true; }
        check(afterExit && manualBot.Starts == 1, "Nothing starts the bot while Trebuchet exits");

        var (hanging, hangingBot, _) = Create(Settings("hanging", false, true));
        _ = hanging.Service;
        hangingBot.HangOnDispose = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        hanging.Shutdown(TimeSpan.FromMilliseconds(300));
        check(watch.Elapsed < TimeSpan.FromSeconds(5), "A bot that does not stop can not keep Trebuchet open");
    }

    private static void DispatcherChecks(Action<bool, string> check)
    {
        using var ui = new TestUiThread();
        var dispatcher = new AvaloniaBotDispatcher(() => ui.IsCurrent, ui.Post);

        // From a bot thread: runs on the UI thread, result and errors come back, the bot continues off the UI thread.
        var (result, ranOn, continuedOn) = Task.Run(async () =>
        {
            var ranOnUi = 0;
            var value = await dispatcher.InvokeAsync(() =>
            {
                ranOnUi = Environment.CurrentManagedThreadId;
                return Task.FromResult(42);
            });
            return (value, ranOnUi, Environment.CurrentManagedThreadId);
        }).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        check(result == 42 && ranOn == ui.ThreadId, "Bot work runs on the UI thread and returns its result");
        check(continuedOn != ui.ThreadId, "The bot does not continue on the UI thread");
        // Not even a continuation that asks to run synchronously: the UI thread is never borrowed by the bot. The UI
        // thread is held busy until the continuation is registered, so it completes the work afterwards for sure.
        using (var hold = new ManualResetEventSlim())
        {
            ui.Post(() => hold.Wait(TimeSpan.FromSeconds(10)));
            var continuation = Task.Factory.StartNew(() => dispatcher.InvokeAsync(() => Task.FromResult(0))
                    .ContinueWith(_ => Environment.CurrentManagedThreadId, TaskContinuationOptions.ExecuteSynchronously),
                CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default).GetAwaiter().GetResult();
            hold.Set();
            check(continuation.Wait(TimeSpan.FromSeconds(10)) && continuation.Result != ui.ThreadId,
                "Synchronous continuations of the bot do not run on the UI thread");
        }
        var error = Task.Run(() => dispatcher.InvokeAsync<int>(() => throw new InvalidOperationException("launcher")));
        check(SpinFor(() => error.IsCompleted) && error.Exception?.GetBaseException().Message == "launcher", "An error of the work reaches the bot");
        var awaited = Task.Run(() => dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(10);
            return Environment.CurrentManagedThreadId;
        }));
        check(SpinFor(() => awaited.IsCompleted) && awaited.Result == ui.ThreadId, "Asynchronous work resumes on the UI thread");

        // On the UI thread itself: runs at once. Queueing it and blocking for it would deadlock the window.
        var nested = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(() =>
        {
            try { nested.TrySetResult(dispatcher.InvokeAsync(() => Task.FromResult(7)).Result); }
            catch (Exception ex) { nested.TrySetException(ex); }
        });
        check(nested.Task.Wait(TimeSpan.FromSeconds(5)) && nested.Task.Result == 7, "Work started on the UI thread does not deadlock");

        // Exit: queued work is cancelled and never runs; afterwards nothing is accepted any more.
        using var release = new ManualResetEventSlim();
        ui.Post(() => release.Wait(TimeSpan.FromSeconds(10)));
        var ran = false;
        // Asked from a pool thread (not the UI thread); StartNew hands back the pending task without waiting for it.
        var queued = Task.Factory.StartNew(() => dispatcher.InvokeAsync(() =>
        {
            ran = true;
            return Task.FromResult(1);
        }), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default).GetAwaiter().GetResult();
        check(!queued.IsCompleted, "Work waits while the UI thread is busy");
        dispatcher.Close();
        check(SpinFor(() => queued.IsCanceled), "Closing cancels the waiting work");
        release.Set();
        var after = dispatcher.InvokeAsync(() => Task.FromResult(2));
        var marker = new TaskCompletionSource();
        ui.Post(() => marker.SetResult());
        check(marker.Task.Wait(TimeSpan.FromSeconds(5)) && !ran && after.IsCanceled && dispatcher.IsClosed,
            "Cancelled work never reaches the launcher, new work is refused");
    }

    private static bool SpinFor(Func<bool> condition) => SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10));

    /// <summary>A single thread with a synchronization context, like the UI thread of the window.</summary>
    private sealed class TestUiThread : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public TestUiThread()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Test UI thread" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;
        public bool IsCurrent => Thread.CurrentThread == _thread;

        public void Post(Action action) => _queue.Add(action);
        public void Post(Func<Task> job) => _queue.Add(() => _ = job());

        public void Dispose() => _queue.CompleteAdding();

        private void Loop()
        {
            SynchronizationContext.SetSynchronizationContext(new Context(this));
            foreach (var action in _queue.GetConsumingEnumerable())
                action();
        }

        private sealed class Context(TestUiThread owner) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object? state) => owner.Post(() => callback(state));
        }
    }

    private sealed class FlipProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Select(x => (byte)(x ^ 0x5A)).Reverse().ToArray();
        public byte[] Unprotect(byte[] data) => data.Reverse().Select(x => (byte)(x ^ 0x5A)).ToArray();
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public int Saves { get; private set; }
        public bool HasSecret => false;
        public bool FromEnvironment => false;
        public bool CanSave => false;
        public void Save(string secret) => Saves++;
        public void Delete() { }
    }

    private sealed class FakeBot : IDiscordBotService
    {
        public int Starts { get; private set; }
        public int Applies { get; private set; }
        public int Disposals { get; private set; }
        public bool HangOnDispose { get; set; }
        public DiscordBotState State { get; private set; } = DiscordBotState.Stopped;
        public DiscordBotProblem Problem { get; set; }
        public IReadOnlyList<BotConfigIssue> Issues => [];
        public string? BotName => null;
        public string? GuildName => null;
        public int LatencyMs => -1;
        public DateTime? ConnectedSinceUtc => null;
        public event EventHandler? StateChanged;

        public Task StartAsync(CancellationToken token)
        {
            Starts++;
            if (Problem == DiscordBotProblem.None) State = DiscordBotState.Connected;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            State = DiscordBotState.Stopped;
            return Task.CompletedTask;
        }

        public Task ApplySettingsAsync()
        {
            Applies++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BotCheckItem>> CheckSecurityAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<BotCheckItem>>([]);

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return HangOnDispose ? new ValueTask(new TaskCompletionSource().Task) : ValueTask.CompletedTask;
        }
    }
}
