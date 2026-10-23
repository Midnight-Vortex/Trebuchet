using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using TrebuchetLib.DiscordBot;

namespace Trebuchet.ViewModels.DiscordBot;

/// <summary>Outcome of an ID typed into the panel (a user, a role, the super admin).</summary>
public enum BotIdInputResult
{
    Added,
    /// <summary>Not 17 to 20 digits.</summary>
    Invalid,
    /// <summary>Already on the list (or the super admin itself).</summary>
    Duplicate,
    /// <summary>A role with the ID of the server: the role @everyone that every member has.</summary>
    EveryoneRole,
    /// <summary>The bot token was pasted into an ID field: the field is cleared, the token never shown.</summary>
    LooksLikeToken
}

/// <summary>Range of a limit, the same that <see cref="BotPolicy"/> accepts (a check makes sure they agree).</summary>
public sealed record BotLimitRange(int Min, int Max);

public static class DiscordBotLimits
{
    public static readonly BotLimitRange Cooldown = new(1, 300);
    public static readonly BotLimitRange CriticalLimit = new(1, 20);
    public static readonly BotLimitRange CriticalWindow = new(1, 120);
    public static readonly BotLimitRange LockoutAttempts = new(1, 50);
    public static readonly BotLimitRange LockoutMinutes = new(1, DiscordBotConfig.HardMaxPauseMinutes);
    public static readonly BotLimitRange ActionDelay = new(0, 60);
    public static readonly BotLimitRange MaxPause = new(1, DiscordBotConfig.HardMaxPauseMinutes);
}

/// <summary>
/// The copy of the bot settings the panel edits. Nothing reaches the settings file before <see cref="Save"/>, and
/// <see cref="Save"/> writes nothing that <see cref="BotPolicy"/> does not accept. The admin channel is not edited
/// here: Discord sets it, the panel can only widen it back to every channel (<see cref="ClearAdminChannel"/>).
/// No UI types: tested without Avalonia.
/// </summary>
public sealed class DiscordBotSettingsEditor
{
    // Field names as BotPolicy uses them in the details of its issues.
    public const string GuildIdField = nameof(DiscordBotConfig.GuildId);
    public const string AuditChannelIdField = nameof(DiscordBotConfig.AuditChannelId);

    private static readonly char[] ChoiceSeparators = [',', ';', ' ', '\t'];

    private readonly List<ulong> _users = [];
    private readonly List<ulong> _roles = [];
    private readonly HashSet<BotCommand> _commands = [];
    private List<ulong> _adminChannels = [];
    private Snapshot _loaded;
    private string _guildIdText = string.Empty;
    private string _auditChannelIdText = string.Empty;

    public DiscordBotSettingsEditor()
    {
        _loaded = Capture();
    }

    public bool AutoStart { get; set; }
    public string Language { get; set; } = "de";
    public ulong SuperAdminUserId { get; private set; }
    public int CooldownSeconds { get; set; }
    public int CriticalLimit { get; set; }
    public int CriticalWindowMinutes { get; set; }
    public int LockoutAttempts { get; set; }
    public int LockoutMinutes { get; set; }
    public int MaxPauseMinutes { get; set; }
    public int ActionDelayMinutes { get; set; }
    /// <summary>The minutes offered when postponing, as typed: "15, 30, 60".</summary>
    public string PostponeChoicesText { get; set; } = string.Empty;

    /// <summary>The server ID as typed. Empty: none.</summary>
    public string GuildIdText => _guildIdText;
    /// <summary>The audit channel ID as typed. Empty: none.</summary>
    public string AuditChannelIdText => _auditChannelIdText;

    /// <summary>Allowed users besides the super admin (the super admin may be on it too, it can not be removed).</summary>
    public IReadOnlyList<ulong> UserIds => _users;
    public IReadOnlyList<ulong> RoleIds => _roles;
    /// <summary>The channels the bot answers in, as the settings hold them. Empty: every channel.</summary>
    public IReadOnlyList<ulong> AdminChannelIds => _adminChannels;
    /// <summary>The admin channel is dropped (every channel again) with the next save.</summary>
    public bool ClearAdminChannelPending { get; private set; }

    /// <summary>The edits differ from what the settings file holds.</summary>
    public bool IsDirty => Capture() != _loaded;

