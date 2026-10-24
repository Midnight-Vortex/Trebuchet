using System.Text;
using System.Text.Json;
using TrebuchetLib.DiscordBot;

/// <summary>
/// Security checks of the Discord bot: nobody is allowed anything without both the user ID and the role, bad settings
/// allow nothing, inputs and logs can not be faked, the token stays out of files and logs.
/// </summary>
static class DiscordBotChecks
{
    private const ulong Guild = 100000000000000001UL;
    private const ulong SuperAdmin = 200000000000000001UL;
    private const ulong Operator = 200000000000000002UL;
    private const ulong Stranger = 200000000000000003UL;
    private const ulong Role = 300000000000000001UL;
    private const ulong OtherRole = 300000000000000002UL;
    private const ulong Channel = 400000000000000001UL;
    private const ulong OtherChannel = 400000000000000002UL;
    // Made up. Put together at run time so secret scanners do not take the source for a real token.
    // A different token in every run: the lock of a token is a machine wide name (parallel runs must not meet).
    private static readonly string SampleToken = string.Join(".", "MTIzNDU2Nzg5MDEyMzQ1Njc4", "GabcDE", "abcdefghijklmnopqrstuvwxyz0123456789ABCD" + Guid.NewGuid().ToString("N")[..8]);

    private sealed class ReversingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Reverse().Select(x => (byte)(x ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => data.Select(x => (byte)(x ^ 0x5A)).Reverse().ToArray();
    }

    private sealed class ThrowingRoles : IReadOnlyCollection<ulong>
    {
        public int Count => 1;
        public IEnumerator<ulong> GetEnumerator() => throw new InvalidOperationException("broken");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static DiscordBotConfig ValidConfig()
    {
        var config = new DiscordBotConfig { GuildId = Guild, SuperAdminUserId = SuperAdmin };
        config.AddUser(Operator);
        config.AddRole(Role);
        return config;
    }

    private static BotPolicy PolicyOf(DiscordBotConfig config)
    {
        var result = BotPolicy.Create(config);
        if (result.Policy is null) throw new Exception("The settings of the check are not valid: " + string.Join(", ", result.Issues.Select(x => x.Kind + " " + x.Detail)));
        return result.Policy;
    }

    private static BotRequest Request(ulong user = Operator, ulong? guild = Guild, ulong? channel = Channel, ulong[]? roles = null,
        bool human = true, BotCommand command = BotCommand.Status, ulong interaction = 0)
        => new(user, guild, channel, roles ?? [Role], human, command, interaction);

    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var t0 = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        // --- IDs
        Check(BotId.TryParse("12345678901234567", out var parsedId) && parsedId == 12345678901234567UL, "A 17 digit ID is valid");
        Check(BotId.TryParse("18446744073709551615", out _), "A 20 digit ID is valid");
        foreach (var bad in new[] { null, "", "0", "1234", "1234567890123456", " 12345678901234567", "+12345678901234567", "12345678901234567 ",
                     "1234567890123456a", "184467440737095516150", "-12345678901234567", "１２３４５６７８９０１２３４５６７" })
            Check(!BotId.TryParse(bad, out _), $"'{bad}' is no ID");

        // --- settings: anything missing or wrong means no policy, so nobody is allowed anything
        var empty = BotPolicy.Create(new DiscordBotConfig());
        Check(!empty.IsValid && empty.Policy is null, "New settings give no policy");
        Check(empty.Issues.Any(x => x.Kind == BotConfigIssueKind.GuildMissing)
              && empty.Issues.Any(x => x.Kind == BotConfigIssueKind.SuperAdminMissing)
              && empty.Issues.Any(x => x.Kind == BotConfigIssueKind.NoRoles), "Server, super admin and roles are required");
        Check(!BotPolicy.Create(null).IsValid, "No settings give no policy");

        var noRole = ValidConfig();
        noRole.AllowedRoleIds.Clear();
        Check(!BotPolicy.Create(noRole).IsValid, "Without a role nobody is allowed: both the ID and the role are needed");
        var noAdmin = ValidConfig();
        noAdmin.SuperAdminUserId = 0;
        Check(!BotPolicy.Create(noAdmin).IsValid, "Without a super admin the bot does not start");
        var noGuild = ValidConfig();
        noGuild.GuildId = 0;
        Check(!BotPolicy.Create(noGuild).IsValid, "Without a server the bot does not start");

        var everyone = ValidConfig();
        everyone.AllowedRoleIds.Add(Guild);
        Check(BotPolicy.Create(everyone).Issues.Any(x => x.Kind == BotConfigIssueKind.EveryoneRole),
            "The role @everyone (ID of the server) is refused");
        var smallId = ValidConfig();
        smallId.AllowedUserIds.Add(12345);
        Check(BotPolicy.Create(smallId).Issues.Any(x => x.Kind == BotConfigIssueKind.InvalidId), "A number that is no ID is refused");
        var badChannel = ValidConfig();
        badChannel.AllowedChannelIds.Add(7);
        Check(!BotPolicy.Create(badChannel).IsValid, "A channel that is no ID is refused");
        var badAudit = ValidConfig();
        badAudit.AuditChannelId = 7;
        Check(!BotPolicy.Create(badAudit).IsValid, "An audit channel that is no ID is refused");

        foreach (var mutate in new Action<DiscordBotConfig>[]
                 {
                     c => c.CooldownSeconds = 0, c => c.CriticalLimit = 0, c => c.CriticalWindowMinutes = 0, c => c.LockoutAttempts = 0,
                     c => c.LockoutMinutes = 0, c => c.MaxPauseMinutes = 0, c => c.MaxPauseMinutes = DiscordBotConfig.HardMaxPauseMinutes + 1, c => c.ActionDelayMinutes = -1, c => c.ActionDelayMinutes = 61,
                     c => c.PostponeChoices = [], c => c.PostponeChoices = [0], c => c.PostponeChoices = [361],
                     c => c.PostponeChoices = Enumerable.Range(1, 26).ToList(), c => c.EnabledCommands.Add("FormatDisk"),
                     c => c.EnabledCommands.Add("status")
                 })
        {
            var config = ValidConfig();
            mutate(config);
            Check(!BotPolicy.Create(config).IsValid, "Limits out of range and unknown commands are refused");
        }

        var good = ValidConfig();
        var policy = PolicyOf(good);
        Check(policy.IsCommandEnabled(BotCommand.Status) && policy.IsCommandEnabled(BotCommand.Postpone)
              && policy.IsCommandEnabled(BotCommand.Resume) && policy.IsCommandEnabled(BotCommand.SetChannel), "Safe commands are on by default");
        Check(!policy.IsCommandEnabled(BotCommand.RestartNow) && !policy.IsCommandEnabled(BotCommand.Stop)
              && !policy.IsCommandEnabled(BotCommand.Start) && !policy.IsCommandEnabled(BotCommand.Update), "Critical and super admin commands are off by default");
        Check(policy.UserIds.Contains(SuperAdmin) && policy.UserIds.Contains(Operator), "The super admin is always among the users");
        Check(policy.PostponeChoices.SequenceEqual([15, 30, 60, 120, 180, 360]), "Default postponement choices");
        Check(policy.ActionDelay == TimeSpan.FromMinutes(5), "Default warning time of restarts and stops from Discord");

        // --- the double check
        Check(BotAuthorizer.Authorize(policy, Request()).Allowed, "Listed user with an allowed role is allowed");
        Check(BotAuthorizer.Authorize(policy, Request(SuperAdmin)).Allowed, "The super admin with an allowed role is allowed");
        Check(BotAuthorizer.Authorize(policy, Request(SuperAdmin, roles: [OtherRole])).Reason == BotDenyReason.MissingRole,
            "The super admin needs the role too (strict double check)");
        Check(BotAuthorizer.Authorize(policy, Request(Operator, roles: [])).Reason == BotDenyReason.MissingRole, "A listed user without a role is refused");
        Check(BotAuthorizer.Authorize(policy, Request(Operator, roles: [OtherRole, Guild])).Reason == BotDenyReason.MissingRole,
            "Other roles and @everyone are not enough");
        Check(BotAuthorizer.Authorize(policy, Request(Stranger)).Reason == BotDenyReason.NotListedUser, "An unlisted user with the role is refused");
        Check(BotAuthorizer.Authorize(policy, Request(Stranger, roles: [])).Reason == BotDenyReason.NotListedUser, "An unlisted user without a role is refused");
        Check(BotAuthorizer.Authorize(policy, Request(guild: null)).Reason == BotDenyReason.NotInGuild, "A direct message is refused");
        Check(BotAuthorizer.Authorize(policy, Request(guild: Guild + 1)).Reason == BotDenyReason.WrongGuild, "Another server is refused");
        Check(BotAuthorizer.Authorize(policy, Request(human: false)).Reason == BotDenyReason.NotHuman, "Bots and webhooks are refused");
        Check(BotAuthorizer.Authorize(policy, Request(0)).Reason == BotDenyReason.NotListedUser, "User 0 is refused");
        Check(BotAuthorizer.Authorize(null, Request()).Reason == BotDenyReason.NoPolicy, "No policy refuses everything");
        Check(BotAuthorizer.Authorize(policy, null).Reason == BotDenyReason.Error, "No request is refused");
        Check(BotAuthorizer.Authorize(policy, Request() with { RoleIds = null! }).Reason == BotDenyReason.MissingRole, "Unknown roles are refused");
        Check(BotAuthorizer.Authorize(policy, Request() with { RoleIds = new ThrowingRoles() }).Reason == BotDenyReason.Error,
            "An error in the check means no");

        // Every combination of the four checks: only all of them together allow.
        for (var mask = 0; mask < 16; mask++)
        {
            var channelConfig = ValidConfig();
            channelConfig.AllowedChannelIds = [Channel];
            var channelPolicy = PolicyOf(channelConfig);
            var guildOk = (mask & 1) != 0;
            var userOk = (mask & 2) != 0;
            var roleOk = (mask & 4) != 0;
            var channelOk = (mask & 8) != 0;
            var decision = BotAuthorizer.Authorize(channelPolicy, Request(userOk ? Operator : Stranger, guildOk ? Guild : Guild + 5,
                channelOk ? Channel : OtherChannel, roleOk ? [Role] : [OtherRole]));
            Check(decision.Allowed == (guildOk && userOk && roleOk && channelOk), $"Only the full combination allows (mask {mask})");
        }

        // --- commands and levels
        Check(BotAuthorizer.Authorize(policy, Request(command: BotCommand.Stop)).Reason == BotDenyReason.CommandDisabled, "A switched off command is refused");
        Check(BotAuthorizer.Authorize(policy, Request(Operator, command: BotCommand.SetChannel)).Reason == BotDenyReason.LevelTooLow,
            "Only the super admin may set the channel");
        Check(BotAuthorizer.Authorize(policy, Request(SuperAdmin, command: BotCommand.SetChannel)).Allowed, "The super admin may set the channel");
        var allOn = ValidConfig();
        foreach (var command in Enum.GetValues<BotCommand>()) allOn.SetEnabled(command, true);
        var allOnPolicy = PolicyOf(allOn);
        Check(BotAuthorizer.Authorize(allOnPolicy, Request(Operator, command: BotCommand.Stop)).Allowed
              && BotAuthorizer.Authorize(allOnPolicy, Request(Operator, command: BotCommand.RestartNow)).Allowed, "Stop and restart work for users once switched on");
        Check(BotAuthorizer.Authorize(allOnPolicy, Request(Operator, command: BotCommand.Start)).Reason == BotDenyReason.LevelTooLow
              && BotAuthorizer.Authorize(allOnPolicy, Request(Operator, command: BotCommand.Update)).Reason == BotDenyReason.LevelTooLow,
            "Start and update stay with the super admin, even when on");
        Check(BotAuthorizer.Authorize(allOnPolicy, Request(SuperAdmin, command: BotCommand.Start)).Allowed, "The super admin may start");
        // The super admin always has every right: the switches only limit the other users. Everything else still applies.
        Check(Enum.GetValues<BotCommand>().All(c => BotAuthorizer.Authorize(policy, Request(SuperAdmin, command: c)).Allowed),
            "The super admin may use every command without switching any on");
        Check(Enum.GetValues<BotCommand>().Where(c => !policy.IsCommandEnabled(c)).All(c =>
                  BotAuthorizer.Authorize(policy, Request(Operator, command: c)).Reason is BotDenyReason.CommandDisabled),
            "The switches still hold back every other user");
        Check(BotAuthorizer.Authorize(policy, Request(SuperAdmin, roles: [OtherRole], command: BotCommand.Stop)).Reason == BotDenyReason.MissingRole
              && BotAuthorizer.Authorize(policy, Request(SuperAdmin, channel: OtherChannel, command: BotCommand.Stop) with { }).Allowed,
            "The super admin still needs the role (the channel only restricts when one is set)");
        Check(BotAuthorizer.Authorize(policy, Request(SuperAdmin, guild: Guild + 1, command: BotCommand.Stop)).Reason == BotDenyReason.WrongGuild
              && BotAuthorizer.Authorize(policy, Request(SuperAdmin, human: false, command: BotCommand.Stop)).Reason == BotDenyReason.NotHuman,
            "...and the server and the human check");
        var channelBound = ValidConfig();
        channelBound.SetAdminChannel(Channel);
        Check(BotAuthorizer.Authorize(PolicyOf(channelBound), Request(SuperAdmin, channel: OtherChannel, command: BotCommand.Stop)).Reason == BotDenyReason.WrongChannel,
            "...and the admin channel");
        Check(BotCommands.EnabledByDefault.Contains(BotCommand.CheckUpdates) && BotCommands.LevelOf(BotCommand.CheckUpdates) == BotLevel.Manage
              && !BotCommands.NeedsConfirmation(BotCommand.CheckUpdates) && !BotCommands.IsLimited(BotCommand.CheckUpdates),
            "The update check is a harmless command: on by default, no confirmation");
        Check(Enum.GetValues<BotCommand>().All(c => BotCommands.NeedsConfirmation(c) == (BotCommands.LevelOf(c) >= BotLevel.Critical)), "Critical commands need a confirmation");
        Check(!BotCommands.NeedsConfirmation(BotCommand.Postpone) && !BotCommands.NeedsConfirmation(BotCommand.Status), "Postponing needs no confirmation");

        // The channel: restricts the commands, but the channel command works from the new channel.
        var channelOnly = ValidConfig();
        channelOnly.SetAdminChannel(Channel);
        var channelOnlyPolicy = PolicyOf(channelOnly);
        Check(BotAuthorizer.Authorize(channelOnlyPolicy, Request(channel: OtherChannel)).Reason == BotDenyReason.WrongChannel, "Another channel is refused");
        Check(BotAuthorizer.Authorize(channelOnlyPolicy, Request(channel: null)).Reason == BotDenyReason.WrongChannel, "Without a channel it is refused");
        Check(BotAuthorizer.Authorize(channelOnlyPolicy, Request(SuperAdmin, channel: OtherChannel, command: BotCommand.SetChannel)).Allowed,
            "The channel command works from the channel to set");
        Check(BotAuthorizer.Authorize(channelOnlyPolicy, Request(Operator, channel: OtherChannel, command: BotCommand.SetChannel)).Reason == BotDenyReason.LevelTooLow,
            "Not for anyone but the super admin");
        Check(BotAuthorizer.Authorize(channelOnlyPolicy, Request(Stranger, channel: OtherChannel, command: BotCommand.SetChannel)).Reason == BotDenyReason.NotListedUser,
            "Not for strangers");

        // A policy is a copy: later changes of the settings do not reach it.
        var live = ValidConfig();
        var copy = PolicyOf(live);
        live.RemoveRole(Role);
        live.AllowedUserIds.Clear();
        Check(BotAuthorizer.Authorize(copy, Request()).Allowed, "A policy does not change behind its back");
        Check(!BotPolicy.Create(live).IsValid, "A new policy of the changed settings is checked again");

        // --- settings
        var rules = ValidConfig();
        rules.AddUser(SuperAdmin);
        Check(!rules.RemoveUser(SuperAdmin) && rules.AllowedUserIds.Contains(SuperAdmin), "The super admin can not be removed");
        Check(rules.RemoveUser(Operator) && !rules.RemoveUser(Operator), "Other users can be removed");
        Check(!rules.AddUser(5) && rules.AddUser(Operator) && !rules.AddUser(Operator), "Only valid new IDs are added");
        rules.StatusMessageIds[0] = 123456789012345678UL;
        rules.SetAdminChannel(Channel);
        Check(rules.AllowedChannelIds.SequenceEqual([Channel]) && rules.StatusMessageIds.Count == 0, "The channel command replaces the channel and drops old status messages");
        rules.StatusMessageIds[0] = 123456789012345678UL;
        rules.SetAdminChannel(Channel);
        Check(rules.AllowedChannelIds.SequenceEqual([Channel]) && rules.StatusMessageIds.Count == 1,
            "Setting the channel it already is keeps its status messages (no second set next to the old one)");
        var threw = false;
        try { rules.SetAdminChannel(12); } catch (ArgumentException) { threw = true; }
        Check(threw, "A channel that is no ID can not be set");

        var folder = Path.Combine(Path.GetTempPath(), "TrebuchetConsoleChecks-bot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // IDs are written as text and read as text or number.
            var path = Path.Combine(folder, "discord-bot.test.json");
            var saved = DiscordBotConfig.CreateConfig(path);
            saved.GuildId = Guild;
            saved.SuperAdminUserId = SuperAdmin;
            saved.AddUser(Operator);
            saved.AddRole(Role);
            saved.StatusMessageIds[1] = 123456789012345678UL;
            saved.SaveFile();
            var json = File.ReadAllText(path);
            Check(json.Contains($"\"{Guild}\"") && json.Contains("\"123456789012345678\""), "IDs are written as text");
            var loaded = DiscordBotConfig.LoadConfig(path);
            Check(loaded.GuildId == Guild && loaded.AllowedUserIds.SequenceEqual([Operator]) && loaded.StatusMessageIds[1] == 123456789012345678UL
                  && loaded.AutoStart == false && PolicyOf(loaded).IsCommandEnabled(BotCommand.Postpone), "The settings survive saving");
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["GuildId"] = Guild, ["SuperAdminUserId"] = SuperAdmin, ["AllowedRoleIds"] = new[] { Role }
            }));
            var numbers = DiscordBotConfig.LoadConfig(path);
            Check(numbers.GuildId == Guild && numbers.AllowedRoleIds.SequenceEqual([Role]), "IDs written as numbers are read too");
            File.WriteAllText(path, "{ \"GuildId\": \"" + Guild + "\", \"AllowedUserIds\": null, \"AllowedRoleIds\": null, \"AllowedChannelIds\": null, \"EnabledCommands\": null,"
                                    + " \"PostponeChoices\": null, \"StatusMessageIds\": null, \"Language\": null }");
            var withNulls = DiscordBotConfig.LoadConfig(path);
            Check(withNulls.AllowedUserIds.Count == 0 && withNulls.StatusMessageIds.Count == 0 && withNulls.Language == "de" && !withNulls.IsEnabled(BotCommand.Status)
                  && !BotPolicy.Create(withNulls).IsValid, "Null lists in a hand edited file mean nothing and never throw");
            File.WriteAllText(path, "{ this is not json");
            var damaged = DiscordBotConfig.LoadConfig(path);
            Check(!BotPolicy.Create(damaged).IsValid, "Damaged settings give defaults that allow nobody");

