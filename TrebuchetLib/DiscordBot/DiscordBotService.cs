using System.Net;
using System.Net.WebSockets;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// The Discord bot: one outgoing gateway connection with the Guilds intent only, the slash commands of the one configured
/// server, the interactions (acknowledged at once, decided by <see cref="BotInteractionHandler"/>), the status messages
/// (<see cref="BotStatusBoard"/>) and the settings both of them read. It starts only with valid settings, a token no other
/// Trebuchet uses and the configured server present; it leaves every other server. Nothing it logs or exposes holds the
/// token, and nothing it does blocks or throws into the launcher.
/// </summary>
public sealed class DiscordBotService : IDiscordBotService, IBotSettings, IDisposable
{
    internal static readonly TimeSpan ConnectDeadline = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RoleFetchTimeout = TimeSpan.FromSeconds(5);
    private const int MaxInteractionsInFlight = 32;

    private readonly DiscordBotConfig _config;
    private readonly BotTokenStore _tokens;
    private readonly BotAuditLog _audit;
    private readonly IBotActions _actions;
    private readonly BotGuard _guard;
    private readonly BotPendingActions _pending;
    private readonly ILogger _logger;
    private readonly DiscordNetChannelClient _channel;
    // Start, stop and the fail-closed teardown, one at a time.
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _inFlight = new(MaxInteractionsInFlight, MaxInteractionsInFlight);
    private readonly object _stateLock = new();

    private volatile BotInteractionHandler? _handler;
    private volatile BotStatusBoard? _board;
    private CancellationTokenSource? _startCancel;
    private bool _disposed;

    // Guarded by _stateLock.
    private Session? _session;
    private DiscordBotState _state = DiscordBotState.Stopped;
    private DiscordBotProblem _problem = DiscordBotProblem.None;
    private IReadOnlyList<BotConfigIssue> _issues = [];
    private BotPolicy? _policy;
    private ulong _adminChannelId;
    private string? _botName;
    private string? _guildName;
    private int _latencyMs = -1;
    private DateTime? _connectedSinceUtc;

    public DiscordBotService(DiscordBotConfig config, BotTokenStore tokens, BotAuditLog audit, IBotActions actions, BotGuard guard,
        BotPendingActions pending, ILoggerFactory loggerFactory)
    {
        _config = config;
        _tokens = tokens;
        _audit = audit;
        _actions = actions;
        _guard = guard;
        _pending = pending;
        _logger = loggerFactory.CreateLogger<DiscordBotService>();
        _channel = new DiscordNetChannelClient(new SocketChannelTransport(() => CurrentSession?.Client), () => VerifiedGuildId, _logger);
    }

    public event EventHandler? StateChanged;

    public DiscordBotState State { get { lock (_stateLock) return _state; } }
    public DiscordBotProblem Problem { get { lock (_stateLock) return _problem; } }
    public IReadOnlyList<BotConfigIssue> Issues { get { lock (_stateLock) return _issues; } }
    public string? BotName { get { lock (_stateLock) return _botName; } }
    public string? GuildName { get { lock (_stateLock) return _guildName; } }
    public int LatencyMs { get { lock (_stateLock) return _latencyMs; } }
    public DateTime? ConnectedSinceUtc { get { lock (_stateLock) return _connectedSinceUtc; } }

    public BotPolicy? Policy { get { lock (_stateLock) return _policy; } }
    public ulong AdminChannelId { get { lock (_stateLock) return _adminChannelId; } }

    private Session? CurrentSession { get { lock (_stateLock) return _session; } }

    // The server the bot works for right now: only once the connection has seen it.
    private ulong VerifiedGuildId => CurrentSession is { Verified: true } session ? session.GuildId : 0;

    #region Start and stop

