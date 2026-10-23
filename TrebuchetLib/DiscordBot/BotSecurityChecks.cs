using Discord.WebSocket;

namespace TrebuchetLib.DiscordBot;

/// <summary>What the bot may do in the admin channel.</summary>
internal sealed record BotChannelAccess(bool Exists, bool InGuild, bool CanView, bool CanSend, bool CanEmbed);

/// <summary>What the connected bot sees of its server, taken from the cache of the gateway client.</summary>
/// <param name="BotIsAdministrator">Null when the own member is not known.</param>
/// <param name="AdminChannel">Null when no admin channel is set.</param>
internal sealed record BotGuildSnapshot(
    ulong GuildId,
    string GuildName,
    IReadOnlySet<ulong> RoleIds,
    IReadOnlyList<ulong> OtherGuildIds,
    bool? BotIsAdministrator,
    BotChannelAccess? AdminChannel,
    int LatencyMs);

/// <param name="TokenStored">A token file exists (it may not be readable).</param>
/// <param name="TokenReadable">The token could be read.</param>
/// <param name="Connected">The bot runs and has seen its server.</param>
/// <param name="Live">The server as the connected bot sees it, null when not connected or not found.</param>
internal sealed record BotSecurityInput(
    bool TokenStored,
    bool TokenReadable,
    IReadOnlyList<BotConfigIssue> Issues,
    ulong GuildId,
    IReadOnlyList<ulong> RoleIds,
    ulong AdminChannelId,
    bool Connected,
    BotGuildSnapshot? Live);

/// <summary>The "check security" list of the panel. Pure: the input is a snapshot, so every case is tested.</summary>
internal static class BotSecurityChecks
{
    private const string NotConnected = "not connected";

    public static IReadOnlyList<BotCheckItem> Build(BotSecurityInput input)
    {
        var items = new List<BotCheckItem>
        {
            input.TokenReadable
                ? new BotCheckItem(BotCheckKind.Token, BotCheckStatus.Ok)
                : new BotCheckItem(BotCheckKind.Token, BotCheckStatus.Error, input.TokenStored ? "stored but not readable" : "not set")
        };

        if (input.Issues.Count == 0)
            items.Add(new BotCheckItem(BotCheckKind.Settings, BotCheckStatus.Ok));
        else
            items.AddRange(input.Issues.Select(x => new BotCheckItem(BotCheckKind.Settings, BotCheckStatus.Error,
                Clean(string.IsNullOrEmpty(x.Detail) ? x.Kind.ToString() : $"{x.Kind}: {x.Detail}"))));

        var live = input.Connected ? input.Live : null;
        var offline = input.Connected ? "server not found" : NotConnected;

        // The server.
        items.Add(live is not null
            ? new BotCheckItem(BotCheckKind.Guild, BotCheckStatus.Ok, Clean(live.GuildName))
            : new BotCheckItem(BotCheckKind.Guild, input.Connected ? BotCheckStatus.Error : BotCheckStatus.Warning, offline));

        // Every allowed role has to exist: a deleted role lets nobody in.
        if (input.RoleIds.Count == 0)
            items.Add(new BotCheckItem(BotCheckKind.Roles, BotCheckStatus.Error, "no roles"));
        else if (live is null)
            items.Add(new BotCheckItem(BotCheckKind.Roles, BotCheckStatus.Warning, offline));
        else
        {
            var missing = input.RoleIds.Where(x => !live.RoleIds.Contains(x)).ToList();
            items.Add(missing.Count == 0
                ? new BotCheckItem(BotCheckKind.Roles, BotCheckStatus.Ok)
                : new BotCheckItem(BotCheckKind.Roles, BotCheckStatus.Error, "missing: " + string.Join(", ", missing)));
        }

        // @everyone has the ID of the server: the settings alone tell.
        items.Add(input.RoleIds.Any(x => x == input.GuildId && x != 0)
            ? new BotCheckItem(BotCheckKind.EveryoneRole, BotCheckStatus.Error, input.GuildId.ToString())
            : new BotCheckItem(BotCheckKind.EveryoneRole, BotCheckStatus.Ok));

        // The bot leaves other servers by itself; still being on one means that failed.
        if (live is null)
            items.Add(new BotCheckItem(BotCheckKind.OtherGuilds, BotCheckStatus.Warning, offline));
        else
            items.Add(live.OtherGuildIds.Count == 0
                ? new BotCheckItem(BotCheckKind.OtherGuilds, BotCheckStatus.Ok)
                : new BotCheckItem(BotCheckKind.OtherGuilds, BotCheckStatus.Error, string.Join(", ", live.OtherGuildIds)));

        // The bot needs no administrator permission.
        items.Add(live?.BotIsAdministrator switch
        {
            null => new BotCheckItem(BotCheckKind.AdministratorPermission, BotCheckStatus.Warning, live is null ? offline : "unknown"),
            true => new BotCheckItem(BotCheckKind.AdministratorPermission, BotCheckStatus.Warning, "administrator"),
            false => new BotCheckItem(BotCheckKind.AdministratorPermission, BotCheckStatus.Ok)
        });

        // The admin channel.
        if (input.AdminChannelId == 0)
            items.Add(new BotCheckItem(BotCheckKind.Channel, BotCheckStatus.Warning, "not set"));
        else if (live is null)
            items.Add(new BotCheckItem(BotCheckKind.Channel, BotCheckStatus.Warning, offline));
        else
            items.Add(ChannelItem(live.AdminChannel));

        items.Add(input.Connected
            ? new BotCheckItem(BotCheckKind.Connection, BotCheckStatus.Ok, live is null || live.LatencyMs < 0 ? string.Empty : $"{live.LatencyMs} ms")
            : new BotCheckItem(BotCheckKind.Connection, BotCheckStatus.Warning, NotConnected));
        return items;
    }