            // --- audit log
            var auditFolder = Path.Combine(folder, "audit");
            var audit = new BotAuditLog(auditFolder, memoryEntries: 3);
            var raised = 0;
            audit.EntryWritten += (_, _) => raised++;
            audit.Write(new BotAuditEntry(t0, BotAuditResult.Denied, Stranger, "Eve\n2026-10-23 12:00:01Z | ALLOWED | user=1", Guild, Channel,
                BotCommand.Stop, $"reason=\r\nfake {SampleToken}", "MissingRole"));
            var logFile = Path.Combine(auditFolder, BotAuditLog.GetFileName(t0));
            var lines = File.ReadAllLines(logFile);
            Check(lines.Length == 1 && lines[0].Contains("DENIED") && lines[0].Contains($"user={Stranger}"), "One line per entry");
            Check(!lines[0].Contains("| ALLOWED |"), "A display name can not fake the fields of an entry");
            Check(!File.ReadAllText(logFile).Contains(SampleToken) && lines[0].Contains(BotLogRedactor.Replacement), "A token never reaches the audit log");
            Check(raised == 1, "The audit log reports new entries");
            for (var i = 0; i < 5; i++)
                audit.Write(new BotAuditEntry(t0.AddMinutes(i), BotAuditResult.Allowed, Operator, "Op", Guild, Channel, BotCommand.Status, "", ""));
            Check(audit.Recent.Count == 3 && File.ReadAllLines(logFile).Length == 6, "The list in memory is limited, the file keeps everything");
            Check(BotAuditLog.GetFileName(t0) == "discord-bot-audit-202610.log", "One file per month");
            var blocked = Path.Combine(folder, "blocked");
            File.WriteAllText(blocked, "a file where a folder should be");
            var failing = new BotAuditLog(Path.Combine(blocked, "sub"));
            failing.Write(new BotAuditEntry(t0, BotAuditResult.Allowed, Operator, "Op", Guild, Channel, null, "", ""));
            Check(failing.Recent.Count == 1, "An audit log that can not write does not throw");

