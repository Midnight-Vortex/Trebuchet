using Discord;
using TrebuchetLib.DiscordBot;

/// <summary>
/// Crash regressions of the Discord bot: an input at a limit of Discord.Net must never make an answer throw, because a
/// throwing render loses the whole answer (and for the status board the whole message).
/// </summary>
static class CrashBotChecks
{
    private const ulong Operator = 200000000000000012UL;

    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        // --- the menu of a postponement: Discord.Net takes at most 100 characters of placeholder, not 150
        var nonce = new BotPendingActions().Create(Operator, BotCommand.Postpone, 0, DateTime.UtcNow);
        var placeholders = new[]
        {
            "x", new string('p', 99), new string('p', 100), new string('p', 101), new string('p', 150), new string('p', 400),
            // A surrogate pair across the limit: never half of it.
            new string('p', 99) + "\U0001F600" + "tail"
        };
        foreach (var placeholder in placeholders)
        {
            BotOutgoingMessage? rendered = null;
            Exception? error = null;
            try
            {
                rendered = BotDiscordRenderer.Render(new BotReply("Um wie viel verschieben?",
                    Question: new BotSelectQuestion(nonce, placeholder, [new BotChoice("15 Min", "15"), new BotChoice("1 Std", "60")])));
            }
            catch (Exception ex)
            {
                error = ex;
            }
            var menu = rendered?.Components.Components.OfType<ActionRowComponent>().SelectMany(x => x.Components).OfType<SelectMenuComponent>().SingleOrDefault();
            Check(error is null && menu is not null, $"A placeholder of {placeholder.Length} characters still renders the menu ({error?.GetType().Name}: {error?.Message})");
            Check(menu!.Placeholder.Length is > 0 and <= SelectMenuBuilder.MaxPlaceholderLength && !char.IsHighSurrogate(menu.Placeholder[^1])
                  && placeholder.StartsWith(menu.Placeholder, StringComparison.Ordinal), $"The placeholder of {placeholder.Length} characters is cut to the limit of Discord");
        }
        foreach (var messages in new[] { BotMessages.German, BotMessages.English })
            Check(messages.PostponePlaceholder.Length <= SelectMenuBuilder.MaxPlaceholderLength, "The placeholder of the bot fits as it is");

        // --- no server state can break the status board or /status: odd names, many servers, odd numbers and dates
        var config = new DiscordBotConfig { GuildId = 100000000000000011UL, SuperAdminUserId = Operator };
        config.AddRole(300000000000000011UL);
        foreach (var command in Enum.GetValues<BotCommand>()) config.SetEnabled(command, true);
        var policy = BotPolicy.Create(config).Policy ?? throw new Exception("The settings of the check are not valid");
        var odd = string.Concat(Enumerable.Repeat("Ex\u0000il\ud800ed `**` \U0001F600 \u202Elands\n", 40));
        DateTime?[] dates = [null, DateTime.MinValue, DateTime.MaxValue, DateTime.UtcNow, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)];
        var statuses = new List<BotServerStatus>();
        var n = 0;
        foreach (var state in Enum.GetValues<BotServerState>())
        foreach (var pending in Enum.GetValues<BotPendingAction>())
        foreach (var date in dates)
        {
            var instance = n++ % 7 == 0 ? 999 : n % 40;
            statuses.Add(new BotServerStatus(instance, n % 2 == 0 ? odd : string.Empty, state, n % 3 == 0 ? int.MinValue : int.MaxValue, -1,
                date, pending, dates[n % dates.Length], date));
        }
        foreach (var texts in new[] { BotTexts.German, BotTexts.English })
        {
            var models = new List<BotStatusMessage>();
            Exception? error = null;
            try
            {
                foreach (var status in statuses) models.Add(BotStatusMessageBuilder.Build(status, policy, texts, DateTime.UtcNow));
                foreach (var model in models) BotDiscordRenderer.RenderStatus(model);
                var all = BotDiscordRenderer.Render(new BotReply(odd, models));
                Check(all.Text.Length <= BotDiscordRenderer.MaxTextLength && all.Embeds.Length <= BotDiscordRenderer.MaxEmbeds
                      && all.Embeds.Sum(x => x.Length) <= 6000 && all.Components.Components.Count <= BotDiscordRenderer.MaxRows,
                    "One answer with every server stays inside the limits of Discord");
            }
            catch (Exception ex)
            {
                error = ex;
            }
            Check(error is null && models.Count == statuses.Count, $"No server state breaks a status message ({error?.GetType().Name}: {error?.Message})");
        }

        await Task.CompletedTask;
        return count;
    }
}
