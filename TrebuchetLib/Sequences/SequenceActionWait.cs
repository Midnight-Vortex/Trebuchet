namespace TrebuchetLib.Sequences;

public class SequenceActionWait : ISequenceAction
{
    public TimeSpan WaitTime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Like the Dedicated Server Launcher: stop waiting as soon as the server is online with no player connected,
    /// so restarts for updates do not keep an empty server down for nothing.
    /// </summary>
    public bool SkipIfNoPlayers { get; set; }

    public async Task Execute(SequenceArgs args)
    {
        var end = DateTime.UtcNow + WaitTime;
        while (DateTime.UtcNow < end)
        {
            args.CancellationToken.ThrowIfCancellationRequested();
            if (SkipIfNoPlayers && IsServerEmpty(args)) return;
            var remaining = end - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1),
                args.CancellationToken);
        }
    }

    private static bool IsServerEmpty(SequenceArgs args)
    {
        var process = args.Launcher.GetServerProcesses().FirstOrDefault(x => x.Infos.Instance == args.Instance);
        // Only trust the player count when the query port answers.
        return process is { Online: true, Players: 0 };
    }
}
