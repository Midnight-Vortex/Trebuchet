using Discord;
using Discord.WebSocket;

namespace TrebuchetLib.DiscordBot;

internal enum BotRawOptionKind { SubCommand, SubCommandGroup, Integer, String, Other }

/// <summary>An option of a slash command as Discord delivered it.</summary>
internal sealed record BotRawOption(string Name, BotRawOptionKind Kind, object? Value, IReadOnlyList<BotRawOption> Options);

/// <param name="RoleIds">The roles of the member in this very interaction. Null when Discord sent no member object.</param>
internal sealed record BotRawUser(ulong Id, string DisplayName, bool IsBot, bool IsWebhook, IReadOnlyCollection<ulong>? RoleIds);

/// <summary>How the interaction is acknowledged, which decides where the answers go.</summary>
internal enum BotAckKind
{
    /// <summary>
    /// "Thinking" in a new private message (type 5, ephemeral) before anything is done. Every answer goes into that new
    /// message or a further private one. What is not a command or a button of the status message (junk, a question that
    /// sits on a public message): nothing is done unless Discord took the acknowledgment.
    /// </summary>
    NewPrivateMessage,
    /// <summary>
    /// Not acknowledged before the handler runs: its first answer is a new private message (type 4, ephemeral), one trip to
    /// Discord instead of two (first "thinking", then the answer), and the handler starts at once. When it takes too long
    /// the Discord layer acknowledges with "thinking" and the answer fills that message. Commands and the buttons of the
    /// status message: the public status message is never touched.
    /// </summary>
    LazyPrivate,
    /// <summary>
    /// The click on a question of the bot in one of its own private messages (type 6): an update replaces that message,
    /// a reply is a new private message.
    /// </summary>
    UpdateQuestion,
    /// <summary>
    /// Not acknowledged before the handler runs: the menu of a question answers with the window for the reason, and a
    /// window can only be the very first answer. The first answer of the handler is the acknowledgment; the Discord layer
    /// acknowledges on its own when the handler takes too long (the window is not possible then).
    /// </summary>
    Lazy
}

/// <param name="NeedsRoles">Discord sent no member: the roles have to be fetched before anything is decided.</param>
internal sealed record BotCapturedInteraction(BotInteraction Interaction, BotAckKind Ack, bool NeedsRoles);

/// <summary>
/// Reduces a command, click or menu choice to a <see cref="BotInteraction"/>, using only what Discord delivered with it.
/// The roles are copied at once from the member object of the interaction (Discord.Net applies that object to its
/// member right before it raises the event, on the same task), so a later change can not reach the copy. Names decide
/// nothing, they only go to the log.
/// </summary>
internal static class BotInteractionMapper
{
    private const int MaxDepth = 3;
    private const int MaxValues = 25;
    private const int MaxValueLength = 100;
    private const int AnyInstance = 1000;
    private const int MaxModalFields = 5;
    private const int MaxModalValueLength = 500;

    public static BotInteraction FromSlash(ulong interactionId, BotRawUser user, ulong? guildId, ulong? channelId, string commandName,
        IReadOnlyCollection<BotRawOption>? options)
    {
        var path = new List<string> { commandName };
        var current = options ?? [];
        // Sub command groups and sub commands come as one nested option each.
        while (path.Count < MaxDepth && current.Count == 1
               && current.First() is { Kind: BotRawOptionKind.SubCommand or BotRawOptionKind.SubCommandGroup } sub)
        {
            path.Add(sub.Name);
            current = sub.Options;
        }

        var numbers = new Dictionary<string, long>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in current)
        {
            switch (option.Kind)
            {
                case BotRawOptionKind.Integer when ToLong(option.Value) is { } number:
                    numbers.TryAdd(option.Name, number);
                    break;
                case BotRawOptionKind.String when option.Value is string text:
                    texts.TryAdd(option.Name, text);
                    break;
            }
        }

