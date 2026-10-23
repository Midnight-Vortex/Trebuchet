using System.Collections.Frozen;

namespace TrebuchetLib.DiscordBot;

public enum BotConfigIssueKind
{
    GuildMissing,
    SuperAdminMissing,
    InvalidId,
    NoRoles,
    EveryoneRole,
    InvalidLimit,
    InvalidPostponeChoices,
    UnknownCommand
}

public sealed record BotConfigIssue(BotConfigIssueKind Kind, string Detail = "");

/// <summary>Result of checking the settings. A policy only exists for settings without any issue.</summary>
public sealed record BotPolicyResult(BotPolicy? Policy, IReadOnlyList<BotConfigIssue> Issues)
{
    public bool IsValid => Policy is not null;
}

/// <summary>
/// An unchanging copy of the checked settings. The authorization only works on this copy: changes in the settings
/// apply when a new copy replaces it, and settings that are not valid give no copy at all (nobody is allowed anything).
/// </summary>
public sealed class BotPolicy
{
    private BotPolicy(DiscordBotConfig config, IReadOnlyList<int> postponeChoices)
    {
        GuildId = config.GuildId;
        SuperAdminUserId = config.SuperAdminUserId;
        UserIds = config.AllowedUserIds.Append(config.SuperAdminUserId).ToFrozenSet();
        RoleIds = config.AllowedRoleIds.ToFrozenSet();
        ChannelIds = config.AllowedChannelIds.ToFrozenSet();
        AuditChannelId = config.AuditChannelId;
        EnabledCommands = Enum.GetValues<BotCommand>().Where(config.IsEnabled).ToFrozenSet();
        Cooldown = TimeSpan.FromSeconds(config.CooldownSeconds);
        CriticalLimit = config.CriticalLimit;
        CriticalWindow = TimeSpan.FromMinutes(config.CriticalWindowMinutes);
        LockoutAttempts = config.LockoutAttempts;
        LockoutDuration = TimeSpan.FromMinutes(config.LockoutMinutes);
        MaxPause = TimeSpan.FromMinutes(config.MaxPauseMinutes);
        ActionDelay = TimeSpan.FromMinutes(config.ActionDelayMinutes);
        PostponeChoices = postponeChoices;
        Language = config.Language;
        // Only instances a Discord option or button can name; everything else is left out (never more than was chosen).
        ManagedInstances = config.ManagedInstances?
            .Where(x => x >= 0 && x <= BotInputRules.MaxInstance).ToFrozenSet();
    }

    public ulong GuildId { get; }
    public ulong SuperAdminUserId { get; }
    /// <summary>All users who may use the bot (with a role), the super admin included.</summary>
    public IReadOnlySet<ulong> UserIds { get; }
    public IReadOnlySet<ulong> RoleIds { get; }
    /// <summary>Empty: every channel.</summary>
    public IReadOnlySet<ulong> ChannelIds { get; }
    public ulong AuditChannelId { get; }
    public IReadOnlySet<BotCommand> EnabledCommands { get; }
    public TimeSpan Cooldown { get; }
    public int CriticalLimit { get; }
    public TimeSpan CriticalWindow { get; }
    public int LockoutAttempts { get; }
    public TimeSpan LockoutDuration { get; }
    public TimeSpan MaxPause { get; }
    /// <summary>Warning time before a restart or stop started from Discord.</summary>
    public TimeSpan ActionDelay { get; }
    public IReadOnlyList<int> PostponeChoices { get; }
    public string Language { get; }
    /// <summary>The instances the bot manages, null: all of them.</summary>
    public IReadOnlySet<int>? ManagedInstances { get; }

    public bool IsCommandEnabled(BotCommand command) => EnabledCommands.Contains(command);

    public bool IsManaged(int instance) => ManagedInstances is null || ManagedInstances.Contains(instance);

