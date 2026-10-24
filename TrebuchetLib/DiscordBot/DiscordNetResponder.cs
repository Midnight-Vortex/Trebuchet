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

    // The interaction was not acknowledged yet (<see cref="BotAckKind.Lazy"/>): the first answer is the acknowledgment.

    /// <summary>The window for the reason as the first answer to a menu choice.</summary>
    Task RespondModalAsync(BotReasonModal modal);

    /// <summary>The first answer replaces the message of the menu (type 7).</summary>
    Task RespondUpdateAsync(BotOutgoingMessage message);

    /// <summary>The first answer is a new private message (type 4).</summary>
    Task RespondNewAsync(BotOutgoingMessage message);

    /// <summary>Acknowledges without any visible change (type 6): later answers replace the message of the menu.</summary>
    Task DeferUpdateAsync();
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
    // A lazy interaction is acknowledged by its first answer (or by the watchdog of the Discord layer).
    private bool _acked = ack != BotAckKind.Lazy;

    public BotAckKind Ack => ack;

    public Task ReplyAsync(BotReply reply) => SendAsync(reply, update: false);

    public Task UpdateAsync(BotReply reply) => SendAsync(reply, update: true);

    public async Task<bool> ShowModalAsync(BotReasonModal modal)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A window is only possible as the first answer: afterwards Discord refuses it.
            if (_acked) return false;
            await transport.RespondModalAsync(modal).ConfigureAwait(false);
            _acked = true;
            _originalUsed = true;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Discord bot: could not show the window for the reason: {Error}", DiscordNetLog.Describe(ex));
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A lazy interaction that nobody answered yet is acknowledged now (the handler takes too long, or has nothing to say):
    /// Discord shows "interaction failed" after three seconds. Without effect for every other interaction.
    /// </summary>
    public async Task AcknowledgeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_acked) return;
            await transport.DeferUpdateAsync().ConfigureAwait(false);
            _acked = true;
        }
        catch (Exception ex)
        {
            logger.LogDebug("Discord bot: could not acknowledge a menu choice: {Error}", DiscordNetLog.Describe(ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Where an answer goes. <see cref="BotAckKind.NewPrivateMessage"/>: the first answer fills the "thinking" message,
    /// later ones are new private messages; an update never reaches the message that was clicked (it may be public).
    /// <see cref="BotAckKind.UpdateQuestion"/>: an update replaces the private question, a reply is a new message.
    /// </summary>
    internal static BotReplyTarget TargetOf(BotAckKind ack, bool update, bool originalUsed) => ack is BotAckKind.UpdateQuestion or BotAckKind.Lazy
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
            else if (!_acked)
            {
                // Nobody was answered (not allowed and ignored): the menu still has to be acknowledged.
                await transport.DeferUpdateAsync().ConfigureAwait(false);
                _acked = true;
            }
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
            if (!_acked)
            {
                // The first answer of a lazy interaction is its acknowledgment.
                if (update) await transport.RespondUpdateAsync(message).ConfigureAwait(false);
                else await transport.RespondNewAsync(message).ConfigureAwait(false);
                _acked = true;
                _originalUsed = true;
                return;
            }
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

    public Task RespondModalAsync(BotReasonModal modal) => interaction is SocketMessageComponent component
        ? component.RespondWithModalAsync(BotDiscordRenderer.RenderModal(modal))
        : throw new InvalidOperationException("Only a menu choice or a click can open the window");

    public Task RespondUpdateAsync(BotOutgoingMessage message) => interaction is SocketMessageComponent component
        ? component.UpdateAsync(properties =>
        {
            properties.Content = message.Text;
            properties.Embeds = message.Embeds;
            properties.Components = message.Components;
            properties.AllowedMentions = message.AllowedMentions;
        })
        : throw new InvalidOperationException("Only a menu choice or a click can update its message");

    public Task RespondNewAsync(BotOutgoingMessage message) => interaction.RespondAsync(
        text: string.IsNullOrEmpty(message.Text) ? null : message.Text,
        embeds: message.Embeds,
        ephemeral: true,
        allowedMentions: message.AllowedMentions,
        components: message.Components);

    public Task DeferUpdateAsync() => interaction is SocketMessageComponent component
        ? component.DeferAsync()
        : interaction.DeferAsync(ephemeral: true);
}
