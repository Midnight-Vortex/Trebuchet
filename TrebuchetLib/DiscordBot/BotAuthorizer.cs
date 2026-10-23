namespace TrebuchetLib.DiscordBot;

/// <summary>
/// What the bot knows about a command or button click, taken from the message Discord delivered. Only IDs: names,
/// nicknames and texts decide nothing. The roles are those the member has in this very message, not a cached copy.
/// </summary>
public sealed record BotRequest(
    ulong UserId,
    ulong? GuildId,
    ulong? ChannelId,
    IReadOnlyCollection<ulong> RoleIds,
    bool IsHuman,
    BotCommand Command,
    ulong InteractionId = 0);

/// <summary>Why a request was refused. The user only gets the neutral message, the reason is for the local log.</summary>
public enum BotDenyReason
{
    None,
    NoPolicy,
    NotInGuild,
    WrongGuild,
    NotHuman,
    WrongChannel,
    NotListedUser,
    MissingRole,
    CommandDisabled,
    LevelTooLow,
    Error
}

public readonly record struct BotDecision(bool Allowed, BotDenyReason Reason)
{
    public static BotDecision Allow { get; } = new(true, BotDenyReason.None);
    public static BotDecision Deny(BotDenyReason reason) => new(false, reason);

    /// <summary>The same text for every reason: it does not tell whether the ID or the role was missing.</summary>
    public const string PublicMessage = "Keine Berechtigung.";
}

/// <summary>
/// The double check: a request is allowed only when it comes from the right server, the right channel and a user whose
/// ID is on the list <b>and</b> who has one of the allowed roles. Every check has to pass, in this order, and any
/// error means "no". Used for commands, for button clicks and again for each confirmation.
/// </summary>
public static class BotAuthorizer
{
    public static BotDecision Authorize(BotPolicy? policy, BotRequest? request)
    {
        try
        {
            if (policy is null) return BotDecision.Deny(BotDenyReason.NoPolicy);
            if (request is null) return BotDecision.Deny(BotDenyReason.Error);

            // 1. Only on the server, never in a direct message.
            if (request.GuildId is not { } guildId) return BotDecision.Deny(BotDenyReason.NotInGuild);
            // 2. Only on the one configured server.
            if (guildId != policy.GuildId) return BotDecision.Deny(BotDenyReason.WrongGuild);
            // 3. Only real users: no bots, no webhooks.
            if (!request.IsHuman) return BotDecision.Deny(BotDenyReason.NotHuman);
            // 4. Only in the admin channel. The channel command works from the channel to be set.
            if (request.Command != BotCommand.SetChannel && policy.ChannelIds.Count > 0
                && (request.ChannelId is not { } channelId || !policy.ChannelIds.Contains(channelId)))
                return BotDecision.Deny(BotDenyReason.WrongChannel);
            // 5. The user ID has to be on the list (the super admin always is).
            if (!BotId.IsValid(request.UserId) || !policy.UserIds.Contains(request.UserId))
                return BotDecision.Deny(BotDenyReason.NotListedUser);
            // 6. And the user needs one of the allowed roles. Both checks are needed, either alone is not enough.
            if (!HasAllowedRole(policy, request.RoleIds)) return BotDecision.Deny(BotDenyReason.MissingRole);
            // 7. The command has to be switched on.
            if (!policy.IsCommandEnabled(request.Command)) return BotDecision.Deny(BotDenyReason.CommandDisabled);
            // 8. Super admin commands only for the super admin.
            if (BotCommands.LevelOf(request.Command) == BotLevel.SuperAdmin && request.UserId != policy.SuperAdminUserId)
                return BotDecision.Deny(BotDenyReason.LevelTooLow);

            return BotDecision.Allow;
        }
        catch (Exception)
        {
            return BotDecision.Deny(BotDenyReason.Error);
        }
    }

    private static bool HasAllowedRole(BotPolicy policy, IReadOnlyCollection<ulong>? roles)
    {
        if (roles is null) return false;
        foreach (var role in roles)
            if (policy.RoleIds.Contains(role))
                return true;
        return false;
    }
}
