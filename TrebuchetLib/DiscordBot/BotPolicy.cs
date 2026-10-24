using System.Collections.Frozen;
using System.Globalization;

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
    UnknownCommand,
    /// <summary>A link that is not an https link (the picture of a log). The detail is the name of the field.</summary>
    InvalidUrl,
    /// <summary>A name that Discord refuses for a webhook message or that carries a secret. The detail is the name of the field.</summary>
    InvalidName,
    /// <summary>The name does not fit a bot account (2 to 32 characters), and the bot is to take it. The detail is the name of the field.</summary>
    BotNameLength
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
        AuditName = CleanIdentityName(config.AuditName);
        AuditAvatarUrl = (config.AuditAvatarUrl ?? string.Empty).Trim();
        CommunityEnabled = config.CommunityEnabled;
        CommunityName = CleanIdentityName(config.CommunityName);
        CommunityAvatarUrl = (config.CommunityAvatarUrl ?? string.Empty).Trim();
        CommunityLead = TimeSpan.FromMinutes(config.CommunityLeadMinutes);
        BotTakesCommunityIdentity = config.BotTakesCommunityIdentity;
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
    /// <summary>The name and picture of the audit log through its webhook, empty: the ones of the webhook.</summary>
    public string AuditName { get; }
    public string AuditAvatarUrl { get; }
    public bool CommunityEnabled { get; }
    public string CommunityName { get; }
    public string CommunityAvatarUrl { get; }
    /// <summary>How long before a planned restart or stop the community is told.</summary>
    public TimeSpan CommunityLead { get; }
    /// <summary>The bot account takes the name and picture of the community log.</summary>
    public bool BotTakesCommunityIdentity { get; }
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
        CheckLimit(issues, nameof(config.CriticalLimit), config.CriticalLimit, 1, DiscordBotConfig.MaxCriticalLimit);
        CheckLimit(issues, nameof(config.CriticalWindowMinutes), config.CriticalWindowMinutes, 1, 120);
        CheckLimit(issues, nameof(config.CommunityLeadMinutes), config.CommunityLeadMinutes, 1, DiscordBotConfig.MaxCommunityLeadMinutes);
        CheckAvatar(issues, nameof(config.AuditAvatarUrl), config.AuditAvatarUrl);
        CheckAvatar(issues, nameof(config.CommunityAvatarUrl), config.CommunityAvatarUrl);
        CheckName(issues, nameof(config.AuditName), config.AuditName);
        CheckName(issues, nameof(config.CommunityName), config.CommunityName);
        // A bot account has a shorter name than a webhook message: only when the bot is to take it.
        if (config.BotTakesCommunityIdentity && !string.IsNullOrWhiteSpace(config.CommunityName)
            && CleanIdentityName(config.CommunityName).Length is < DiscordBotConfig.MinBotUsernameLength or > DiscordBotConfig.MaxBotUsernameLength)
            issues.Add(new BotConfigIssue(BotConfigIssueKind.BotNameLength, nameof(config.CommunityName)));
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

    /// <summary>One clean line of at most 80 characters: the name a webhook message is sent under.</summary>
    private static string CleanIdentityName(string? name)
    {
        var clean = BotInputRules.SanitizeForLog(name);
        if (clean.Length <= DiscordBotConfig.MaxIdentityNameLength) return clean;
        // Cut between the characters people see: an emoji (a pair of surrogates, often joined with others) is never split.
        var end = 0;
        var elements = StringInfo.GetTextElementEnumerator(clean);
        while (elements.MoveNext())
        {
            var next = elements.ElementIndex + elements.GetTextElement().Length;
            if (next > DiscordBotConfig.MaxIdentityNameLength) break;
            end = next;
        }
        return clean[..end].TrimEnd();
    }

    // Discord refuses a webhook message whose name contains these.
    private static readonly string[] ForbiddenNameParts = ["discord", "clyde"];

    // The name is posted in public, the settings file is plain text: a token or a webhook link pasted into the wrong
    // field (the admin copied the wrong thing) is refused here, not saved and not posted.
    private static void CheckName(List<BotConfigIssue> issues, string field, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        // What is checked is also what is sent: the invisible characters (a zero-width space in "Dis cord") are gone from the
        // name before it goes out, so both the text as typed and the cleaned text are looked at.
        foreach (var text in new[] { name, CleanIdentityName(name) })
        {
            if (!ContainsSecret(text) && !ForbiddenNameParts.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
            issues.Add(new BotConfigIssue(BotConfigIssueKind.InvalidName, field));
            return;
        }
    }

    private static bool ContainsSecret(string text) => BotWebhook.Contains(text) || BotLogRedactor.Redact(text) != text;

    // The link only goes to Discord (it fetches the picture itself), Trebuchet never opens it: still only https, one clean link.
    private static void CheckAvatar(List<BotConfigIssue> issues, string field, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var text = url.Trim();
        if (text.Length > DiscordBotConfig.MaxAvatarUrlLength || text.Any(char.IsWhiteSpace) || text.Any(char.IsControl)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length > 0 || ContainsSecret(text))
            issues.Add(new BotConfigIssue(BotConfigIssueKind.InvalidUrl, field));
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
