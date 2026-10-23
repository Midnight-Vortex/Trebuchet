namespace TrebuchetLib.DiscordBot;

public enum BotInteractionKind { Slash, Button, Select }

/// <summary>
/// A command, button click or menu choice, reduced to what the bot decides on. The Discord layer fills it from the
/// message Discord delivered: IDs only, roles as sent in that very message. Nothing here is a Discord.Net type.
/// </summary>
/// <param name="Name">Slash: the command path ("status", "restart postpone", "bot channel set"). Button/Select: the custom ID.</param>
/// <param name="Numbers">Slash options that are whole numbers by name ("instance", "minutes", "delay").</param>
/// <param name="Texts">Slash options that are text by name ("reason").</param>
/// <param name="Values">Select: the values the user picked.</param>
public sealed record BotInteraction(
    ulong InteractionId,
    BotInteractionKind Kind,
    ulong UserId,
    string UserName,
    bool IsHuman,
    ulong? GuildId,
    ulong? ChannelId,
    IReadOnlyCollection<ulong> RoleIds,
    string Name,
    IReadOnlyDictionary<string, long>? Numbers = null,
    IReadOnlyDictionary<string, string>? Texts = null,
    IReadOnlyList<string>? Values = null);

public sealed record BotChoice(string Label, string Value);

/// <summary>A question the bot asks with components: buttons to confirm, or a menu to pick from.</summary>
public abstract record BotQuestion(string Nonce);

public sealed record BotConfirmQuestion(string Nonce, string ConfirmLabel, string CancelLabel) : BotQuestion(Nonce);

public sealed record BotSelectQuestion(string Nonce, string Placeholder, IReadOnlyList<BotChoice> Choices) : BotQuestion(Nonce);

/// <summary>
/// An answer. Always visible only to the user who asked (ephemeral) and never with mentions: whoever renders it must
/// switch mentions off (<c>AllowedMentions.None</c>). Components of a question use <see cref="BotCustomId"/>.
/// </summary>
public sealed record BotReply(string Text, IReadOnlyList<BotStatusMessage>? Embeds = null, BotQuestion? Question = null);

/// <summary>Where the answer to one interaction goes. Implemented by the Discord layer (and by fakes in tests).</summary>
public interface IBotResponder
{
    /// <summary>Answer the interaction with a new private message.</summary>
    Task ReplyAsync(BotReply reply);

    /// <summary>Replace the private message of the button or menu that was used (its old components are gone).</summary>
    Task UpdateAsync(BotReply reply);
}

/// <summary>Posts and edits the public status messages and short notes. Implemented by the Discord layer.</summary>
public interface IBotChannelClient
{
    /// <returns>The ID of the new message, null when it could not be posted.</returns>
    Task<ulong?> PostStatusAsync(ulong channelId, BotStatusMessage message);

    /// <returns>False when the message does not exist any more (deleted), so a new one has to be posted.</returns>
    Task<bool> EditStatusAsync(ulong channelId, ulong messageId, BotStatusMessage message);

    /// <summary>A plain note, e.g. to the audit channel. No mentions.</summary>
    Task PostNoteAsync(ulong channelId, string text);
}

/// <summary>What the handler and the status board need from the settings of the running bot.</summary>
public interface IBotSettings
{
    /// <summary>The current policy, null when the settings are not valid (nobody is allowed anything).</summary>
    BotPolicy? Policy { get; }

    /// <summary>The first channel of the admin channels, 0 when the bot answers everywhere.</summary>
    ulong AdminChannelId { get; }

    /// <summary>Stores the channel as the only admin channel, saves the settings and replaces the policy.</summary>
    Task SetAdminChannelAsync(ulong channelId);

    ulong GetStatusMessageId(int instance);

    /// <summary>Remembers the status message of an instance in the settings file.</summary>
    void SetStatusMessageId(int instance, ulong messageId);
}
