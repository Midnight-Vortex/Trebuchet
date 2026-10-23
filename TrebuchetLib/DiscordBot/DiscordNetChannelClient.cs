using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>Sending to and editing in a channel. A fake replaces it in the tests.</summary>
internal interface IBotChannelTransport
{
    /// <summary>The server of a channel the bot knows, null when it does not know the channel (or it is no server channel).</summary>
    ulong? GuildOf(ulong channelId);

    /// <returns>The ID of the new message.</returns>
    Task<ulong> SendAsync(ulong channelId, BotOutgoingMessage message);

    /// <returns>False when the message does not exist (any more).</returns>
    Task<bool> ModifyAsync(ulong channelId, ulong messageId, BotOutgoingMessage message);
}

/// <summary>
/// The public status messages and notes. It writes only to the channel it is given, and only when that channel belongs to
/// the one server the bot works for. Nothing here throws: a failure is logged and reported as null or a failed result.
/// </summary>
internal sealed class DiscordNetChannelClient(IBotChannelTransport transport, Func<ulong> guildId, ILogger logger) : IBotChannelClient
{
    public async Task<ulong?> PostStatusAsync(ulong channelId, BotStatusMessage message)
    {
        if (!IsAllowed(channelId)) return null;
        try
        {
            return await transport.SendAsync(channelId, BotDiscordRenderer.RenderStatus(message)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Discord bot: could not post the status message in channel {Channel}: {Error}", channelId, DiscordNetLog.Describe(ex));
            return null;
        }
    }

    public async Task<BotEditResult> EditStatusAsync(ulong channelId, ulong messageId, BotStatusMessage message)
    {
        if (messageId == 0) return BotEditResult.Gone;
        if (!IsAllowed(channelId)) return BotEditResult.Failed;
        try
        {
            return await transport.ModifyAsync(channelId, messageId, BotDiscordRenderer.RenderStatus(message)).ConfigureAwait(false)
                ? BotEditResult.Edited
                : BotEditResult.Gone;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Discord bot: could not edit status message {Message} in channel {Channel}: {Error}", messageId, channelId,
                DiscordNetLog.Describe(ex));
            return BotEditResult.Failed;
        }
    }

    public async Task PostNoteAsync(ulong channelId, string text)
    {
        if (!IsAllowed(channelId)) return;
        try
        {
            await transport.SendAsync(channelId, BotDiscordRenderer.RenderNote(text)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Discord bot: could not post a note in channel {Channel}: {Error}", channelId, DiscordNetLog.Describe(ex));
        }
    }

    /// <summary>Only a channel the bot knows on the server it works for. Not connected: nothing.</summary>
    internal static bool IsAllowedChannel(ulong? channelGuild, ulong configuredGuild) =>
        BotId.IsValid(configuredGuild) && channelGuild == configuredGuild;

    private bool IsAllowed(ulong channelId)
    {
        ulong? channelGuild;
        try
        {
            channelGuild = BotId.IsValid(channelId) ? transport.GuildOf(channelId) : null;
        }
        catch (Exception)
        {
            channelGuild = null;
        }
        if (IsAllowedChannel(channelGuild, guildId())) return true;
        logger.LogWarning("Discord bot: refused to write to channel {Channel}, it is not a known channel of the configured server", channelId);
        return false;
    }
}

/// <summary>The real transport: the channels in the cache of the gateway client (complete with the Guilds intent).</summary>
internal sealed class SocketChannelTransport(Func<DiscordSocketClient?> client) : IBotChannelTransport
{
    public ulong? GuildOf(ulong channelId) => (client()?.GetChannel(channelId) as SocketGuildChannel)?.Guild.Id;

    public async Task<ulong> SendAsync(ulong channelId, BotOutgoingMessage message)
    {
        var channel = ChannelOf(channelId);
        var sent = await channel.SendMessageAsync(
            text: string.IsNullOrEmpty(message.Text) ? null : message.Text,
            embeds: message.Embeds,
            allowedMentions: message.AllowedMentions,
            components: message.Components).ConfigureAwait(false);
        return sent.Id;
    }

    public async Task<bool> ModifyAsync(ulong channelId, ulong messageId, BotOutgoingMessage message)
    {
        var channel = ChannelOf(channelId);
        try
        {
            await channel.ModifyMessageAsync(messageId, properties =>
            {
                properties.Content = message.Text;
                properties.Embeds = message.Embeds;
                properties.Components = message.Components;
                properties.AllowedMentions = message.AllowedMentions;
            }).ConfigureAwait(false);
            return true;
        }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound || ex.DiscordCode == DiscordErrorCode.UnknownMessage)
        {
            return false;
        }
    }

    private ISocketMessageChannel ChannelOf(ulong channelId) =>
        client()?.GetChannel(channelId) as ISocketMessageChannel ?? throw new InvalidOperationException("The channel is not known (any more).");
}