    /// <summary>Takes the values of the settings, dropping every edit.</summary>
    public void Load(DiscordBotConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        AutoStart = config.AutoStart;
        Language = config.Language;
        _guildIdText = FormatId(config.GuildId);
        SuperAdminUserId = config.SuperAdminUserId;
        _auditChannelIdText = FormatId(config.AuditChannelId);
        _users.Clear();
        _users.AddRange(config.AllowedUserIds.Distinct());
        _roles.Clear();
        _roles.AddRange(config.AllowedRoleIds.Distinct());
        _commands.Clear();
        foreach (var command in Enum.GetValues<BotCommand>().Where(config.IsEnabled))
            _commands.Add(command);
        CooldownSeconds = config.CooldownSeconds;
        CriticalLimit = config.CriticalLimit;
        CriticalWindowMinutes = config.CriticalWindowMinutes;
        LockoutAttempts = config.LockoutAttempts;
        LockoutMinutes = config.LockoutMinutes;
        MaxPauseMinutes = config.MaxPauseMinutes;
        ActionDelayMinutes = config.ActionDelayMinutes;
        PostponeChoicesText = string.Join(@", ", config.PostponeChoices);
        ClearAdminChannelPending = false;
        _adminChannels = config.AllowedChannelIds.ToList();
        _loaded = Capture();
    }

    /// <summary>The bot may have set the admin channel meanwhile: show it, keep every other edit.</summary>
    public void RefreshAdminChannels(DiscordBotConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _adminChannels = config.AllowedChannelIds.ToList();
    }

    /// <returns>False when the text looks like the bot token: it is not kept.</returns>
    public bool SetGuildIdText(string? text) => SetIdText(ref _guildIdText, text);

    /// <returns>False when the text looks like the bot token: it is not kept.</returns>
    public bool SetAuditChannelIdText(string? text) => SetIdText(ref _auditChannelIdText, text);

    public bool IsCommandEnabled(BotCommand command) => _commands.Contains(command);

    public void SetCommandEnabled(BotCommand command, bool enabled)
    {
        if (!Enum.IsDefined(command)) return;
        if (enabled) _commands.Add(command);
        else _commands.Remove(command);
    }

    /// <summary>Back to every channel. Discord commands can only narrow the channels, this is the only way back.</summary>
    public void ClearAdminChannel()
    {
        if (_adminChannels.Count > 0) ClearAdminChannelPending = true;
    }

    public BotIdInputResult TryAddUser(string? text)
    {
        var result = ParseId(text, out var id);
        if (result != BotIdInputResult.Added) return result;
        if (id == SuperAdminUserId || _users.Contains(id)) return BotIdInputResult.Duplicate;
        _users.Add(id);
        return BotIdInputResult.Added;
    }

    /// <returns>False for the super admin: that user can not be removed (like <see cref="DiscordBotConfig.RemoveUser"/>).</returns>
    public bool RemoveUser(ulong id) => id != SuperAdminUserId && _users.Remove(id);

    public BotIdInputResult TryAddRole(string? text)
    {
        var result = ParseId(text, out var id);
        if (result != BotIdInputResult.Added) return result;
        // The role @everyone has the ID of the server and every member has it.
        if (BotId.TryParse(_guildIdText.Trim(), out var guild) && id == guild) return BotIdInputResult.EveryoneRole;
        if (_roles.Contains(id)) return BotIdInputResult.Duplicate;
        _roles.Add(id);
        return BotIdInputResult.Added;
    }

    public bool RemoveRole(ulong id) => _roles.Remove(id);

    /// <summary>Checks an ID for the super admin without setting it (the panel asks for a confirmation first).</summary>
    public BotIdInputResult CheckSuperAdmin(string? text, out ulong id)
    {
        var result = ParseId(text, out id);
        if (result == BotIdInputResult.Added && id == SuperAdminUserId) return BotIdInputResult.Duplicate;
        return result;
    }

    /// <summary>The super admin can only be replaced, never removed.</summary>
    public bool SetSuperAdmin(ulong id)
    {
        if (!BotId.IsValid(id)) return false;
        SuperAdminUserId = id;
        return true;
    }

