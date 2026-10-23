using Microsoft.Extensions.Logging;
using TrebuchetLib;
using TrebuchetLib.DiscordBot;
using TrebuchetLib.Services;

namespace Boulder;

/// <summary>Exit codes of <c>boulder bot</c>.</summary>
public enum BotHostExitCode
{
    /// <summary>Ended by Ctrl+C or a stop signal.</summary>
    Stopped = 0,
    /// <summary>The window or another <c>boulder bot</c> already controls the servers.</summary>
    AlreadyRunning = 1,
    /// <summary>No settings, settings that can not be read, or settings that are not valid.</summary>
    InvalidSettings = 2,
    /// <summary>No token in the protected file or the environment variable.</summary>
    NoToken = 3,
    /// <summary>Another Trebuchet already runs a bot with this token.</summary>
    TokenInUse = 4,
    /// <summary>Discord refused the token, the bot is not on the server, or the start failed.</summary>
    StartFailed = 5
}

public enum BotHostTokenCheck { Found, Missing, VariableNotAToken }

/// <summary>What the host is made of: the command uses the installation, the checks use stand-ins.</summary>
public interface IBotHostParts
{
    GameEdition Edition { get; }

    /// <summary>The lock the window takes: one controller of the servers. Null: the window or another host has it.</summary>
    IDisposable? TryLockInstallation();

    /// <summary>The settings of the bot. Null: there are none (nothing is created). Throws when they can not be read.</summary>
    DiscordBotConfig? LoadSettings();

    BotHostTokenCheck CheckToken();

    /// <summary>The launcher thread. The tick and the bot use the launcher through this one only.</summary>
    SerialBotDispatcher GetDispatcher();

    /// <summary>Called on the launcher thread.</summary>
    Task AttachRunningServers();

    /// <summary>Called on the launcher thread.</summary>
    Task Tick();

    IDiscordBotService CreateBot();
}