    /// <summary>The server as the connected bot sees it. Null when it is not in the cache.</summary>
    public static BotGuildSnapshot? SnapshotOf(DiscordSocketClient client, ulong guildId, ulong adminChannelId)
    {
        var guild = client.GetGuild(guildId);
        if (guild is null) return null;
        var me = guild.CurrentUser;
        BotChannelAccess? access = null;
        if (adminChannelId != 0)
        {
            var channel = client.GetChannel(adminChannelId) as SocketGuildChannel;
            if (channel is null) access = new BotChannelAccess(false, false, false, false, false);
            else if (channel.Guild.Id != guildId || me is null) access = new BotChannelAccess(true, channel.Guild.Id == guildId, false, false, false);
            else
            {
                var permissions = me.GetPermissions(channel);
                var canSend = channel is SocketThreadChannel ? permissions.SendMessagesInThreads : permissions.SendMessages;
                access = new BotChannelAccess(true, true, permissions.ViewChannel, canSend, permissions.EmbedLinks);
            }
        }
        return new BotGuildSnapshot(
            guild.Id,
            guild.Name ?? string.Empty,
            guild.Roles.Select(x => x.Id).ToHashSet(),
            client.Guilds.Where(x => x.Id != guildId).Select(x => x.Id).ToList(),
            me?.GuildPermissions.Administrator,
            access,
            client.Latency);
    }

    private static BotCheckItem ChannelItem(BotChannelAccess? access) => access switch
    {
        null or { Exists: false } => new BotCheckItem(BotCheckKind.Channel, BotCheckStatus.Error, "channel not found"),
        { InGuild: false } => new BotCheckItem(BotCheckKind.Channel, BotCheckStatus.Error, "channel on another server"),
        { CanView: false } => new BotCheckItem(BotCheckKind.Channel, BotCheckStatus.Error, "can not view the channel"),
        { CanSend: false } => new BotCheckItem(BotCheckKind.Channel, BotCheckStatus.Error, "can not send messages"),
        { CanEmbed: false } => new BotCheckItem(BotCheckKind.Channel, BotCheckStatus.Error, "can not embed links"),
        _ => new BotCheckItem(BotCheckKind.Channel, BotCheckStatus.Ok)
    };

    private static string Clean(string? text) => BotInputRules.SanitizeForLog(BotLogRedactor.Redact(text));
}