            // --- token
            var tokenPath = Path.Combine(folder, "bot.token");
            var store = new BotTokenStore(tokenPath, new ReversingProtector(), _ => null);
            Check(!store.HasToken && store.Load() is null, "No token at first");
            store.Save("Bot \"" + SampleToken + "\"");
            Check(store.HasToken && store.Load() == SampleToken, "The token is stored and read back, a pasted 'Bot ' prefix and quotes are dropped");
            Check(File.ReadAllBytes(tokenPath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(SampleToken)) < 0, "The token file holds no plain token");
            Check(new BotTokenStore(tokenPath, new ReversingProtector(), _ => "").Load() == SampleToken, "An empty environment variable is ignored");
            var environmentToken = SampleToken.Replace("GabcDE", "GzzzZZ");
            Check(new BotTokenStore(tokenPath, new ReversingProtector(), _ => environmentToken).Load() == environmentToken, "The environment variable wins");
            File.WriteAllBytes(tokenPath, [1, 2, 3, 4]);
            Check(store.Load() is null && store.HasToken, "A token that can not be decrypted is no token");
            store.Delete();
            Check(!store.HasToken, "The token can be removed");
            var invalid = false;
            try { store.Save("not a token"); } catch (ArgumentException) { invalid = true; }
            Check(invalid && !File.Exists(tokenPath), "Something that is no token is not stored");
            if (OperatingSystem.IsWindows())
            {
                var windowsStore = new BotTokenStore(Path.Combine(folder, "dpapi.token"), environment: _ => null);
                windowsStore.Save(SampleToken);
                Check(windowsStore.Load() == SampleToken
                      && File.ReadAllBytes(Path.Combine(folder, "dpapi.token")).AsSpan().IndexOf(Encoding.UTF8.GetBytes(SampleToken)) < 0,
                    "Windows data protection stores and reads the token");
            }
            else
            {
                var plain = false;
                try { new BotTokenStore(Path.Combine(folder, "x.token"), environment: _ => null).Save(SampleToken); } catch (PlatformNotSupportedException) { plain = true; }
                Check(plain, "Without data protection the token is never written as plain text");
            }

