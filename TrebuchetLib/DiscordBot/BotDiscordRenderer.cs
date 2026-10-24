using Discord;

namespace TrebuchetLib.DiscordBot;

/// <summary>A message on its way to Discord. Built only by <see cref="BotDiscordRenderer"/>, so it never mentions anyone.</summary>
internal sealed record BotOutgoingMessage(string Text, Embed[] Embeds, MessageComponent Components, AllowedMentions AllowedMentions);

/// <summary>
/// Turns the plain answers and status messages of the bot into Discord.Net embeds and components. Nothing here talks to
/// Discord, so all of it is tested without a connection. Every message switches mentions off and stays inside the limits
/// of Discord (a message over a limit would be refused as a whole).
/// </summary>
internal static class BotDiscordRenderer
{
    public const int MaxTextLength = 2000;
    public const int MaxEmbeds = 10;
    public const int MaxRows = 5;
    public const int MaxButtonsPerRow = 5;
    private const int MaxEmbedTotal = 6000;
    private const int MaxTitle = 256;
    private const int MaxFieldName = 256;
    private const int MaxFieldValue = 1024;
    private const int MaxFields = 25;
    private const int MaxFooter = 2048;
    private const int MaxLabel = 80;
    // Discord.Net refuses a longer placeholder (ArgumentException) and the whole answer with it.
    private const int MaxPlaceholder = SelectMenuBuilder.MaxPlaceholderLength;
    private const int MaxOptions = 25;
    private const int MaxOptionText = 100;
    // Button IDs carry an instance of at most three digits.
    private const int AnyInstance = 1000;

    /// <summary>A new object every time: nobody can widen a shared instance for all messages.</summary>
    public static AllowedMentions NoMentions() => new(AllowedMentionTypes.None) { MentionRepliedUser = false };

    /// <summary>An answer to an interaction: text, the embeds with their buttons, and the question (if any) at the end.</summary>
    public static BotOutgoingMessage Render(BotReply reply)
    {
        var (statuses, embeds) = EmbedsOf(reply.Embeds);
        var components = ComponentsOf(statuses, reply.Question);
        var text = Cut(reply.Text, MaxTextLength);
        // Discord refuses a message without anything in it.
        if (string.IsNullOrWhiteSpace(text) && embeds.Length == 0 && components.Components.Count == 0) text = "…";
        return new BotOutgoingMessage(text, embeds, components, NoMentions());
    }

    /// <summary>The id of the one text field of the window for the reason (the handler reads it by this name).</summary>
    public const string ReasonFieldId = "reason";
    // Limits of Discord for a window and its field.
    private const int MaxModalTitle = 45;
    private const int MaxModalLabel = 45;
    private const int MaxModalPlaceholder = 100;

    /// <summary>The window for the reason: one short text field that may stay empty.</summary>
    public static Modal RenderModal(BotReasonModal modal) => new ModalBuilder()
        .WithTitle(Cut(modal.Title, MaxModalTitle))
        .WithCustomId(BotCustomId.ForReasonModal(modal.Nonce))
        .AddTextInput(Cut(modal.Label, MaxModalLabel), ReasonFieldId, TextInputStyle.Short, Cut(modal.Placeholder, MaxModalPlaceholder),
            minLength: 0, maxLength: Math.Clamp(modal.MaxLength, 1, 4000), required: false)
        .Build();

    /// <summary>The public status message of a server: one embed, its buttons below, no text.</summary>
    public static BotOutgoingMessage RenderStatus(BotStatusMessage message)
    {
        var (statuses, embeds) = EmbedsOf([message]);
        return new BotOutgoingMessage(string.Empty, embeds, ComponentsOf(statuses, null), NoMentions());
    }

    /// <summary>A short plain note (audit channel).</summary>
    public static BotOutgoingMessage RenderNote(string text)
    {
        var clean = Cut(text, MaxTextLength);
        return new BotOutgoingMessage(string.IsNullOrWhiteSpace(clean) ? "…" : clean, [], new ComponentBuilder().Build(), NoMentions());
    }

    public static Embed ToEmbed(BotStatusMessage message)
    {
        var builder = new EmbedBuilder()
            .WithTitle(Cut(message.Title, MaxTitle))
            .WithColor(new Color(message.Color & 0xFFFFFFu));
        foreach (var field in message.Fields.Take(MaxFields))
            builder.AddField(NonEmpty(Cut(field.Name, MaxFieldName)), NonEmpty(Cut(field.Value, MaxFieldValue)), field.Inline);
        if (!string.IsNullOrWhiteSpace(message.Footer))
            builder.WithFooter(Cut(message.Footer, MaxFooter));
        if (message.UpdatedUtc is { } updated)
            builder.WithTimestamp(new DateTimeOffset(DateTime.SpecifyKind(updated, DateTimeKind.Utc)));
        while (builder.Length > MaxEmbedTotal && builder.Fields.Count > 0)
            builder.Fields.RemoveAt(builder.Fields.Count - 1);
        return builder.Build();
    }

