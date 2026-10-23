using Microsoft.Extensions.Logging;

namespace TrebuchetLib.Sequences;

/// <summary>
/// Message to the players (RCon broadcast / server / alert, as set in the profile) and/or Discord, like the DSL
/// warning messages. Macros such as {servername}, {reason} or {players} are expanded.
/// </summary>
public class SequenceActionBroadcast : ISequenceAction
{
    public string Message { get; set; } = string.Empty;
    public bool SendRCon { get; set; } = true;
    public bool SendDiscord { get; set; } = true;
    public bool CancelOnFailure { get; set; }

    public async Task Execute(SequenceArgs args)
    {
        if (string.IsNullOrWhiteSpace(Message)) return;
        var sent = await args.Launcher.BroadcastMessage(args.Instance, Message, reason: args.Reason,
            rcon: SendRCon, discord: SendDiscord);
        if (sent) return;
        if (CancelOnFailure)
            throw new OperationCanceledException("Message could not be sent (RCon/Discord unavailable)");
        args.Logger.LogWarning("Message could not be sent (RCon/Discord unavailable)");
    }
}

/// <summary>
/// Built-in restart countdown of the DSL: sends each warning at its time before the restart and stops waiting as
/// soon as nobody is connected (optional). Only used by generated sequences, never serialized.
/// </summary>
internal sealed class SequenceActionRestartCountdown(IReadOnlyList<RestartWarning> warnings, bool earlyWhenEmpty) : ISequenceAction
{
    /// <summary>How long the countdown runs when the server does not empty before (its first warning).</summary>
    public TimeSpan Length => warnings.Where(x => x.Before > TimeSpan.Zero).Select(x => x.Before).DefaultIfEmpty().Max();

    public async Task Execute(SequenceArgs args)
    {
        var ordered = warnings.Where(x => x.Before > TimeSpan.Zero)
            .OrderByDescending(x => x.Before)
            .ToList();
        if (ordered.Count == 0) return;

        var total = ordered[0].Before;
        var start = DateTime.UtcNow;
        foreach (var warning in ordered)
        {
            if (!await WaitUntil(args, start + (total - warning.Before))) return;
            await args.Launcher.BroadcastMessage(args.Instance, warning.Message, reason: args.Reason);
        }
        await WaitUntil(args, start + total);
    }

    /// <returns>false when the countdown can end early because the server is empty or no longer runs.</returns>
    private async Task<bool> WaitUntil(SequenceArgs args, DateTime utc)
    {
        while (true)
        {
            args.CancellationToken.ThrowIfCancellationRequested();
            // A server that crashed is neither cleaned up nor restarted while a sequence of it runs: a timed stop or
            // restart (up to a day) kept it down until its end. The countdown ends, the crash restart follows.
            if (!SequenceActionWaitOffline.IsServerRunning(args))
            {
                args.Logger.LogInformation("Server {Instance} no longer runs, its countdown ends", args.Instance);
                return false;
            }
            if (earlyWhenEmpty && IsServerEmpty(args))
            {
                args.Logger.LogInformation("No players are connected, initiating early restart of instance {Instance}", args.Instance);
                return false;
            }
            var remaining = utc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return true;
            await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), args.CancellationToken);
        }
    }

    private static bool IsServerEmpty(SequenceArgs args)
    {
        var process = args.Launcher.GetServerProcesses().FirstOrDefault(x => x.Infos.Instance == args.Instance);
        return process is null || process is { Online: true, Players: 0 };
    }
}