            Check(!BotToken.TryNormalize("abc", out _) && !BotToken.TryNormalize(null, out _) && !BotToken.TryNormalize(SampleToken + " x", out _)
                  && !BotToken.TryNormalize(new string('a', 130), out _), "Bad tokens are refused");
            Check(BotToken.TryNormalize("  " + SampleToken + "\n", out var normalized) && normalized == SampleToken, "Spaces around a token are dropped");
            Check(!BotToken.Fingerprint(SampleToken).Contains("GabcDE") && BotToken.Fingerprint(SampleToken) == BotToken.Fingerprint(SampleToken)
                  && BotToken.Fingerprint(SampleToken) != BotToken.Fingerprint(environmentToken), "The fingerprint tells tokens apart without showing them");
            BotLogRedactor.RegisterSecret("short-secret-value");
            Check(BotLogRedactor.Redact($"login {SampleToken} failed, short-secret-value too") == $"login {BotLogRedactor.Replacement} failed, {BotLogRedactor.Replacement} too",
                "Tokens are removed from texts");
            Check(BotLogRedactor.Redact(null) == string.Empty && BotLogRedactor.Redact("nothing here") == "nothing here", "Other texts stay as they are");

            using (var first = BotTokenLock.TryAcquire(environmentToken))
            {
                Check(first is not null, "The first bot gets the token");
                Check(BotTokenLock.TryAcquire(environmentToken) is null, "A second bot with the same token does not start");
                using var other = BotTokenLock.TryAcquire(SampleToken);
                Check(other is not null, "Another token is free");
            }
            using var again = BotTokenLock.TryAcquire(environmentToken);
            Check(again is not null, "The token is free again when the bot ended");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        // --- guard: wait, limits, lock out
        var now = t0;
        var limiter = new BotRateLimiter();
        var guard = new BotGuard(limiter, () => now);
        Check(guard.Evaluate(policy, Request(interaction: 1), 0).Allowed, "A command passes the gate");
        Check(guard.Evaluate(policy, Request(interaction: 1), 0).Outcome == BotGuardOutcome.Ignored, "The same interaction is handled once");
        Check(guard.Evaluate(policy, Request(interaction: 2), 0) is { Outcome: BotGuardOutcome.Throttled, Throttle: BotThrottle.Cooldown }, "A second command right away has to wait");
        now = now.AddSeconds(4);
        Check(guard.Evaluate(policy, Request(interaction: 3), 0).Allowed, "After the cooldown it passes");
        Check(guard.Evaluate(policy, Request(SuperAdmin, interaction: 4), 0).Allowed, "The cooldown is per user");
        Check(guard.Evaluate(policy, Request(interaction: 5), 0, followUp: true).Allowed, "A follow-up answers a question and does not wait");

