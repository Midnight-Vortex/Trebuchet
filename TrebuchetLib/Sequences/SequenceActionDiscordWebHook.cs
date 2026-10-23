using System.Text.RegularExpressions;
using Discord.Webhook;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.Sequences;

public class SequenceActionDiscordWebHook : ISequenceAction
{
    public string Message { get; set; } = string.Empty;
    public string DiscordWebHook { get; set; } = "hhttps://discord.com/api/webhooks/...";
    public bool CancelOnFailure { get; set; }
    
    public async Task Execute(SequenceArgs args)
    {
        if (string.IsNullOrWhiteSpace(Message)) return;
        if (string.IsNullOrEmpty(DiscordWebHook))
        {
            if (CancelOnFailure)
                throw new OperationCanceledException("Webhook is empty");
            args.Logger.LogError("Webhook is empty");
            return;
        }
        
        var matches = DiscordWebHooks.DiscordWebHooksRegex().Match(DiscordWebHook);
        if (!matches.Success)
        {
            if (CancelOnFailure)
                throw new OperationCanceledException("Webhook URL is invalid");
            args.Logger.LogError("Webhook URL is invalid");
            return;
        }

        try
        {
            using var discord = new DiscordWebhookClient(DiscordWebHook);
            // Discord retries on its own: a slow Discord must not hold the sequence (and a pending restart) for ever.
            await discord.SendMessageAsync(args.Expand(Message)).WaitAsync(TimeSpan.FromSeconds(30), args.CancellationToken);
        }
        catch (Exception ex)
        {
            if (CancelOnFailure)
                throw new OperationCanceledException("Failed to send webhook", ex);
            args.Logger.LogError(ex, "Failed to send webhook");
        }
    }
}