    public static ButtonStyle StyleOf(BotButtonStyle style) => style switch
    {
        BotButtonStyle.Success => ButtonStyle.Success,
        BotButtonStyle.Danger => ButtonStyle.Danger,
        BotButtonStyle.Primary => ButtonStyle.Primary,
        // Never a link or premium button: those do not come back to the bot as a click.
        _ => ButtonStyle.Secondary
    };

    private static (List<BotStatusMessage> statuses, Embed[] embeds) EmbedsOf(IReadOnlyList<BotStatusMessage>? messages)
    {
        var statuses = new List<BotStatusMessage>();
        var embeds = new List<Embed>();
        var total = 0;
        foreach (var message in messages ?? [])
        {
            if (embeds.Count >= MaxEmbeds) break;
            var embed = ToEmbed(message);
            // The limit of 6000 characters counts all embeds of a message together.
            if (total + embed.Length > MaxEmbedTotal) break;
            total += embed.Length;
            statuses.Add(message);
            embeds.Add(embed);
        }
        return (statuses, embeds.ToArray());
    }

    private static MessageComponent ComponentsOf(IReadOnlyList<BotStatusMessage> statuses, BotQuestion? question)
    {
        var rows = new List<ActionRowBuilder>();
        var questionRow = QuestionRow(question);
        var buttonRows = MaxRows - (questionRow is null ? 0 : 1);
        // Several servers in one answer: say on every button which server it is for.
        var withButtons = statuses.Count(x => x.Buttons.Count > 0);
        foreach (var status in statuses)
        {
            var buttons = status.Buttons.Select(b => ButtonOf(b, withButtons > 1)).OfType<ButtonBuilder>().ToList();
            for (var i = 0; i < buttons.Count && rows.Count < buttonRows; i += MaxButtonsPerRow)
                rows.Add(new ActionRowBuilder().WithComponents(buttons.Skip(i).Take(MaxButtonsPerRow).Cast<IMessageComponentBuilder>().ToList()));
        }
        if (questionRow is not null) rows.Add(questionRow);
        return new ComponentBuilder().WithRows(rows).Build();
    }

    private static ButtonBuilder? ButtonOf(BotButton button, bool withInstance)
    {
        // Only IDs of the bot itself: anything else would make Discord refuse the whole message.
        if (!BotCustomId.TryParse(button.CustomId, AnyInstance, out var kind, out _, out var instance, out _)
            || kind != BotCustomIdKind.Action)
            return null;
        var label = withInstance ? $"#{instance} {button.Label}" : button.Label;
        return new ButtonBuilder()
            .WithLabel(NonEmpty(Cut(label, MaxLabel)))
            .WithCustomId(button.CustomId)
            .WithStyle(StyleOf(button.Style))
            .WithDisabled(button.Disabled);
    }

    private static ActionRowBuilder? QuestionRow(BotQuestion? question)
    {
        switch (question)
        {
            case BotConfirmQuestion confirm:
            {
                var ok = BotCustomId.ForConfirm(confirm.Nonce);
                var cancel = BotCustomId.ForCancel(confirm.Nonce);
                if (!IsQuestionId(ok) || !IsQuestionId(cancel)) return null;
                return new ActionRowBuilder().WithComponents([
                    new ButtonBuilder().WithLabel(NonEmpty(Cut(confirm.ConfirmLabel, MaxLabel))).WithCustomId(ok).WithStyle(ButtonStyle.Danger),
                    new ButtonBuilder().WithLabel(NonEmpty(Cut(confirm.CancelLabel, MaxLabel))).WithCustomId(cancel).WithStyle(ButtonStyle.Secondary)
                ]);
            }
            case BotSelectQuestion select:
            {
                var id = BotCustomId.ForPostponeSelect(select.Nonce);
                var options = select.Choices
                    .Where(x => !string.IsNullOrWhiteSpace(x.Value) && x.Value.Length <= MaxOptionText)
                    .DistinctBy(x => x.Value)
                    .Take(MaxOptions)
                    .Select(x => new SelectMenuOptionBuilder().WithLabel(NonEmpty(Cut(x.Label, MaxOptionText))).WithValue(x.Value))
                    .ToList();
                if (!IsQuestionId(id) || options.Count == 0) return null;
                var menu = new SelectMenuBuilder()
                    .WithCustomId(id)
                    .WithMinValues(1)
                    .WithMaxValues(1)
                    .WithOptions(options);
                if (!string.IsNullOrWhiteSpace(select.Placeholder)) menu.WithPlaceholder(Cut(select.Placeholder, MaxPlaceholder));
                return new ActionRowBuilder().WithComponents([menu]);
            }
            default:
                return null;
        }
    }

    private static bool IsQuestionId(string id) =>
        BotCustomId.TryParse(id, AnyInstance, out var kind, out _, out _, out _) && kind != BotCustomIdKind.Action;

    private static string NonEmpty(string text) => string.IsNullOrWhiteSpace(text) ? "-" : text;

    internal static string Cut(string? text, int length)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= length) return text;
        var cut = text[..length];
        // Do not end in the middle of a surrogate pair.
        return char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }
}