        // a stranger is locked out after five refusals, quietly
        for (var i = 0; i < 4; i++)
            Check(guard.Evaluate(policy, Request(Stranger), null) is { Outcome: BotGuardOutcome.Denied, LockedOutNow: false }, "Refusals are counted");
        var fifth = guard.Evaluate(policy, Request(Stranger), null);
        Check(fifth is { Outcome: BotGuardOutcome.Denied, LockedOutNow: true }, "The fifth refusal locks the user out");
        Check(guard.Evaluate(policy, Request(Stranger), null).Outcome == BotGuardOutcome.Ignored, "A locked out user gets no answer");
        Check(guard.Evaluate(policy, Request(Stranger, roles: [Role]), null).Outcome == BotGuardOutcome.Ignored, "Not even with the right role");
        now = now.AddMinutes(9);
        Check(guard.Evaluate(policy, Request(Stranger), null).Outcome == BotGuardOutcome.Ignored, "The lock lasts");
        now = now.AddMinutes(2);
        Check(guard.Evaluate(policy, Request(Stranger), null).Outcome == BotGuardOutcome.Denied, "The lock ends");
        Check(guard.Evaluate(null, Request(), 0).Reason == BotDenyReason.NoPolicy, "Without a policy everything is refused");

        // critical actions: ten per server in ten minutes (by default), counted when confirmed
        now = t0.AddHours(1);
        var criticalLimiter = new BotRateLimiter();
        var criticalGuard = new BotGuard(criticalLimiter, () => now);
        Check(allOnPolicy.CriticalLimit == 10 && allOnPolicy.CriticalWindow == TimeSpan.FromMinutes(10), "By default ten critical actions per server in ten minutes");
        for (var i = 0; i < allOnPolicy.CriticalLimit; i++)
        {
            now = now.AddSeconds(5);
            Check(criticalGuard.Evaluate(allOnPolicy, Request(command: BotCommand.Stop), 0).Allowed, "The request of a critical action passes");
            Check(criticalGuard.Evaluate(allOnPolicy, Request(command: BotCommand.Stop), 0, followUp: true).Allowed, "The confirmation passes and uses the limit");
        }
        now = now.AddSeconds(5);
        Check(criticalGuard.Evaluate(allOnPolicy, Request(command: BotCommand.Stop), 0) is { Outcome: BotGuardOutcome.Throttled, Throttle: BotThrottle.CriticalLimit },
            "The eleventh critical action in ten minutes is refused");
        now = now.AddSeconds(4);
        Check(criticalGuard.Evaluate(allOnPolicy, Request(command: BotCommand.Stop), 1).Allowed, "The limit is per server");
        now = now.AddMinutes(11);
        Check(criticalGuard.Evaluate(allOnPolicy, Request(command: BotCommand.Stop), 0).Allowed, "The limit runs out");
        now = now.AddSeconds(5);
        for (var i = 0; i < 5; i++)
        {
            now = now.AddSeconds(5);
            Check(criticalGuard.Evaluate(allOnPolicy, Request(command: BotCommand.Postpone), 0).Allowed, "Postponing is not a critical action");
        }
        Check(criticalLimiter.TryMarkInteraction(0) && criticalLimiter.TryMarkInteraction(0), "Interaction 0 is never a duplicate");
        for (ulong id = 1; id <= 2500; id++) criticalLimiter.TryMarkInteraction(id);
        Check(criticalLimiter.TryMarkInteraction(1), "Old interactions are forgotten, the list stays small");

        // --- questions of the bot
        var pending = new BotPendingActions();
        var nonce = pending.Create(Operator, BotCommand.Stop, 2, t0);
        Check(nonce.Length == 22 && nonce.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "The code is random text");
        Check(!pending.TryTake(nonce, Stranger, t0, BotPendingKind.Confirm, out _), "Another user can not answer the question");
        Check(pending.TryTake(nonce, Operator, t0.AddSeconds(5), BotPendingKind.Confirm, out var taken) && taken.Command == BotCommand.Stop && taken.Instance == 2, "The right user can");
        Check(!pending.TryTake(nonce, Operator, t0.AddSeconds(6), BotPendingKind.Confirm, out _), "A question is answered once");
        var late = pending.Create(Operator, BotCommand.Stop, 0, t0);
        Check(!pending.TryTake(late, Operator, t0.AddSeconds(31), BotPendingKind.Confirm, out _), "A question runs out");
        var discarded = pending.Create(Operator, BotCommand.Stop, 0, t0);
        Check(!pending.Discard(discarded, Stranger) && pending.Discard(discarded, Operator) && !pending.TryTake(discarded, Operator, t0, BotPendingKind.Confirm, out _), "Cancel drops the question");
        Check(!pending.TryTake(null, Operator, t0, BotPendingKind.Confirm, out _) && !pending.TryTake("nonsense", Operator, t0, BotPendingKind.Confirm, out _), "Unknown codes do nothing");
        Check(pending.Create(Operator, BotCommand.Stop, 0, t0) != pending.Create(Operator, BotCommand.Stop, 0, t0), "Codes are different every time");
        // A code only answers the kind of question it was made for, and a wrong try does not use it up.
        var menuCode = pending.Create(Operator, BotCommand.Stop, 1, t0, kind: BotPendingKind.Menu);
        Check(!pending.TryTake(menuCode, Operator, t0, BotPendingKind.Reason, out _) && !pending.TryTake(menuCode, Operator, t0, BotPendingKind.Confirm, out _),
            "The code of a menu is no confirmation and no window");
        Check(pending.TryTake(menuCode, Operator, t0, BotPendingKind.Menu, out var menuTaken, consume: false) && menuTaken.Instance == 1
              && pending.TryTake(menuCode, Operator, t0.AddSeconds(1), BotPendingKind.Menu, out _), "A menu can be answered again, and the wrong tries did not use it up");
        Check(!pending.TryTake(menuCode, Operator, t0.AddSeconds(2), BotPendingKind.Menu, out _), "...until it was taken");
        var reasonCode = pending.Create(Operator, BotCommand.Stop, 1, t0, kind: BotPendingKind.Reason);
        Check(!pending.TryTake(reasonCode, Operator, t0, BotPendingKind.Confirm, out _) && !pending.TryTake(reasonCode, Operator, t0, BotPendingKind.Menu, out _)
              && pending.TryTake(reasonCode, Operator, t0, BotPendingKind.Reason, out _), "The code of a window answers only the window");
        for (var i = 0; i < 700; i++) pending.Create(Operator, BotCommand.Stop, 0, t0);
        Check(pending.Count <= 500, "Open questions are limited");

