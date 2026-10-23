using System.Net;
using System.Text.RegularExpressions;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Turns the slash commands of the handler into Discord.Net builders and registers them for the one configured server
/// only. A command path of two or three words becomes a sub command or a sub command group ("restart postpone",
/// "bot channel set"). No command is registered globally, and none can be used in a direct message.
/// </summary>
internal static partial class DiscordNetCommandRegistrar
{
    /// <summary>Text options are the reason of an action, which the bot cuts to this length anyway.</summary>
    public const int StringMaxLength = BotInputRules.MaxReasonLength;
    private const int MaxDescription = 100;

    /// <exception cref="ArgumentException">A path that Discord would refuse or that conflicts with another one.</exception>
    public static IReadOnlyList<SlashCommandBuilder> BuildCommands(IReadOnlyList<BotSlashCommand> commands)
    {
        var roots = new List<Node>();
        foreach (var command in commands)
        {
            var parts = command.Path.Split(' ');
            if (parts.Length is < 1 or > 3 || parts.Any(x => !NamePattern().IsMatch(x)))
                throw new ArgumentException($"'{command.Path}' is no valid command path", nameof(commands));

            var level = roots;
            Node? node = null;
            foreach (var part in parts)
            {
                if (node?.Leaf is not null) throw new ArgumentException($"'{command.Path}' extends the command '{node.Leaf.Path}'", nameof(commands));
                node = level.FirstOrDefault(x => x.Name == part);
                if (node is null)
                {
                    node = new Node(part);
                    level.Add(node);
                }
                level = node.Children;
            }
            if (node!.Leaf is not null || node.Children.Count > 0)
                throw new ArgumentException($"'{command.Path}' is defined twice or has sub commands", nameof(commands));
            node.Leaf = command;
        }

        return roots.Select(ToBuilder).ToList();
    }

    /// <summary>The commands as Discord.Net sends them, optionally without the flags for direct messages and installs.</summary>
    public static ApplicationCommandProperties[] BuildProperties(IReadOnlyList<BotSlashCommand> commands, bool withContexts)
    {
        var properties = BuildCommands(commands).Select(x => (ApplicationCommandProperties)x.Build()).ToArray();
        if (!withContexts)
        {
            foreach (var command in properties)
            {
                command.ContextTypes = Optional<HashSet<InteractionContextType>>.Unspecified;
                command.IntegrationTypes = Optional<HashSet<ApplicationIntegrationType>>.Unspecified;
                command.IsDMEnabled = Optional<bool>.Unspecified;
            }
        }
        return properties;
    }

    /// <summary>Replaces the commands of the server with ours and removes any global command of the application.</summary>
    public static async Task RegisterAsync(DiscordSocketClient client, SocketGuild guild, IReadOnlyList<BotSlashCommand> commands, ILogger logger)
    {
        try
        {
            await guild.BulkOverwriteApplicationCommandAsync(BuildProperties(commands, withContexts: true)).ConfigureAwait(false);
        }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.BadRequest)
        {
            // Discord documents the context flags for global commands only. A command of a server can never be used in a
            // direct message anyway, so it is registered without them rather than not at all.
            logger.LogWarning("Discord bot: the server refused the commands with context flags ({Error}), registering them without",
                DiscordNetLog.Describe(ex));
            await guild.BulkOverwriteApplicationCommandAsync(BuildProperties(commands, withContexts: false)).ConfigureAwait(false);
        }
        // Global commands would show up on every server and in direct messages: there must be none.
        var global = await client.GetGlobalApplicationCommandsAsync().ConfigureAwait(false);
        if (global.Count > 0)
            await client.BulkOverwriteGlobalApplicationCommandsAsync([]).ConfigureAwait(false);
    }

    private static SlashCommandBuilder ToBuilder(Node root)
    {
        var builder = new SlashCommandBuilder
        {
            Name = root.Name,
            Description = DescriptionOf(root),
            ContextTypes = [InteractionContextType.Guild],
            IntegrationTypes = [ApplicationIntegrationType.GuildInstall],
            IsNsfw = false
        };
#pragma warning disable CS0618 // Replaced by ContextTypes, but still sent by this version: set both.
        builder.IsDMEnabled = false;
#pragma warning restore CS0618
        if (root.Leaf is not null)
        {
            foreach (var option in root.Leaf.Options) builder.AddOption(OptionOf(option));
        }
        else
        {
            foreach (var child in root.Children) builder.AddOption(SubOf(child));
        }
        return builder;
    }

    private static SlashCommandOptionBuilder SubOf(Node node)
    {
        var builder = new SlashCommandOptionBuilder
        {
            Name = node.Name,
            Description = DescriptionOf(node),
            Options = []
        };
        if (node.Leaf is not null)
        {
            builder.Type = ApplicationCommandOptionType.SubCommand;
            foreach (var option in node.Leaf.Options) builder.Options.Add(OptionOf(option));
        }
        else
        {
            builder.Type = ApplicationCommandOptionType.SubCommandGroup;
            foreach (var child in node.Children) builder.Options.Add(SubOf(child));
        }
        return builder;
    }

    internal static SlashCommandOptionBuilder OptionOf(BotSlashOption option)
    {
        if (!NamePattern().IsMatch(option.Name)) throw new ArgumentException($"'{option.Name}' is no valid option name", nameof(option));
        var builder = new SlashCommandOptionBuilder
        {
            Name = option.Name,
            Description = Describe(option.Description, option.Name),
            IsRequired = option.Required
        };
        switch (option.Type)
        {
            case BotOptionType.Integer:
                builder.Type = ApplicationCommandOptionType.Integer;
                builder.MinValue = option.Min;
                builder.MaxValue = option.Max;
                break;
            case BotOptionType.String:
                builder.Type = ApplicationCommandOptionType.String;
                builder.MaxLength = StringMaxLength;
                break;
            default:
                throw new ArgumentException($"Option type {option.Type} is not supported", nameof(option));
        }
        return builder;
    }

    // A group has no text of its own: the only command in it lends its text, else the names of its parts.
    private static string DescriptionOf(Node node)
    {
        if (node.Leaf is not null) return Describe(node.Leaf.Description, node.Name);
        var leaves = Leaves(node).ToList();
        return leaves.Count == 1
            ? Describe(leaves[0].Description, node.Name)
            : Describe(string.Join(", ", node.Children.Select(x => x.Name)), node.Name);
    }

    private static IEnumerable<BotSlashCommand> Leaves(Node node) =>
        node.Leaf is not null ? [node.Leaf] : node.Children.SelectMany(Leaves);

    private static string Describe(string? text, string fallback)
    {
        var clean = BotInputRules.SanitizeForLog(text);
        return BotDiscordRenderer.Cut(string.IsNullOrWhiteSpace(clean) ? fallback : clean, MaxDescription);
    }

    // Discord: lower case, 1 to 32 characters. Kept to plain ASCII on purpose.
    [GeneratedRegex("^[a-z0-9_-]{1,32}$")]
    private static partial Regex NamePattern();

    private sealed class Node(string name)
    {
        public string Name { get; } = name;
        public BotSlashCommand? Leaf { get; set; }
        public List<Node> Children { get; } = [];
    }
}