    /// <summary>Digits only, 17 to 20 of them. A pasted token is recognized so the panel can clear it at once.</summary>
    public static BotIdInputResult ParseId(string? text, out ulong id)
    {
        id = 0;
        var trimmed = text?.Trim();
        if (BotToken.TryNormalize(trimmed, out _)) return BotIdInputResult.LooksLikeToken;
        return BotId.TryParse(trimmed, out id) ? BotIdInputResult.Added : BotIdInputResult.Invalid;
    }

    /// <summary>Whole minutes separated by commas or spaces. False when anything else is in the text.</summary>
    public static bool TryParseChoices(string? text, out List<int> choices)
    {
        choices = [];
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (var part in text.Split(ChoiceSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > 5 || !part.All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) return false;
            choices.Add(minutes);
        }
        return true;
    }

    /// <summary>The settings as they would be saved, and everything that is wrong with them.</summary>
    public DiscordBotSettingsCheck Validate(DiscordBotConfig current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var candidate = Clone(current);
        var issues = new List<BotConfigIssue>();

        var guildTyped = !string.IsNullOrWhiteSpace(_guildIdText);
        var guildValid = BotId.TryParse(_guildIdText.Trim(), out var guild);
        // Only the name of the field: what was typed is not repeated (it could be a pasted secret).
        if (guildTyped && !guildValid) issues.Add(new BotConfigIssue(BotConfigIssueKind.InvalidId, GuildIdField));
        var auditValid = BotId.TryParse(_auditChannelIdText.Trim(), out var audit);
        if (!string.IsNullOrWhiteSpace(_auditChannelIdText) && !auditValid)
            issues.Add(new BotConfigIssue(BotConfigIssueKind.InvalidId, AuditChannelIdField));
        var choicesValid = TryParseChoices(PostponeChoicesText, out var choices);
        if (!choicesValid) issues.Add(new BotConfigIssue(BotConfigIssueKind.InvalidPostponeChoices));

        candidate.AutoStart = AutoStart;
        candidate.Language = Language;
        candidate.GuildId = guildValid ? guild : 0;
        candidate.SuperAdminUserId = SuperAdminUserId;
        candidate.AuditChannelId = auditValid ? audit : 0;
        candidate.AllowedUserIds = _users.ToList();
        candidate.AllowedRoleIds = _roles.ToList();
        candidate.EnabledCommands = Enum.GetValues<BotCommand>().Where(_commands.Contains).Select(x => x.ToString()).ToList();
        candidate.CooldownSeconds = CooldownSeconds;
        candidate.CriticalLimit = CriticalLimit;
        candidate.CriticalWindowMinutes = CriticalWindowMinutes;
        candidate.LockoutAttempts = LockoutAttempts;
        candidate.LockoutMinutes = LockoutMinutes;
        candidate.MaxPauseMinutes = MaxPauseMinutes;
        candidate.ActionDelayMinutes = ActionDelayMinutes;
        if (choicesValid) candidate.PostponeChoices = choices.Distinct().Order().ToList();
        if (ClearAdminChannelPending) candidate.AllowedChannelIds = [];

        foreach (var issue in BotPolicy.Create(candidate).Issues)
        {
            // Already reported as not readable above: "missing" would only repeat it.
            if (issue.Kind == BotConfigIssueKind.GuildMissing && guildTyped) continue;
            if (issue.Kind == BotConfigIssueKind.InvalidPostponeChoices && !choicesValid) continue;
            issues.Add(issue);
        }
        return new DiscordBotSettingsCheck(candidate, issues);
    }