        // --- input: warning times, server names, instances
        Check(BotInputRules.DelayChoices(BotCommand.RestartNow).SequenceEqual([0, 1, 5, 10, 15, 30, 60, 120])
              && BotInputRules.DelayChoices(BotCommand.Stop).SequenceEqual([0, 1, 5, 10, 15, 30, 60])
              && BotInputRules.MaxDelayMinutes(BotCommand.RestartNow) == 120 && BotInputRules.MaxDelayMinutes(BotCommand.Stop) == 60,
            "The warning times are those of the dialog in Trebuchet: a restart up to two hours, a stop up to one");
        Check(BotInputRules.TryParseDelayChoice("0", BotCommand.Stop, out var noDelay) && noDelay == 0
              && BotInputRules.TryParseDelayChoice("120", BotCommand.RestartNow, out var twoHours) && twoHours == 120
              && !BotInputRules.TryParseDelayChoice("120", BotCommand.Stop, out _) && !BotInputRules.TryParseDelayChoice("7", BotCommand.Stop, out _)
              && !BotInputRules.TryParseDelayChoice("5", BotCommand.Postpone, out _) && !BotInputRules.TryParseDelayChoice(null, BotCommand.Stop, out _)
              && !BotInputRules.TryParseDelayChoice("", BotCommand.Stop, out _) && !BotInputRules.TryParseDelayChoice("-1", BotCommand.Stop, out _)
              && !BotInputRules.TryParseDelayChoice("1e1", BotCommand.Stop, out _) && !BotInputRules.TryParseDelayChoice("5 ", BotCommand.Stop, out _),
            "Only an offered warning time of that command is accepted");
        Check(BotInputRules.SanitizeName("  Haupt <@123> [Server] `x` |y| *z* ~w~ \\ {a} #b :c:  ") == "Haupt 123 Server x y z w a b c"
              && BotInputRules.SanitizeName(null) == string.Empty && BotInputRules.SanitizeName("<>@") == string.Empty
              && BotInputRules.SanitizeName(new string('n', 100)).Length == BotInputRules.MaxNameLength
              && BotInputRules.SanitizeName("A\nB\u202EC\u200BD") == "A BCD" && BotInputRules.SanitizeName("Haupt_Server (1) - Süd") == "Haupt_Server (1) - Süd",
            "Server names lose everything Discord would turn into a mention, link or format, and stay one short line");
        var namedConfig = ValidConfig();
        namedConfig.ManagedInstances = [2, -1, 100, 0, 2];
        var namedPolicy = PolicyOf(namedConfig);
        Check(namedPolicy.IsManaged(0) && namedPolicy.IsManaged(2) && !namedPolicy.IsManaged(1) && !namedPolicy.IsManaged(100) && !namedPolicy.IsManaged(-1),
            "The policy manages the chosen instances that a Discord option can name, nothing else");
        var noneChosen = ValidConfig();
        noneChosen.ManagedInstances = [];
        Check(PolicyOf(ValidConfig()).ManagedInstances is null && PolicyOf(ValidConfig()).IsManaged(0) && PolicyOf(ValidConfig()).IsManaged(99)
              && !PolicyOf(noneChosen).IsManaged(0),
            "Not chosen yet manages every server, an empty choice none");
        var jsonPath = Path.Combine(folder, "servers.json");
        var serversConfig = DiscordBotConfig.CreateConfig(jsonPath);
        serversConfig.ManagedInstances = [1, 3];
        serversConfig.SaveFile();
        var loadedServers = DiscordBotConfig.LoadStrict(jsonPath);
        Check(loadedServers.ManagedInstances!.SequenceEqual([1, 3]), "The chosen servers survive saving");
        // A file of an older version still holds the names the bot used to keep: they are ignored, the name of the server
        // comes from its save now.
        File.WriteAllText(jsonPath, "{ \"ManagedInstances\": null, \"InstanceNames\": { \"0\": \"Haupt\" } }");
        var nullServers = DiscordBotConfig.LoadStrict(jsonPath);
        Check(nullServers.ManagedInstances is null, "Nothing written (or null) in the file is not chosen yet, old names are ignored");

        // --- the limit of critical actions: raised, files written before keep nothing of the old default
        var limitsPath = Path.Combine(folder, "limits.json");
        bool LimitValid(int limit)
        {
            var c = ValidConfig();
            c.CriticalLimit = limit;
            return BotPolicy.Create(c).IsValid;
        }
        Check(new DiscordBotConfig().CriticalLimit == 10 && LimitValid(50) && !LimitValid(51) && !LimitValid(0), "Ten by default, up to fifty can be set");
        File.WriteAllText(limitsPath, "{ \"CriticalLimit\": 3, \"CriticalWindowMinutes\": 10 }");
        var upgraded = DiscordBotConfig.LoadStrict(limitsPath);
        Check(upgraded.CriticalLimit == 10 && upgraded.LimitsVersion == 2, "A file with the old default of three gets the new default once");
        upgraded.CriticalLimit = 3;
        upgraded.SaveFile();
        var chosen = DiscordBotConfig.LoadStrict(limitsPath);
        Check(chosen.CriticalLimit == 3 && chosen.LimitsVersion == 2, "Three chosen afterwards stays three");
        File.WriteAllText(limitsPath, "{ \"CriticalLimit\": 3, \"CriticalWindowMinutes\": 5 }");
        Check(DiscordBotConfig.LoadStrict(limitsPath).CriticalLimit == 3, "A changed window shows the limits were set on purpose: nothing is touched");
        File.WriteAllText(limitsPath, "{ \"CriticalLimit\": 5 }");
        Check(DiscordBotConfig.LoadStrict(limitsPath).CriticalLimit == 5, "Another value of an old file is a choice and stays");
        File.WriteAllText(limitsPath, "{ }");
        Check(DiscordBotConfig.LoadStrict(limitsPath).CriticalLimit == 10, "A file without the value has the default");