public sealed record BotHostTimings
{
    public TimeSpan TickInterval { get; init; } = LauncherTickLoop.DefaultInterval;
    public TimeSpan TickTimeout { get; init; } = LauncherTickLoop.DefaultTimeout;
    public TimeSpan TickStopWait { get; init; } = LauncherTickLoop.DefaultStopWait;
    /// <summary>First wait before the bot is started again after it lost Discord. Doubles up to <see cref="RetryMax"/>.</summary>
    public TimeSpan RetryFirst { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryMax { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StatePoll { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Longest wait for one step of the shutdown.</summary>
    public TimeSpan StopLimit { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Trebuchet without the window, for one edition. It takes the lock of the window, so it is the one controller of the
/// servers, attaches the servers that run, ticks the launcher like the window and runs the Discord bot until it is told
/// to stop. Nothing is touched with missing or invalid settings or without a token. At the end the bot logs out and the
/// servers keep running, like when the window is closed. Nothing it prints holds the token or a path.
/// </summary>
public sealed class BotHost(IBotHostParts parts, ILogger logger, BotHostTimings? timings = null)
{
    private const string TokenVariable = BotTokenStore.EnvironmentVariable;
    private readonly BotHostTimings _timings = timings ?? new BotHostTimings();
    private readonly Lock _shownLock = new();
    private (DiscordBotState, DiscordBotProblem)? _shown;

    public async Task<int> RunAsync(CancellationToken stop)
    {
        using var installation = TryLockInstallation();
        if (installation is null)
        {
            logger.LogError("Trebuchet already runs on this computer (the window or another \"boulder bot\"). " +
                            "Only one of them may control the servers: close it first.");
            return (int)BotHostExitCode.AlreadyRunning;
        }

        // Before anything touches the servers.
        if (CheckSettings() is { } settingsProblem) return (int)settingsProblem;
        if (CheckToken() is { } tokenProblem) return (int)tokenProblem;
        if (stop.IsCancellationRequested) return (int)BotHostExitCode.Stopped;

        var edition = Constants.GetEditionDisplayName(parts.Edition);
        logger.LogInformation("Trebuchet {Edition} runs without the window: server automation and Discord bot. " +
                              "Ctrl+C ends it, the servers keep running.", edition);

        var dispatcher = parts.GetDispatcher();
        using var ticksStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var ticking = Task.CompletedTask;
        IDiscordBotService? bot = null;
        var exit = BotHostExitCode.Stopped;
        try
        {
            await AttachAsync(dispatcher, stop);
            var loop = new LauncherTickLoop(() => dispatcher.InvokeAsync(async () =>
            {
                await parts.Tick();
                return true;
            }), logger)
            {
                Interval = _timings.TickInterval,
                Timeout = _timings.TickTimeout,
                StopWait = _timings.TickStopWait
            };
            ticking = loop.RunAsync(ticksStop.Token);

            bot = CreateBot();
            if (bot is null)
            {
                exit = BotHostExitCode.StartFailed;
            }
            else
            {
                bot.StateChanged += OnBotStateChanged;
                await StartBotAsync(bot, stop);
                exit = ExitAfterStart(bot, stop) ?? await KeepRunningAsync(bot, stop);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "\"boulder bot\" failed");
            exit = BotHostExitCode.StartFailed;
        }
        finally
        {
            await ShutdownAsync(bot, dispatcher, ticksStop, ticking);
        }
        logger.LogInformation("Trebuchet {Edition} without the window ended. The servers keep running.", edition);
        return (int)exit;
    }

    private IDisposable? TryLockInstallation()
    {
        try
        {
            return parts.TryLockInstallation();
        }
        catch (Exception)
        {
            // Fail closed: without the lock this is not the one controller of the servers.
            return null;
        }
    }

    #region Checks

    private BotHostExitCode? CheckSettings()
    {
        DiscordBotConfig? config;
        try
        {
            config = parts.LoadSettings();
        }
        catch (Exception)
        {
            logger.LogError("The settings of the Discord bot could not be read. Open them in the Trebuchet window " +
                            "(menu \"Discord Bot\") and save them again.");
            return BotHostExitCode.InvalidSettings;
        }
        if (config is null)
        {
            logger.LogError("The Discord bot of {Edition} is not set up. Set it up in the Trebuchet window (menu \"Discord Bot\") " +
                            "and close the window, or check the edition flags (--live, --enhanced, --ptc).",
                Constants.GetEditionDisplayName(parts.Edition));
            return BotHostExitCode.InvalidSettings;
        }

        BotPolicyResult policy;
        lock (config) policy = BotPolicy.Create(config);
        if (policy.IsValid) return null;
        logger.LogError("The settings of the Discord bot are not valid, it does not start:");
        LogIssues(policy.Issues);
        return BotHostExitCode.InvalidSettings;
    }

    private void LogIssues(IEnumerable<BotConfigIssue> issues)
    {
        foreach (var line in issues.Select(x => Describe(x)).Distinct())
            logger.LogError("  - {Issue}", line);
    }

    /// <summary>What is wrong, in one line. The details are IDs and numbers of the settings, never the token.</summary>
    public static string Describe(BotConfigIssue issue)
    {
        var detail = BotInputRules.SanitizeForLog(BotLogRedactor.Redact(issue.Detail));
        return issue.Kind switch
        {
            BotConfigIssueKind.GuildMissing => "the server ID (guild) is missing",
            BotConfigIssueKind.SuperAdminMissing => "the user ID of the super admin is missing",
            BotConfigIssueKind.NoRoles => "no role is allowed: at least one role ID is needed (user ID and role are both required)",
            BotConfigIssueKind.EveryoneRole => "the role @everyone is not allowed",
            BotConfigIssueKind.InvalidId => "not a Discord ID (17 to 20 digits): " + detail,
            BotConfigIssueKind.InvalidLimit => "a limit is out of range: " + detail,
            BotConfigIssueKind.InvalidPostponeChoices => "the postpone choices are not valid: " + detail,
            BotConfigIssueKind.UnknownCommand => "unknown command: " + detail,
            _ => issue.Kind + " " + detail
        };
    }

    private BotHostExitCode? CheckToken()
    {
        BotHostTokenCheck check;
        try
        {
            check = parts.CheckToken();
        }
        catch (Exception)
        {
            check = BotHostTokenCheck.Missing;
        }
        switch (check)
        {
            case BotHostTokenCheck.Found:
                return null;
            case BotHostTokenCheck.VariableNotAToken:
                logger.LogError("The environment variable " + TokenVariable + " is set but holds no bot token.");
                return BotHostExitCode.NoToken;
            default:
                if (OperatingSystem.IsWindows())
                    logger.LogError("No bot token. Store it in the Trebuchet window (menu \"Discord Bot\") or set the " +
                                    "environment variable " + TokenVariable + ".");
                else
                    logger.LogError("No bot token. Set the environment variable " + TokenVariable + " (the only way without Windows).");
                return BotHostExitCode.NoToken;
        }
    }

    #endregion

    #region Launcher and bot

    private async Task AttachAsync(SerialBotDispatcher dispatcher, CancellationToken stop)
    {
        try
        {
            var attach = dispatcher.InvokeAsync(async () =>
            {
                await parts.AttachRunningServers();
                return true;
            });
            // Its error is read here when the host does not wait for it any more.
            _ = attach.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (await EndsWithinAsync(attach, _timings.TickTimeout, stop))
                await attach;
            else if (!stop.IsCancellationRequested)
                logger.LogError("Attaching the running servers did not complete within {Timeout}, the tick waits for it", _timings.TickTimeout);
        }
        catch (Exception ex)
        {
            // The tick looks for the running servers again before it starts any.
            if (!stop.IsCancellationRequested) logger.LogError(ex, "Attaching the running servers failed");
        }
    }

    private IDiscordBotService? CreateBot()
    {
        try
        {
            return parts.CreateBot();
        }
        catch (Exception ex)
        {
            logger.LogError("The Discord bot could not be created: {Error}", BotLogRedactor.Redact(ex.Message));
            return null;
        }
    }

    private async Task StartBotAsync(IDiscordBotService bot, CancellationToken stop)
    {
        try
        {
            await bot.StartAsync(stop);
        }
        catch (Exception ex)
        {
            // It never throws; if it does, the state says what is left.
            logger.LogError("The Discord bot could not start: {Error}", BotLogRedactor.Redact(ex.Message));
        }
    }

    /// <summary>The first start decides: a problem that only the settings or the user can fix ends the host.</summary>
    private BotHostExitCode? ExitAfterStart(IDiscordBotService bot, CancellationToken stop)
    {
        if (stop.IsCancellationRequested) return BotHostExitCode.Stopped;
        var (state, problem) = (bot.State, bot.Problem);
        // Connected, or still connecting (Discord.Net keeps trying, a later ready connects).
        if (state is DiscordBotState.Starting or DiscordBotState.Connected) return null;
        // Discord not reachable (yet, e.g. right after a reboot): started again later.
        if (state == DiscordBotState.Error && problem == DiscordBotProblem.ConnectionLost) return null;
        var exit = state != DiscordBotState.Error ? BotHostExitCode.StartFailed : problem switch
        {
            DiscordBotProblem.InvalidSettings => BotHostExitCode.InvalidSettings,
            DiscordBotProblem.NoToken => BotHostExitCode.NoToken,
            DiscordBotProblem.TokenInUse => BotHostExitCode.TokenInUse,
            _ => BotHostExitCode.StartFailed
        };
        ShowState(state, problem, null, null);
        if (problem == DiscordBotProblem.InvalidSettings) LogIssues(bot.Issues);
        logger.LogError("The Discord bot did not start, \"boulder bot\" ends.");
        return exit;
    }

    /// <summary>Until the stop. A bot that lost Discord is started again, waiting longer each time.</summary>
    private async Task<BotHostExitCode> KeepRunningAsync(IDiscordBotService bot, CancellationToken stop)
    {
        var delay = _timings.RetryFirst;
        DateTime? retryAt = null;
        while (true)
        {
            try
            {
                await Task.Delay(_timings.StatePoll, stop);
            }
            catch (OperationCanceledException)
            {
                return BotHostExitCode.Stopped;
            }

            if (bot.State == DiscordBotState.Error && bot.Problem is DiscordBotProblem.ConnectionLost or DiscordBotProblem.Unknown)
            {
                var now = DateTime.UtcNow;
                retryAt ??= now + delay;
                if (now < retryAt) continue;
                logger.LogInformation("Discord bot: connecting again");
                await StartBotAsync(bot, stop);
                delay = delay * 2 < _timings.RetryMax ? delay * 2 : _timings.RetryMax;
                retryAt = null;
            }
            else
            {
                retryAt = null;
                if (bot.State == DiscordBotState.Connected) delay = _timings.RetryFirst;
            }
        }
    }

    private void OnBotStateChanged(object? sender, EventArgs e)
    {
        try
        {
            if (sender is IDiscordBotService bot)
                ShowState(bot.State, bot.Problem, bot.GuildName, bot.BotName);
        }
        catch (Exception)
        {
            // Printing must never break the bot.
        }
    }

    /// <summary>Prints a change of state (not of the latency). Names come cleaned from the bot, never the token.</summary>
    private void ShowState(DiscordBotState state, DiscordBotProblem problem, string? guild, string? botName)
    {
        lock (_shownLock)
        {
            if (_shown == (state, problem)) return;
            _shown = (state, problem);
        }
        switch (state)
        {
            case DiscordBotState.Connected when problem == DiscordBotProblem.InvalidSettings:
                logger.LogWarning("Discord bot: connected, but the settings are not valid: it allows nothing");
                break;
            case DiscordBotState.Connected:
                logger.LogInformation("Discord bot: connected to {Guild} as {Bot}", guild ?? "-", botName ?? "-");
                break;
            case DiscordBotState.Error:
                logger.LogError("Discord bot: {Problem}", Describe(problem));
                break;
            default:
                logger.LogInformation("Discord bot: {State}", state.ToString().ToLowerInvariant());
                break;
        }
    }

    public static string Describe(DiscordBotProblem problem) => problem switch
    {
        DiscordBotProblem.NoToken => "no token",
        DiscordBotProblem.InvalidSettings => "the settings are not valid",
        DiscordBotProblem.TokenInUse => "another Trebuchet already runs a bot with this token (the window or \"boulder bot\" of another edition)",
        DiscordBotProblem.LoginFailed => "Discord refused the token (reset it in the developer portal and store the new one)",
        DiscordBotProblem.GuildNotFound => "the bot is not on the configured Discord server (invite it, check the server ID)",
        DiscordBotProblem.ConnectionLost => "no connection to Discord, trying again",
        _ => "the start failed"
    };

    #endregion

    /// <summary>The bot logs out, the tick ends, launcher work still queued is dropped. The servers keep running.</summary>
    private async Task ShutdownAsync(IDiscordBotService? bot, SerialBotDispatcher dispatcher, CancellationTokenSource ticksStop, Task ticking)
    {
        if (bot is not null)
        {
            await QuietlyAsync(bot.StopAsync, "stopping the Discord bot");
            await QuietlyAsync(() => bot.DisposeAsync().AsTask(), "disposing the Discord bot");
            bot.StateChanged -= OnBotStateChanged;
        }
        await ticksStop.CancelAsync();
        await QuietlyAsync(() => ticking, "ending the launcher tick");
        dispatcher.Dispose();
    }

    private async Task QuietlyAsync(Func<Task> step, string what)
    {
        try
        {
            await step().WaitAsync(_timings.StopLimit);
        }
        catch (Exception ex)
        {
            logger.LogWarning("{Step} failed: {Error}", what, BotLogRedactor.Redact(ex.Message));
        }
    }

    private static async Task<bool> EndsWithinAsync(Task task, TimeSpan limit, CancellationToken token)
    {
        if (task.IsCompleted) return true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var first = await Task.WhenAny(task, Task.Delay(limit, cancel.Token));
        await cancel.CancelAsync();
        return first == task;
    }
}