    /// <summary>
    /// Writes the edits into the settings and saves the file, only when everything is valid. On any issue or error
    /// the settings stay as they were.
    /// </summary>
    /// <returns>The issues that prevented the save; empty when it was saved.</returns>
    public IReadOnlyList<BotConfigIssue> Save(DiscordBotConfig current)
    {
        // The bot changes and saves the same settings object from its own threads (admin channel, status messages) under
        // this lock: a check, copy and save here must not interleave with it.
        lock (current)
        {
            var check = Validate(current);
            if (check.Issues.Count > 0) return check.Issues;

            // The admin channel and the status messages belong to the old server: on another server they only refuse
            // every command for the wrong channel.
            var guildChanged = current.GuildId != 0 && check.Candidate.GuildId != current.GuildId;
            if (guildChanged) check.Candidate.AllowedChannelIds = [];

            var before = Clone(current);
            try
            {
                CopyEditable(check.Candidate, current, ClearAdminChannelPending || guildChanged);
                if (guildChanged) current.StatusMessageIds = [];
                // The bot may have changed the settings since the check (admin channel): check what is really saved.
                var final = BotPolicy.Create(current);
                if (!final.IsValid)
                {
                    CopyEditable(before, current, true);
                    current.StatusMessageIds = before.StatusMessageIds;
                    return final.Issues;
                }
                current.SaveFile();
            }
            catch
            {
                CopyEditable(before, current, true);
                current.StatusMessageIds = before.StatusMessageIds;
                throw;
            }
            Load(current);
            return [];
        }
    }

    private static void CopyEditable(DiscordBotConfig from, DiscordBotConfig to, bool channels)
    {
        to.AutoStart = from.AutoStart;
        to.Language = from.Language;
        to.GuildId = from.GuildId;
        to.SuperAdminUserId = from.SuperAdminUserId;
        to.AuditChannelId = from.AuditChannelId;
        to.AllowedUserIds = from.AllowedUserIds.ToList();
        to.AllowedRoleIds = from.AllowedRoleIds.ToList();
        to.EnabledCommands = from.EnabledCommands.ToList();
        to.CooldownSeconds = from.CooldownSeconds;
        to.CriticalLimit = from.CriticalLimit;
        to.CriticalWindowMinutes = from.CriticalWindowMinutes;
        to.LockoutAttempts = from.LockoutAttempts;
        to.LockoutMinutes = from.LockoutMinutes;
        to.MaxPauseMinutes = from.MaxPauseMinutes;
        to.ActionDelayMinutes = from.ActionDelayMinutes;
        to.PostponeChoices = from.PostponeChoices.ToList();
        // The status messages belong to the bot; the channels only change when the panel widened them.
        if (channels) to.AllowedChannelIds = from.AllowedChannelIds.ToList();
    }

    /// <summary>A detached copy (no file): saving it by mistake fails instead of overwriting the settings.</summary>
    private static DiscordBotConfig Clone(DiscordBotConfig config)
    {
        var json = JsonSerializer.Serialize(config, DiscordBotConfig._jsonOptions);
        return JsonSerializer.Deserialize<DiscordBotConfig>(json, DiscordBotConfig._jsonOptions)
               ?? throw new InvalidOperationException(@"The bot settings could not be copied");
    }

    private static bool SetIdText(ref string field, string? text)
    {
        if (BotToken.TryNormalize(text?.Trim(), out _))
        {
            field = string.Empty;
            return false;
        }
        field = text ?? string.Empty;
        return true;
    }

    private static string FormatId(ulong id) => id == 0 ? string.Empty : id.ToString(CultureInfo.InvariantCulture);

    private Snapshot Capture() => new(
        AutoStart, Language, _guildIdText.Trim(), SuperAdminUserId, _auditChannelIdText.Trim(),
        string.Join(',', _users), string.Join(',', _roles),
        string.Join(',', _commands.Order()),
        CooldownSeconds, CriticalLimit, CriticalWindowMinutes, LockoutAttempts, LockoutMinutes, MaxPauseMinutes,
        ActionDelayMinutes, NormalizeChoices(PostponeChoicesText), ClearAdminChannelPending);

    private static string NormalizeChoices(string text) =>
        TryParseChoices(text, out var choices) ? string.Join(',', choices) : text.Trim();

    // Lists as text: records compare them by value then.
    private sealed record Snapshot(
        bool AutoStart, string Language, string Guild, ulong SuperAdmin, string AuditChannel,
        string Users, string Roles, string Commands,
        int Cooldown, int CriticalLimit, int CriticalWindow, int LockoutAttempts, int LockoutMinutes, int MaxPause,
        int ActionDelay, string Choices, bool ClearChannel);
}

/// <param name="Candidate">The settings as they would be saved (not connected to the file).</param>
public sealed record DiscordBotSettingsCheck(DiscordBotConfig Candidate, IReadOnlyList<BotConfigIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}