        // --- IDs of buttons
        foreach (var command in new[] { BotCommand.Start, BotCommand.Stop, BotCommand.RestartNow, BotCommand.Postpone, BotCommand.Resume, BotCommand.CheckUpdates })
            foreach (var instance in new[] { 0, 3, 9 })
            {
                var id = BotCustomId.ForAction(command, instance);
                Check(id.Length <= BotCustomId.MaxLength && BotCustomId.TryParse(id, 10, out var kind, out var parsedCommand, out var parsedInstance, out _)
                      && kind == BotCustomIdKind.Action && parsedCommand == command && parsedInstance == instance, $"Button ID {id} reads back");
            }
        Check(!BotCustomId.TryParse(BotCustomId.ForAction(BotCommand.Stop, 5), 5, out _, out _, out _, out _), "An instance that does not exist is refused");
        foreach (var bad in new[] { null, "", "tb1", "tb2:a:stop:0", "TB1:a:stop:0", "tb1:a:Stop:0", "tb1:a:format:0", "tb1:a:stop:-1", "tb1:a:stop:0;x",
                     "tb1:a:stop:0 ", "tb1:a:stop:0\n", "tb1:ok:" + new string('a', 22) + "\n", "tb1:a:stop:", "tb1:a:stop:1000", "tb1:ok:short", "tb1:ok:" + new string('a', 23), "tb1:xx:" + new string('a', 22),
                     "tb1:a:status:0", "tb1:a:update:0", new string('a', 101) })
            Check(!BotCustomId.TryParse(bad, 10, out _, out _, out _, out _), $"'{bad}' is no button ID");
        var code = pending.Create(Operator, BotCommand.Stop, 0, t0);
        Check(BotCustomId.TryParse(BotCustomId.ForConfirm(code), 1, out var confirmKind, out _, out _, out var confirmCode) && confirmKind == BotCustomIdKind.Confirm && confirmCode == code
              && BotCustomId.TryParse(BotCustomId.ForCancel(code), 1, out var cancelKind, out _, out _, out _) && cancelKind == BotCustomIdKind.Cancel
              && BotCustomId.TryParse(BotCustomId.ForPostponeSelect(code), 1, out var selectKind, out _, out _, out _) && selectKind == BotCustomIdKind.PostponeSelect,
            "Confirm, cancel and selection IDs read back");
        var threwStatus = false;
        try { BotCustomId.ForAction(BotCommand.Status, 0); } catch (ArgumentException) { threwStatus = true; }
        Check(threwStatus, "Only commands with a button have a button ID");

        // --- inputs
        Check(BotInputRules.TryParseInstance(0, 2, out var instanceNumber) && instanceNumber == 0 && BotInputRules.TryParseInstance(1, 2, out _)
              && !BotInputRules.TryParseInstance(2, 2, out _) && !BotInputRules.TryParseInstance(-1, 2, out _) && !BotInputRules.TryParseInstance(long.MaxValue, 2, out _),
            "Only instances that exist");
        Check(BotInputRules.TryParseMinutes(1, policy, out var minutes) && minutes == 1 && BotInputRules.TryParseMinutes(360, policy, out _)
              && !BotInputRules.TryParseMinutes(0, policy, out _) && !BotInputRules.TryParseMinutes(361, policy, out _) && !BotInputRules.TryParseMinutes(-5, policy, out _)
              && !BotInputRules.TryParseMinutes(long.MaxValue, policy, out _), "Minutes between 1 and the maximum");
        Check(BotInputRules.TryParsePostponeChoice("30", policy, out minutes) && minutes == 30 && !BotInputRules.TryParsePostponeChoice("31", policy, out _)
              && !BotInputRules.TryParsePostponeChoice("-30", policy, out _) && !BotInputRules.TryParsePostponeChoice("3 0", policy, out _)
              && !BotInputRules.TryParsePostponeChoice("", policy, out _) && !BotInputRules.TryParsePostponeChoice(null, policy, out _)
              && !BotInputRules.TryParsePostponeChoice("99999999999", policy, out _), "Only the offered minutes can be picked");
        Check(BotInputRules.SanitizeReason("Event {servername}\r\nstarts") == "Event servername starts", "Macros and line breaks are removed from a reason");
        Check(BotInputRules.SanitizeReason("a‮b​c\u0000d") == "abcd", "Text direction, zero width and control characters are removed");
        Check(BotInputRules.SanitizeReason("   a   b   ") == "a b", "Spaces are tidied");
        Check(BotInputRules.SanitizeReason(null) == string.Empty && BotInputRules.SanitizeReason("{}") == string.Empty, "Nothing stays nothing");
        // A reason that goes on to the launcher (its macros: warnings, webhook, RCon, REST, arguments of programs) is plain words only.
        void Sanitized(string? input, string expected, string message)
        {
            var actual = BotInputRules.SanitizeLauncherReason(input);
            Check(actual == expected, actual == expected ? message : $"{message}: expected \"{expected}\", got \"{actual}\"");
        }
        Sanitized("x\" & calc.exe & \"", "x calc.exe", "Quotes and the & of a command line are dropped");
        Sanitized("@everyone <@&123456789012345678> | $(whoami) `id` ; %PATH% ^ ~ #", "everyone 123456789012345678 (whoami) id PATH",
            "Mentions, pipes, shell and batch syntax and markdown are dropped");
        Sanitized("\", \"admin\": true, [click](http://evil.example)", ", admin: true, click(http:evil.example)",
            "JSON quotes and the brackets of a masked link are dropped");
        Sanitized("Wartung am Abend (Mod-Update), bitte nicht stören: Jörg ist ß-dran?", "Wartung am Abend (Mod-Update), bitte nicht stören: Jörg ist ß-dran?",
            "Normal words with umlauts and light punctuation stay");
        Sanitized("{servername}\r\nzwei   drei\u202e\u200b", "servername zwei drei", "Macros, line breaks and invisible characters go");
        Check(BotInputRules.SanitizeLauncherReason(new string('x', 500)).Length == BotInputRules.MaxReasonLength && BotInputRules.SanitizeLauncherReason(null).Length == 0
              && BotInputRules.SanitizeLauncherReason("   ").Length == 0 && BotInputRules.SanitizeLauncherReason("&|;").Length == 0, "Long and empty reasons"); 
        var longReason = BotInputRules.SanitizeReason(new string('x', 500));
        Check(longReason.Length == BotInputRules.MaxReasonLength, "A reason is cut to 100 characters");
        var surrogates = BotInputRules.SanitizeReason(new string('x', BotInputRules.MaxReasonLength - 1) + "😀😀");
        Check(surrogates.Length <= BotInputRules.MaxReasonLength && !char.IsHighSurrogate(surrogates[^1]), "A cut never splits a character");
        Check(!BotInputRules.SanitizeForLog("a\nb\r\nc").Contains('\n') && BotInputRules.SanitizeForLog(new string('y', 500)).Length == BotInputRules.MaxLogTextLength,
            "Texts for the log are one short line");