        return Create(interactionId, BotInteractionKind.Slash, user, guildId, channelId, string.Join(' ', path))
            with { Numbers = numbers, Texts = texts };
    }

    /// <summary>A window for the reason that was submitted: its custom ID and the text fields by name.</summary>
    public static BotInteraction FromModal(ulong interactionId, BotRawUser user, ulong? guildId, ulong? channelId, string customId,
        IReadOnlyDictionary<string, string>? fields)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in (fields ?? new Dictionary<string, string>()).Take(MaxModalFields))
            texts.TryAdd(field.Key, BotDiscordRenderer.Cut(field.Value, MaxModalValueLength));
        return Create(interactionId, BotInteractionKind.Modal, user, guildId, channelId, customId) with { Texts = texts };
    }

    public static BotInteraction FromComponent(ulong interactionId, BotInteractionKind kind, BotRawUser user, ulong? guildId, ulong? channelId,
        string customId, IReadOnlyCollection<string>? values)
    {
        var interaction = Create(interactionId, kind, user, guildId, channelId, customId);
        return kind == BotInteractionKind.Select
            ? interaction with { Values = (values ?? []).Take(MaxValues).Select(x => BotDiscordRenderer.Cut(x, MaxValueLength)).ToArray() }
            : interaction;
    }

    /// <summary>
    /// A copy of the roles without @everyone (its ID is the ID of the server, every member has it) and without zeros.
    /// No roles known means none: the check refuses.
    /// </summary>
    public static IReadOnlyCollection<ulong> CleanRoles(IEnumerable<ulong>? roles, ulong? guildId) =>
        roles is null ? [] : roles.Where(x => x != 0 && x != guildId).Distinct().ToArray();

    /// <summary>
    /// Only a question of the bot (confirm, cancel, minutes) in a private message may be replaced by the answer. Anything
    /// else, above all a button of the public status message, gets a new private message.
    /// </summary>
    public static BotAckKind ChooseAck(BotInteractionKind kind, string? customId, bool messageIsPrivate, string? commandPath = null)
    {
        // A command, or a button of the status message (a public message): answered with a new private message. A command that
        // changes something without asking first is acknowledged before it runs, so that nothing is done when Discord does not take it
        // (the user would see "failed" for something that happened, and trying again would do it twice).
        if (kind == BotInteractionKind.Slash) return ActsWithoutQuestion(commandPath) ? BotAckKind.NewPrivateMessage : BotAckKind.LazyPrivate;
        if (!messageIsPrivate)
            return kind == BotInteractionKind.Button
                   && BotCustomId.TryParse(customId, AnyInstance, out var publicKind, out _, out _, out _) && publicKind == BotCustomIdKind.Action
                ? BotAckKind.LazyPrivate
                : BotAckKind.NewPrivateMessage;
        if (!BotCustomId.TryParse(customId, AnyInstance, out var idKind, out _, out _, out _) || idKind == BotCustomIdKind.Action)
            return BotAckKind.NewPrivateMessage;
        // The menu answers with the window for the reason: that has to be the first answer, so nothing is acknowledged before.
        if (kind == BotInteractionKind.Select && idKind == BotCustomIdKind.PostponeSelect) return BotAckKind.Lazy;
        return BotAckKind.UpdateQuestion;
    }

    /// <summary>
    /// The commands that do something that is not undone by asking again, without a question first: postponing adds the minutes every
    /// time. (Cancelling and releasing end up the same when done twice, the others ask for a confirmation first or only read.)
    /// </summary>
    internal static bool ActsWithoutQuestion(string? commandPath) => string.Equals(commandPath, "restart postpone", StringComparison.Ordinal);

    /// <summary>Runs on the gateway task, before anything else can change the member: takes what is needed right now.</summary>
    public static BotCapturedInteraction Capture(SocketInteraction interaction)
    {
        var user = UserOf(interaction.User);
        switch (interaction)
        {
            case SocketSlashCommand slash:
            {
                var fromSlash = FromSlash(slash.Id, user, slash.GuildId, slash.ChannelId, slash.Data.Name, slash.Data.Options?.Select(OptionOf).ToList());
                return new BotCapturedInteraction(fromSlash, ChooseAck(BotInteractionKind.Slash, null, true, fromSlash.Name), user.RoleIds is null);
            }
            case SocketMessageComponent component:
            {
                var kind = component.Data.Type == ComponentType.Button ? BotInteractionKind.Button : BotInteractionKind.Select;
                var isPrivate = component.Message?.Flags is { } flags && flags.HasFlag(MessageFlags.Ephemeral);
                return new BotCapturedInteraction(
                    FromComponent(component.Id, kind, user, component.GuildId, component.ChannelId, component.Data.CustomId ?? string.Empty,
                        component.Data.Values),
                    ChooseAck(kind, component.Data.CustomId, isPrivate), user.RoleIds is null);
            }
            case SocketModal modal:
            {
                var isPrivate = modal.Message?.Flags is { } flags && flags.HasFlag(MessageFlags.Ephemeral);
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var component in modal.Data.Components ?? [])
                    if (!string.IsNullOrEmpty(component.CustomId) && component.Value is { } value)
                        fields.TryAdd(component.CustomId, value);
                return new BotCapturedInteraction(
                    FromModal(modal.Id, user, modal.GuildId, modal.ChannelId, modal.Data.CustomId ?? string.Empty, fields),
                    ChooseAck(BotInteractionKind.Modal, modal.Data.CustomId, isPrivate), user.RoleIds is null);
            }
            default:
                throw new NotSupportedException($"Interaction type {interaction.Type} is not handled");
        }
    }

    private static BotRawUser UserOf(SocketUser user)
    {
        // SocketGuildUser (and a webhook user) carry the roles of the member object of this interaction.
        var roles = user is IGuildUser member ? member.RoleIds.ToArray() : null;
        var name = (user as SocketGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username;
        return new BotRawUser(user.Id, name ?? string.Empty, user.IsBot, user.IsWebhook, roles);
    }

    private static BotRawOption OptionOf(SocketSlashCommandDataOption option) => new(
        option.Name,
        option.Type switch
        {
            ApplicationCommandOptionType.SubCommand => BotRawOptionKind.SubCommand,
            ApplicationCommandOptionType.SubCommandGroup => BotRawOptionKind.SubCommandGroup,
            ApplicationCommandOptionType.Integer => BotRawOptionKind.Integer,
            ApplicationCommandOptionType.String => BotRawOptionKind.String,
            _ => BotRawOptionKind.Other
        },
        option.Value,
        option.Options?.Select(OptionOf).ToList() ?? []);

    private static BotInteraction Create(ulong interactionId, BotInteractionKind kind, BotRawUser user, ulong? guildId, ulong? channelId, string name) =>
        new(interactionId, kind, user.Id, BotInputRules.SanitizeForLog(user.DisplayName), !user.IsBot && !user.IsWebhook,
            guildId, channelId, CleanRoles(user.RoleIds, guildId), name);

    private static long? ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        _ => null
    };
}