    public static BotPolicyResult Create(DiscordBotConfig? config)
    {
        try
        {
            return CreateCore(config);
        }
        catch (Exception ex)
        {
            // Settings that can not even be read are settings that allow nothing.
            return new BotPolicyResult(null, [new BotConfigIssue(BotConfigIssueKind.InvalidId, "settings can not be read: " + ex.GetType().Name)]);
        }
    }

    private static BotPolicyResult CreateCore(DiscordBotConfig? config)
    {
        if (config is null) return new BotPolicyResult(null, [new BotConfigIssue(BotConfigIssueKind.GuildMissing)]);
        var issues = new List<BotConfigIssue>();

        if (config.GuildId == 0) issues.Add(new BotConfigIssue(BotConfigIssueKind.GuildMissing));
        else CheckId(issues, config.GuildId, "GuildId");

        if (config.SuperAdminUserId == 0) issues.Add(new BotConfigIssue(BotConfigIssueKind.SuperAdminMissing));
        else CheckId(issues, config.SuperAdminUserId, "SuperAdminUserId");

        foreach (var id in config.AllowedUserIds) CheckId(issues, id, "AllowedUserIds");
        foreach (var id in config.AllowedChannelIds) CheckId(issues, id, "AllowedChannelIds");
        if (config.AuditChannelId != 0) CheckId(issues, config.AuditChannelId, "AuditChannelId");

        if (config.AllowedRoleIds.Count == 0) issues.Add(new BotConfigIssue(BotConfigIssueKind.NoRoles));
        foreach (var id in config.AllowedRoleIds)
        {
            CheckId(issues, id, "AllowedRoleIds");
            // The role @everyone has the ID of the server and every member has it: it would allow everyone.
            if (id == config.GuildId) issues.Add(new BotConfigIssue(BotConfigIssueKind.EveryoneRole, id.ToString()));
        }

        CheckLimit(issues, nameof(config.CooldownSeconds), config.CooldownSeconds, 1, 300);
        CheckLimit(issues, nameof(config.CriticalLimit), config.CriticalLimit, 1, 20);
        CheckLimit(issues, nameof(config.CriticalWindowMinutes), config.CriticalWindowMinutes, 1, 120);
        CheckLimit(issues, nameof(config.LockoutAttempts), config.LockoutAttempts, 1, 50);
        CheckLimit(issues, nameof(config.LockoutMinutes), config.LockoutMinutes, 1, DiscordBotConfig.HardMaxPauseMinutes);
        CheckLimit(issues, nameof(config.ActionDelayMinutes), config.ActionDelayMinutes, 0, 60);
        CheckLimit(issues, nameof(config.MaxPauseMinutes), config.MaxPauseMinutes, 1, DiscordBotConfig.HardMaxPauseMinutes);

        var choices = config.PostponeChoices.Distinct().Order().ToList();
        if (choices.Count == 0 || choices.Count > DiscordBotConfig.MaxPostponeChoices
            || choices.Any(x => x < 1 || x > config.MaxPauseMinutes))
            issues.Add(new BotConfigIssue(BotConfigIssueKind.InvalidPostponeChoices, string.Join(",", config.PostponeChoices)));

        foreach (var name in config.EnabledCommands)
            if (!Enum.TryParse<BotCommand>(name, out var parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != name)
                issues.Add(new BotConfigIssue(BotConfigIssueKind.UnknownCommand, name));

        return issues.Count > 0
            ? new BotPolicyResult(null, issues)
            : new BotPolicyResult(new BotPolicy(config, choices), []);
    }

    private static void CheckId(List<BotConfigIssue> issues, ulong id, string field)
    {
        if (!BotId.IsValid(id)) issues.Add(new BotConfigIssue(BotConfigIssueKind.InvalidId, $"{field}: {id}"));
    }

    private static void CheckLimit(List<BotConfigIssue> issues, string field, int value, int min, int max)
    {
        if (value < min || value > max)
            issues.Add(new BotConfigIssue(BotConfigIssueKind.InvalidLimit, $"{field}: {value} ({min}-{max})"));
    }
}