        // --- the status message
        var online = new BotServerStatus(1, "Exiled Lands", BotServerState.Online, 12, 40, t0.AddHours(-3), BotPendingAction.None, null, null);
        var defaults = BotStatusMessageBuilder.Build(online, BotTexts.German, t0);
        Check(defaults.Color == BotStatusMessageBuilder.Green
              && defaults.Buttons.Select(b => b.Command).SequenceEqual([BotCommand.RestartNow, BotCommand.Stop, BotCommand.CheckUpdates])
              && defaults.Buttons.All(b => !b.Disabled),
            "A healthy server is green with restart, stop and update check buttons, the command switches do not hide them");
        Check(defaults.UpdatedUtc == t0 && defaults.Footer == "Aktualisiert", "The message carries the time of the check");
        Check(defaults.Title.Contains("Exiled Lands") && defaults.Fields.Any(f => f.Name == "Spieler" && f.Value == "12 / 40")
              && defaults.Fields.Any(f => f.Name == "Läuft" && f.Value.StartsWith("<t:")), "Name, players and run time are shown");
        var unix = new DateTimeOffset(t0.AddHours(-3)).ToUnixTimeSeconds();
        Check(defaults.Fields.Any(f => f.Value == $"<t:{unix}:R>"), "Times are Discord timestamps");

        foreach (var (pendingAction, label) in new[]
                 {
                     (BotPendingAction.Restart, "Neustart verschieben"), (BotPendingAction.Stop, "Stopp verschieben"), (BotPendingAction.Update, "Update verschieben")
                 })
        {
            var planned = BotStatusMessageBuilder.Build(online with { Pending = pendingAction, PendingAtUtc = t0.AddMinutes(10) }, BotTexts.German, t0);
            var postponeButton = planned.Buttons.SingleOrDefault(b => b.Command == BotCommand.Postpone);
            Check(planned.Buttons.Count == 4 && postponeButton is { Style: BotButtonStyle.Secondary } && postponeButton.Label == label
                  && planned.Color == BotStatusMessageBuilder.Yellow && planned.Fields.Any(f => f.Name == "Geplant"), $"A pending {pendingAction} offers '{label}'");
            Check(postponeButton is not null && BotCustomId.TryParse(postponeButton.CustomId, 4, out _, out var buttonCommand, out var buttonInstance, out _)
                  && buttonCommand == BotCommand.Postpone && buttonInstance == 1, "The button belongs to its server and command");
        }
        Check(BotStatusMessageBuilder.Build(online with { Pending = BotPendingAction.Restart }, BotTexts.English, t0).Buttons.Single(b => b.Command == BotCommand.Postpone).Label == "Postpone restart",
            "The texts follow the language");

        // A restart that is pending but put off by a pause shows as postponed (blue), with both buttons.
        var putOff = BotStatusMessageBuilder.Build(online with { Pending = BotPendingAction.Restart, PausedUntilUtc = t0.AddHours(1) }, BotTexts.German, t0);
        Check(putOff.Color == BotStatusMessageBuilder.Blue && putOff.Buttons.Select(b => b.Command).SequenceEqual([BotCommand.RestartNow, BotCommand.Stop, BotCommand.Postpone, BotCommand.Resume, BotCommand.CheckUpdates]),
            "A pending restart that is postponed is blue, it can be postponed further or released");

        // The message is the same for everybody (and the super admin may do everything): no switch and no policy decides
        // which buttons it shows, the click is checked like every other request.
        Check(BotStatusMessageBuilder.Build(online with { Pending = BotPendingAction.Restart }, BotTexts.German, t0).Buttons.Any(b => b.Command == BotCommand.Postpone),
            "The postpone button shows while something is pending");

        var paused = online with { PausedUntilUtc = t0.AddHours(1) };
        var pausedMessage = BotStatusMessageBuilder.Build(paused, BotTexts.German, t0);
        Check(pausedMessage.Color == BotStatusMessageBuilder.Blue && pausedMessage.Buttons.Select(b => b.Command).SequenceEqual([BotCommand.RestartNow, BotCommand.Stop, BotCommand.Resume, BotCommand.CheckUpdates])
              && pausedMessage.Fields.Any(f => f.Name == "Verschoben bis"), "A postponed server is blue and can be released");
        Check(BotStatusMessageBuilder.Build(paused with { PausedUntilUtc = t0.AddMinutes(-1) }, BotTexts.German, t0).Buttons.All(b => b.Command != BotCommand.Resume),
            "A postponement that is over has no release button");

        var full = BotStatusMessageBuilder.Build(online with { Pending = BotPendingAction.Restart }, BotTexts.German, t0);
        Check(full.Buttons.Select(b => (b.Command, b.Style)).SequenceEqual(
                  [(BotCommand.RestartNow, BotButtonStyle.Primary), (BotCommand.Stop, BotButtonStyle.Danger), (BotCommand.Postpone, BotButtonStyle.Secondary),
                   (BotCommand.CheckUpdates, BotButtonStyle.Secondary)]),
            "A running server: restart blue, stop red, postpone and update check grey");
        var stopped = BotStatusMessageBuilder.Build(online with { State = BotServerState.Stopped, Players = 0, StartedUtc = null }, BotTexts.German, t0);
        Check(stopped.Color == BotStatusMessageBuilder.Red && stopped.Buttons[0] is { Command: BotCommand.Start, Style: BotButtonStyle.Success, Disabled: false }
              && stopped.Buttons.Count == 2 && !stopped.Fields.Any(f => f.Name == "Spieler"), "A stopped server is red with a green start button");
        var crashed = BotStatusMessageBuilder.Build(online with { State = BotServerState.Crashed }, BotTexts.German, t0);
        Check(crashed.Color == BotStatusMessageBuilder.Red && crashed.Buttons[0].Command == BotCommand.Start, "A crashed server can be started");
        foreach (var transition in new[] { BotServerState.Starting, BotServerState.Stopping })
        {
            var message = BotStatusMessageBuilder.Build(online with { State = transition }, BotTexts.German, t0);
            Check(message.Color == BotStatusMessageBuilder.Yellow
                  && (transition == BotServerState.Stopping
                      ? message.Buttons.Count == 2 && message.Buttons[0] is { Command: BotCommand.Start, Disabled: true } && !message.Buttons[1].Disabled
                      : message.Buttons.Count == 3 && message.Buttons.All(b => !b.Disabled)),
                $"{transition}: yellow, the buttons stay and are greyed out while the server is going down");
        }
        Check(allOnPolicy.IsCommandEnabled(BotCommand.Start) && full.Buttons.Count <= 5, "At most five buttons fit in a row");
        Check(!full.Fields.Concat(stopped.Fields).Any(f => f.Value.Contains(folder) || f.Value.Contains("password", StringComparison.OrdinalIgnoreCase)), "No paths or passwords in the message");

        return count;
    }
}
