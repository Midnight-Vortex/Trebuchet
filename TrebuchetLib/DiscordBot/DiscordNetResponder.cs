using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>The three ways to answer an acknowledged interaction. A fake replaces it in the tests.</summary>
internal interface IBotInteractionTransport
{
    /// <summary>Edits the message the acknowledgment refers to (see <see cref="BotAckKind"/>).</summary>
    Task ModifyOriginalAsync(BotOutgoingMessage message);

    /// <summary>A further message, always private.</summary>
    Task FollowupAsync(BotOutgoingMessage message);

    Task DeleteOriginalAsync();
}

internal enum BotReplyTarget { Original, Followup }

/// <summary>
/// Answers one interaction after it was acknowledged. Every answer is private and mentions nobody. Errors of Discord
/// (a token that ran out, the network) are logged and swallowed: the handler has done its work already.
/// </summary>
internal sealed class DiscordNetResponder(IBotInteractionTransport transport, BotAckKind ack, ILogger logger) : IBotResponder
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _originalUsed;

    public BotAckKind Ack => ack;

    public Task ReplyAsync(BotReply reply) => SendAsync(reply, update: false);

    public Task UpdateAsync(BotReply reply) => SendAsync(reply, update: true);

    /// <summary>
    /// Where an answer goes. <see cref="BotAckKind.NewPrivateMessage"/>: the first answer fills the "thinking" message,
    /// later ones are new private messages; an update never reaches the message that was clicked (it may be public).
    /// <see cref="BotAckKind.UpdateQuestion"/>: an update replaces the private question, a reply is a new message.
    /// </summary>
    internal static BotReplyTarget TargetOf(BotAckKind ack, bool update, bool originalUsed) => ack == BotAckKind.UpdateQuestion
        ? update ? BotReplyTarget.Original : BotReplyTarget.Followup
        : originalUsed ? BotReplyTarget.Followup : BotReplyTarget.Original;

    /// <summary>Removes the "thinking" message when the handler decided to give no answer at all.</summary>
    public async Task FinishAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ack == BotAckKind.NewPrivateMessage && !_originalUsed)
                await transport.DeleteOriginalAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Discord bot: could not remove the pending answer: {Error}", DiscordNetLog.Describe(ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SendAsync(BotReply reply, bool update)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var message = BotDiscordRenderer.Render(reply);
            if (TargetOf(ack, update, _originalUsed) == BotReplyTarget.Original)
            {
                await transport.ModifyOriginalAsync(message).ConfigureAwait(false);
                _originalUsed = true;
            }
            else
            {
                await transport.FollowupAsync(message).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Discord bot: could not answer an interaction: {Error}", DiscordNetLog.Describe(ex));
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>The real transport: the interaction of Discord.Net.</summary>
internal sealed class SocketInteractionTransport(SocketInteraction interaction) : IBotInteractionTransport
{
    public Task ModifyOriginalAsync(BotOutgoingMessage message) => interaction.ModifyOriginalResponseAsync(properties =>
    {
        properties.Content = message.Text;
        properties.Embeds = message.Embeds;
        // Empty components remove the old buttons or menu.
        properties.Components = message.Components;
        properties.AllowedMentions = message.AllowedMentions;
    });

    public Task FollowupAsync(BotOutgoingMessage message) => interaction.FollowupAsync(
        text: string.IsNullOrEmpty(message.Text) ? null : message.Text,
        embeds: message.Embeds,
        ephemeral: true,
        allowedMentions: message.AllowedMentions,
        components: message.Components);

    public Task DeleteOriginalAsync() => interaction.DeleteOriginalResponseAsync();
}
