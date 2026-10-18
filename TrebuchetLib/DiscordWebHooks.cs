using System.Text.RegularExpressions;
using Discord;
using Discord.Webhook;

namespace TrebuchetLib;

public partial class DiscordWebHooks()
{
    public static Task Notify(ServerProfile profile, string message)
    {
        return Notify(profile, message, null);
    }

    /// <summary>
    /// Send a message to every webhook of the profile. The bot name may contain macros (like the DSL "Nick").
    /// With <see cref="ServerProfile.DiscordTimestamp"/> the message is sent as an embed so Discord shows its time.
    /// </summary>
    public static async Task Notify(ServerProfile profile, string message, MacroContext? context)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var hooks = profile.GetDiscordWebHooks().Where(x => DiscordWebHooksRegex().IsMatch(x)).ToList();
        if (hooks.Count == 0) return;

        if (context is not null)
            message = ServerMacros.Expand(message, context);
        var username = string.IsNullOrWhiteSpace(profile.DiscordBotName)
            ? null
            : context is null ? profile.DiscordBotName : ServerMacros.Expand(profile.DiscordBotName, context);

        List<Exception> errors = [];
        foreach (var hook in hooks)
        {
            try
            {
                using var discord = new DiscordWebhookClient(hook);
                if (profile.DiscordTimestamp)
                {
                    var embed = new EmbedBuilder()
                        .WithDescription(message)
                        .WithCurrentTimestamp()
                        .Build();
                    await discord.SendMessageAsync(embeds: [embed], username: username);
                }
                else
                    await discord.SendMessageAsync(text: message, username: username);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        if (errors.Count > 0)
            throw new AggregateException("Failed to send Discord notification", errors);
    }

    [GeneratedRegex("https:\\/\\/(?:\\w+\\.)?discord(?:app)?\\.com\\/api\\/webhooks\\/([0-9]+)\\/([\\w-]+)")]
    public static partial Regex DiscordWebHooksRegex();
}