    public async Task StartAsync(CancellationToken token)
    {
        try
        {
            await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }
        try
        {
            bool disposed;
            Session? existing;
            lock (_stateLock)
            {
                disposed = _disposed;
                existing = _session;
            }
            if (disposed) return;
            if (existing is not null)
            {
                // Connected or still connecting: nothing to start. A connection that was lost (the gateway gave up, the
                // deadline passed) is ended and started anew: "start" of the panel and the retry of the headless host
                // must not be silent no-ops while the state says error.
                if (State != DiscordBotState.Error) return;
                await TearDownAsync(existing).ConfigureAwait(false);
            }
            using var startCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (_stateLock) _startCancel = startCancel;
            try
            {
                Apply(null, BotConnectionEvent.Starting);
                await StartCoreAsync(startCancel.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_stateLock) _startCancel = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Discord bot: start failed: {Error}", DiscordNetLog.Describe(ex));
            if (CurrentSession is { } session) await TearDownAsync(session).ConfigureAwait(false);
            Apply(null, BotConnectionEvent.Failed);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancel)
    {
        // 1. Settings: nothing starts without a policy.
        var policy = RebuildPolicy();
        if (!policy.IsValid)
        {
            _logger.LogWarning("Discord bot: not started, the settings are not valid ({Issues})",
                string.Join(", ", policy.Issues.Select(x => x.Kind).Distinct()));
            Apply(null, BotConnectionEvent.SettingsInvalid);
            return;
        }
        var guildId = policy.Policy!.GuildId;

        // 2. The token.
        string? secret;
        try
        {
            secret = _tokens.Load();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Discord bot: the token could not be read: {Error}", DiscordNetLog.Describe(ex));
            secret = null;
        }
        if (secret is null)
        {
            Apply(null, BotConnectionEvent.TokenMissing);
            return;
        }

        // 3. One bot per token.
        var tokenLock = BotTokenLock.TryAcquire(secret);
        if (tokenLock is null)
        {
            _logger.LogWarning("Discord bot: not started, another Trebuchet already runs a bot with this token");
            Apply(null, BotConnectionEvent.TokenBusy);
            return;
        }

        Session session;
        try
        {
            // An action (or the channel command) changed something: show it soon. Refused requests do not wake the loop.
            _handler ??= new BotInteractionHandler(_actions, _guard, _pending, _audit, this, _channel, logger: _logger)
            {
                AfterAction = _ =>
                {
                    Wake(CurrentSession);
                    return Task.CompletedTask;
                }
            };
            _board ??= new BotStatusBoard(_actions, _channel, this, logger: _logger);
            session = new Session(new DiscordSocketClient(CreateClientConfig()), tokenLock, guildId);
        }
        catch (Exception)
        {
            tokenLock.Dispose();
            throw;
        }
        Subscribe(session);
        lock (_stateLock) _session = session;
        session.Loop = Task.Run(() => RefreshLoopAsync(session), CancellationToken.None);

        // 4. Login (REST): a wrong token ends here.
        try
        {
            await session.Client.LoginAsync(TokenType.Bot, secret).WaitAsync(LoginTimeout, cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TearDownAsync(session).ConfigureAwait(false);
            if (cancel.IsCancellationRequested)
            {
                Apply(null, BotConnectionEvent.Stopped);
                return;
            }
            var problem = ClassifyLoginFailure(ex);
            _logger.LogWarning("Discord bot: login failed: {Error}", DiscordNetLog.Describe(ex));
            Apply(null, problem == DiscordBotProblem.LoginFailed ? BotConnectionEvent.LoginRejected
                : problem == DiscordBotProblem.ConnectionLost ? BotConnectionEvent.LoginUnreachable
                : BotConnectionEvent.Failed);
            return;
        }

        // 5. Gateway. Discord.Net reconnects by itself from here on.
        await session.Client.StartAsync().ConfigureAwait(false);
        var deadline = Task.Delay(ConnectDeadline, cancel);
        var first = await Task.WhenAny(session.Ready.Task, deadline).ConfigureAwait(false);
        if (cancel.IsCancellationRequested)
        {
            await TearDownAsync(session).ConfigureAwait(false);
            Apply(null, BotConnectionEvent.Stopped);
            return;
        }
        if (first != session.Ready.Task)
        {
            // Not connected in time: say so, Discord.Net keeps trying and a later ready still connects.
            _logger.LogWarning("Discord bot: not connected within {Seconds} s, still trying", (int)ConnectDeadline.TotalSeconds);
            Apply(null, BotConnectionEvent.ConnectDeadline);
            return;
        }
        if (await session.Ready.Task.ConfigureAwait(false) == ReadyOutcome.GuildMissing)
        {
            await TearDownAsync(session).ConfigureAwait(false);
            Apply(null, BotConnectionEvent.GuildMissing);
        }
    }

    public async Task StopAsync()
    {
        try
        {
            // A start that waits for Discord gives up at once. Cancelled outside the lock: the start continues inline.
            CancellationTokenSource? starting;
            lock (_stateLock) starting = _startCancel;
            try { starting?.Cancel(); } catch (ObjectDisposedException) { /* the start just ended */ }
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (CurrentSession is { } session)
                {
                    Apply(null, BotConnectionEvent.Stopping);
                    await TearDownAsync(session).ConfigureAwait(false);
                }
                Apply(null, BotConnectionEvent.Stopped);
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Discord bot: stop failed: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    /// <summary>For containers that dispose synchronously. Safe to call after <see cref="DisposeAsync"/> (it is idempotent).</summary>
    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(10));

    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Ends a connection that must not go on (the server is missing): fail closed.</summary>
    private async Task FailClosedAsync(Session session, BotConnectionEvent reason)
    {
        try
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(CurrentSession, session)) return;
                await TearDownAsync(session).ConfigureAwait(false);
                Apply(null, reason);
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Discord bot: could not end the connection: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    /// <summary>Disconnects, frees the token for other programs and forgets the connection. The state is set by the caller.</summary>
    private async Task TearDownAsync(Session session)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_session, session)) _session = null;
            _botName = null;
            _guildName = null;
            _latencyMs = -1;
            _connectedSinceUtc = null;
        }
        session.Verified = false;
        try { session.Cancel.Cancel(); } catch (Exception) { /* already gone */ }
        if (session.Loop is { } loop)
            await Quietly(loop.WaitAsync(TimeSpan.FromSeconds(5)), "refresh loop").ConfigureAwait(false);
        await Quietly(session.Client.StopAsync().WaitAsync(ShutdownTimeout), "stop").ConfigureAwait(false);
        await Quietly(session.Client.LogoutAsync().WaitAsync(ShutdownTimeout), "logout").ConfigureAwait(false);
        await Quietly(session.Client.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout), "dispose").ConfigureAwait(false);
        // Only now another Trebuchet may use the token.
        session.TokenLock.Dispose();
    }

    private async Task Quietly(Task task, string step)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Discord bot: {Step}: {Error}", step, DiscordNetLog.Describe(ex));
        }
    }

    internal static DiscordSocketConfig CreateClientConfig() => new()
    {
        // Slash commands, buttons and menus need nothing else. No privileged intent: no members, presences or message content.
        GatewayIntents = GatewayIntents.Guilds,
        AlwaysDownloadUsers = false,
        AlwaysDownloadDefaultStickers = false,
        AlwaysResolveStickers = false,
        MessageCacheSize = 0,
        LogLevel = LogSeverity.Info,
        LogGatewayIntentWarnings = false,
        SuppressUnknownDispatchWarnings = true,
        // The three seconds of an interaction count from its arrival, not from a possibly wrong clock of this computer.
        UseInteractionSnowflakeDate = false
    };

    internal static DiscordBotProblem ClassifyLoginFailure(Exception exception) => exception switch
    {
        HttpException { HttpCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => DiscordBotProblem.LoginFailed,
        // Discord.Net refused the form of the token.
        ArgumentException => DiscordBotProblem.LoginFailed,
        HttpException or HttpRequestException or TimeoutException or OperationCanceledException or WebSocketException
            or System.Net.Sockets.SocketException => DiscordBotProblem.ConnectionLost,
        _ => DiscordBotProblem.Unknown
    };

    #endregion

    #region Gateway events

    private void Subscribe(Session session)
    {
        var client = session.Client;
        client.Log += message =>
        {
            ForwardDiscordLog(message);
            return Task.CompletedTask;
        };
        // The handlers run on the gateway task: they must return at once, the work runs elsewhere.
        client.Ready += () =>
        {
            if (IsCurrent(session)) _ = Task.Run(() => OnReadyAsync(session));
            return Task.CompletedTask;
        };
        client.Connected += () =>
        {
            Apply(session, BotConnectionEvent.GatewayConnected);
            return Task.CompletedTask;
        };
        client.Disconnected += exception =>
        {
            if (IsCurrent(session))
                _logger.LogWarning("Discord bot: disconnected: {Error}", DiscordNetLog.Describe(exception));
            Apply(session, BotConnectionEvent.GatewayDisconnected);
            return Task.CompletedTask;
        };
        client.LatencyUpdated += (_, latency) =>
        {
            bool changed;
            lock (_stateLock)
            {
                changed = ReferenceEquals(_session, session) && _latencyMs != latency;
                if (changed) _latencyMs = latency;
            }
            if (changed) RaiseStateChanged();
            return Task.CompletedTask;
        };
        client.JoinedGuild += guild =>
        {
            var id = guild.Id;
            var name = guild.Name;
            if (IsCurrent(session) && id != session.GuildId) _ = Task.Run(() => LeaveGuildAsync(session, id, name));
            return Task.CompletedTask;
        };
        client.LeftGuild += guild =>
        {
            if (IsCurrent(session) && guild.Id == session.GuildId)
            {
                _logger.LogWarning("Discord bot: the bot was removed from its server");
                _ = Task.Run(() => FailClosedAsync(session, BotConnectionEvent.GuildMissing));
            }
            return Task.CompletedTask;
        };
        client.SlashCommandExecuted += command => OnInteraction(session, command);
        client.ButtonExecuted += component => OnInteraction(session, component);
        client.SelectMenuExecuted += component => OnInteraction(session, component);
    }

    /// <summary>Every message of Discord.Net, as one line without the token, into the log of Trebuchet.</summary>
    internal void ForwardDiscordLog(LogMessage message)
    {
        try
        {
            _logger.Log(DiscordNetLog.LevelOf(message.Severity), "Discord: {Message}", DiscordNetLog.Format(message));
        }
        catch (Exception)
        {
            // Logging must never break the gateway.
        }
    }

    /// <summary>Registers the slash commands for the server. A failure is logged and shown as a problem, never thrown.</summary>
    private async Task<bool> TryRegisterCommandsAsync(Session session)
    {
        session.LastCommandAttemptUtc = DateTime.UtcNow;
        try
        {
            var guild = session.Client.GetGuild(session.GuildId);
            if (guild is null) return false;
            await DiscordNetCommandRegistrar.RegisterAsync(session.Client, guild, BotInteractionHandler.SlashCommands, _logger).ConfigureAwait(false);
            session.CommandsRegistered = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("Discord bot: the slash commands could not be registered: {Error}", DiscordNetLog.Describe(ex));
            return false;
        }
    }

    private async Task OnReadyAsync(Session session)
    {
        try
        {
            var client = session.Client;
            var (found, leave) = BotConnectionStateMachine.PlanGuilds(session.GuildId, client.Guilds.Select(x => x.Id));
            if (!found)
            {
                _logger.LogWarning("Discord bot: the configured server {Guild} is not reachable, disconnecting (the bot is on {Count} other servers)",
                    session.GuildId, client.Guilds.Count);
                session.Ready.TrySetResult(ReadyOutcome.GuildMissing);
                _ = FailClosedAsync(session, BotConnectionEvent.GuildMissing);
                return;
            }
            foreach (var id in leave)
                await LeaveGuildAsync(session, id, client.GetGuild(id)?.Name).ConfigureAwait(false);

            var guild = client.GetGuild(session.GuildId);
            var commandsOk = session.CommandsRegistered || await TryRegisterCommandsAsync(session).ConfigureAwait(false);

            session.Verified = true;
            lock (_stateLock)
            {
                if (ReferenceEquals(_session, session))
                {
                    _botName = BotInputRules.SanitizeForLog(client.CurrentUser?.Username);
                    _guildName = BotInputRules.SanitizeForLog(guild.Name);
                    _latencyMs = client.Latency;
                }
            }
            Apply(session, commandsOk ? BotConnectionEvent.ReadyVerified : BotConnectionEvent.ReadyVerifiedCommandsFailed);
            session.Ready.TrySetResult(ReadyOutcome.Verified);
            _logger.LogInformation("Discord bot: connected to server {Guild}", session.GuildId);
            Interlocked.Exchange(ref session.ForceRefresh, 1);
            Wake(session);
        }
        catch (Exception ex)
        {
            _logger.LogError("Discord bot: ready failed: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    private async Task LeaveGuildAsync(Session session, ulong guildId, string? name)
    {
        try
        {
            var guild = session.Client.GetGuild(guildId);
            if (guild is null || guildId == session.GuildId) return;
            await guild.LeaveAsync().ConfigureAwait(false);
            _logger.LogWarning("Discord bot: left server {Guild} ({Name}), it is not the configured server", guildId, BotInputRules.SanitizeForLog(name));
        }
        catch (Exception ex)
        {
            _logger.LogError("Discord bot: could not leave server {Guild}: {Error}", guildId, DiscordNetLog.Describe(ex));
        }
    }

    #endregion

    #region Interactions

    private Task OnInteraction(Session session, SocketInteraction interaction)
    {
        if (!IsCurrent(session)) return Task.CompletedTask;
        BotCapturedInteraction captured;
        try
        {
            // Here, on the gateway task: the roles exactly as this interaction delivered them.
            captured = BotInteractionMapper.Capture(interaction);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Discord bot: could not read an interaction: {Error}", DiscordNetLog.Describe(ex));
            return Task.CompletedTask;
        }
        if (!_inFlight.Wait(0))
        {
            _logger.LogWarning("Discord bot: too many interactions at once, ignored one of user {User}", captured.Interaction.UserId);
            return Task.CompletedTask;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessAsync(session, interaction, captured).ConfigureAwait(false);
            }
            finally
            {
                _inFlight.Release();
            }
        });
        return Task.CompletedTask;
    }

    private async Task ProcessAsync(Session session, SocketInteraction interaction, BotCapturedInteraction captured)
    {
        var request = captured.Interaction;
        try
        {
            // 1. Discord wants an acknowledgment within three seconds. Without it nothing is done at all.
            try
            {
                await AcknowledgeAsync(interaction, captured.Ack).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Discord bot: interaction of user {User} not acknowledged, nothing done: {Error}", request.UserId, DiscordNetLog.Describe(ex));
                _audit.Write(new BotAuditEntry(DateTime.UtcNow, BotAuditResult.Failed, request.UserId, request.UserName, request.GuildId ?? 0,
                    request.ChannelId ?? 0, null, request.Name, "not acknowledged in time, nothing done"));
                return;
            }

            // 2. No member object in the interaction: fetch the roles fresh (only for the configured server).
            if (captured.NeedsRoles && request.GuildId is { } guildId && guildId == session.GuildId)
                request = request with { RoleIds = await FetchRolesAsync(session, guildId, request.UserId).ConfigureAwait(false) };

            // 3. Everything else is the handler's: gate, questions, action, audit, answer.
            var responder = new DiscordNetResponder(new SocketInteractionTransport(interaction), captured.Ack, _logger);
            try
            {
                if (_handler is not { } handler) throw new InvalidOperationException("No handler");
                await handler.HandleAsync(request, responder, session.Cancel.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Discord bot: the interaction of user {User} failed: {Error}", request.UserId, DiscordNetLog.Describe(ex));
                await responder.ReplyAsync(new BotReply(BotDecision.PublicMessage)).ConfigureAwait(false);
            }
            await responder.FinishAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError("Discord bot: interaction failed: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    private static Task AcknowledgeAsync(SocketInteraction interaction, BotAckKind ack) => interaction switch
    {
        // Our own private question: the answer replaces it later (type 6, no loading state).
        SocketMessageComponent component when ack == BotAckKind.UpdateQuestion => component.DeferAsync(),
        // A public message (or anything else): "thinking" in a new private message (type 5), the clicked message stays as it is.
        SocketMessageComponent component => component.DeferLoadingAsync(ephemeral: true),
        _ => interaction.DeferAsync(ephemeral: true)
    };

    private async Task<IReadOnlyCollection<ulong>> FetchRolesAsync(Session session, ulong guildId, ulong userId)
    {
        try
        {
            var member = await session.Client.Rest.GetGuildUserAsync(guildId, userId).WaitAsync(RoleFetchTimeout).ConfigureAwait(false);
            return BotInteractionMapper.CleanRoles(member?.RoleIds, guildId);
        }
        catch (Exception ex)
        {
            // No roles: the check refuses.
            _logger.LogWarning("Discord bot: could not fetch the roles of user {User}: {Error}", userId, DiscordNetLog.Describe(ex));
            return [];
        }
    }

    #endregion

    #region Status messages

    private async Task RefreshLoopAsync(Session session)
    {
        var token = session.Cancel.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // The commands could not be registered at ready (e.g. a missing invite scope): try again every minute.
                if (session.Verified && !session.CommandsRegistered && State == DiscordBotState.Connected
                    && DateTime.UtcNow - session.LastCommandAttemptUtc > CommandRetryInterval
                    && await TryRegisterCommandsAsync(session).ConfigureAwait(false))
                    Apply(session, BotConnectionEvent.ReadyVerified);
                if (session.Verified && State == DiscordBotState.Connected && _board is { } board)
                {
                    var force = Interlocked.Exchange(ref session.ForceRefresh, 0) == 1;
                    await board.RefreshAsync(token, force).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Discord bot: status refresh failed: {Error}", DiscordNetLog.Describe(ex));
            }

            try
            {
                await session.Wake.WaitAsync(RefreshInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void Wake(Session? session)
    {
        try
        {
            session?.Wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
        catch (ObjectDisposedException)
        {
            // Gone.
        }
    }

    /// <summary>The board forgets its messages with its next refresh (it serializes that itself, no lock needed here).</summary>
    private void ResetBoard()
    {
        try
        {
            _board?.Reset();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Discord bot: status board reset failed: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    #endregion

    #region Settings

    public Task ApplySettingsAsync()
    {
        try
        {
            var oldChannel = AdminChannelId;
            RebuildPolicy();
            var session = CurrentSession;
            Apply(null, BotConnectionEvent.SettingsApplied);
            RaiseStateChanged();

            ulong configuredGuild;
            lock (_config) configuredGuild = _config.GuildId;
            if (session is not null && configuredGuild != session.GuildId)
            {
                // Another server needs a new connection: its commands, its check, leaving the old one.
                _logger.LogInformation("Discord bot: the server changed, reconnecting");
                ForgetChannelsOfOldGuild();
                _ = Task.Run(async () =>
                {
                    await StopAsync().ConfigureAwait(false);
                    await StartAsync(CancellationToken.None).ConfigureAwait(false);
                });
                return Task.CompletedTask;
            }
            if (AdminChannelId != oldChannel) ResetBoard();
            if (session is not null) Interlocked.Exchange(ref session.ForceRefresh, 1);
            Wake(session);
        }
        catch (Exception ex)
        {
            _logger.LogError("Discord bot: applying the settings failed: {Error}", DiscordNetLog.Describe(ex));
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The admin channel and the status messages belong to the old server: kept, every command on the new one would be
    /// refused for the wrong channel and the board would try to write to a channel it can not find.
    /// </summary>
    private void ForgetChannelsOfOldGuild()
    {
        try
        {
            lock (_config)
            {
                _config.AllowedChannelIds = [];
                _config.StatusMessageIds = [];
                _config.SaveFile();
            }
            RebuildPolicy();
        }
        catch (Exception ex)
        {
            _logger.LogError("Discord bot: could not reset the admin channel for the new server: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    public async Task SetAdminChannelAsync(ulong channelId)
    {
        if (!BotId.IsValid(channelId)) throw new ArgumentException("Not a Discord channel ID", nameof(channelId));
        ulong previousChannel;
        Dictionary<int, ulong> previousMessages;
        lock (_config)
        {
            var oldChannels = _config.AllowedChannelIds.ToList();
            var oldMessages = new Dictionary<int, ulong>(_config.StatusMessageIds);
            previousChannel = oldChannels.FirstOrDefault();
            previousMessages = oldMessages;
            _config.SetAdminChannel(channelId);
            try
            {
                _config.SaveFile();
            }
            catch (Exception ex)
            {
                // All or nothing: the old settings stay.
                _config.AllowedChannelIds = oldChannels;
                _config.StatusMessageIds = oldMessages;
                _logger.LogError("Discord bot: the admin channel could not be saved: {Error}", DiscordNetLog.Describe(ex));
                throw new IOException("The settings of the Discord bot could not be saved.");
            }
        }
        RebuildPolicy();
        ResetBoard();
        RaiseStateChanged();
        var session = CurrentSession;
        if (session is not null) Interlocked.Exchange(ref session.ForceRefresh, 1);
        Wake(session);
        if (previousChannel != 0 && previousChannel != channelId)
            _ = Task.Run(() => RetireOldStatusMessagesAsync(previousChannel, previousMessages));
    }

    /// <summary>
    /// The status messages of the old admin channel would keep their buttons: clicking them can only be refused. They
    /// are replaced by a short note without buttons. Best effort, a failure is only logged.
    /// </summary>
    private async Task RetireOldStatusMessagesAsync(ulong channelId, Dictionary<int, ulong> messages)
    {
        try
        {
            var texts = BotTexts.For(Policy?.Language);
            var note = new BotStatusMessage(texts.MovedTitle, 0x95A5A6, [], [], texts.UpdatedFooter);
            foreach (var id in messages.Values.Where(x => x != 0).Distinct())
                await _channel.EditStatusAsync(channelId, id, note).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Discord bot: could not retire the status messages of the old admin channel: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    public ulong GetStatusMessageId(int instance)
    {
        lock (_config) return _config.StatusMessageIds.TryGetValue(instance, out var id) ? id : 0;
    }

    public void SetStatusMessageId(int instance, ulong messageId)
    {
        lock (_config)
        {
            var known = _config.StatusMessageIds.TryGetValue(instance, out var old);
            if (messageId == 0 ? !known : known && old == messageId) return;
            if (messageId == 0) _config.StatusMessageIds.Remove(instance);
            else _config.StatusMessageIds[instance] = messageId;
            try
            {
                _config.SaveFile();
            }
            catch (Exception ex)
            {
                // Kept in memory: after a restart a new status message is posted, nothing worse.
                _logger.LogWarning("Discord bot: the status message could not be saved: {Error}", DiscordNetLog.Describe(ex));
            }
        }
    }

    /// <summary>A new policy from the settings as they are now. Invalid settings: no policy, nobody is allowed anything.</summary>
    private BotPolicyResult RebuildPolicy()
    {
        BotPolicyResult result;
        ulong admin;
        lock (_config)
        {
            result = BotPolicy.Create(_config);
            var first = _config.AllowedChannelIds.FirstOrDefault();
            admin = BotId.IsValid(first) ? first : 0;
        }
        lock (_stateLock)
        {
            _policy = result.Policy;
            _issues = result.Issues;
            _adminChannelId = admin;
        }
        return result;
    }

    #endregion

    #region Security check

    public Task<IReadOnlyList<BotCheckItem>> CheckSecurityAsync(CancellationToken token)
    {
        try
        {
            var stored = false;
            var readable = false;
            try
            {
                stored = _tokens.HasToken;
                readable = _tokens.Load() is not null;
            }
            catch (Exception)
            {
                readable = false;
            }

            BotPolicyResult policy;
            ulong guildId;
            List<ulong> roles;
            ulong admin;
            lock (_config)
            {
                policy = BotPolicy.Create(_config);
                guildId = _config.GuildId;
                roles = _config.AllowedRoleIds.ToList();
                admin = _config.AllowedChannelIds.FirstOrDefault();
            }

            var session = CurrentSession;
            var connected = session is { Verified: true } && State == DiscordBotState.Connected;
            var live = connected ? BotSecurityChecks.SnapshotOf(session!.Client, guildId, admin) : null;
            return Task.FromResult(BotSecurityChecks.Build(new BotSecurityInput(stored, readable, policy.Issues, guildId, roles, admin, connected, live)));
        }
        catch (Exception ex)
        {
            _logger.LogError("Discord bot: the security check failed: {Error}", DiscordNetLog.Describe(ex));
            return Task.FromResult<IReadOnlyList<BotCheckItem>>([new BotCheckItem(BotCheckKind.Connection, BotCheckStatus.Error, "check failed")]);
        }
    }

    #endregion

    #region State

    private bool IsCurrent(Session session)
    {
        lock (_stateLock) return ReferenceEquals(_session, session);
    }

    /// <param name="session">Gateway news: only for the current connection. Null: start, stop and teardown.</param>
    private void Apply(Session? session, BotConnectionEvent e)
    {
        bool changed;
        lock (_stateLock)
        {
            if (session is not null && !ReferenceEquals(_session, session)) return;
            var verified = _session is { Verified: true };
            var (state, problem) = BotConnectionStateMachine.Next(_state, _problem, e, verified, _policy is not null);
            changed = state != _state || problem != _problem;
            if (state == DiscordBotState.Connected && _state != DiscordBotState.Connected) _connectedSinceUtc = DateTime.UtcNow;
            if (state != DiscordBotState.Connected) _connectedSinceUtc = null;
            _state = state;
            _problem = problem;
        }
        if (changed) RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Discord bot: a state listener failed: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    #endregion

    private enum ReadyOutcome { Verified, GuildMissing }

    /// <summary>One connection: its client, the token lock and what it has seen. A new start makes a new one.</summary>
    private sealed class Session(DiscordSocketClient client, BotTokenLock tokenLock, ulong guildId)
    {
        public DiscordSocketClient Client { get; } = client;
        public BotTokenLock TokenLock { get; } = tokenLock;
        /// <summary>The configured server at the start of this connection.</summary>
        public ulong GuildId { get; } = guildId;
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource<ReadyOutcome> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim Wake { get; } = new(0, 1);
        public Task? Loop { get; set; }
        public volatile bool Verified;
        public volatile bool CommandsRegistered;
        public DateTime LastCommandAttemptUtc;
        public int ForceRefresh = 1;
    }
}
