namespace TrebuchetLib.DiscordBot;

/// <summary>What happened to the connection of the bot. Turned into <see cref="DiscordBotState"/> and <see cref="DiscordBotProblem"/>.</summary>
internal enum BotConnectionEvent
{
    Starting,
    SettingsInvalid,
    TokenMissing,
    TokenBusy,
    /// <summary>Discord refused the token.</summary>
    LoginRejected,
    /// <summary>Discord could not be reached for the login.</summary>
    LoginUnreachable,
    /// <summary>The gateway is connected (again): new session or resumed.</summary>
    GatewayConnected,
    GatewayDisconnected,
    /// <summary>Ready, and the configured server is there.</summary>
    ReadyVerified,
    /// <summary>The configured server is not there, or the bot was removed from it.</summary>
    GuildMissing,
    /// <summary>No ready within the connect deadline (Discord.Net keeps trying).</summary>
    ConnectDeadline,
    /// <summary>The settings were applied: valid or not.</summary>
    SettingsApplied,
    Stopping,
    Stopped,
    Failed
}

/// <summary>The rules of the state the panel shows. Pure, so every transition is tested.</summary>
internal static class BotConnectionStateMachine
{
    /// <param name="guildVerified">This connection had a ready with the configured server.</param>
    /// <param name="settingsValid">The current settings give a policy. Invalid ones keep the bot connected but allow nothing.</param>
    public static (DiscordBotState State, DiscordBotProblem Problem) Next(DiscordBotState state, DiscordBotProblem problem,
        BotConnectionEvent e, bool guildVerified, bool settingsValid)
    {
        var healthy = settingsValid ? DiscordBotProblem.None : DiscordBotProblem.InvalidSettings;
        // Gateway news only count while the bot is meant to run and connecting or connected.
        var live = state is DiscordBotState.Starting or DiscordBotState.Connected
                   || state == DiscordBotState.Error && problem == DiscordBotProblem.ConnectionLost;
        return e switch
        {
            BotConnectionEvent.Starting => (DiscordBotState.Starting, DiscordBotProblem.None),
            BotConnectionEvent.SettingsInvalid => (DiscordBotState.Error, DiscordBotProblem.InvalidSettings),
            BotConnectionEvent.TokenMissing => (DiscordBotState.Error, DiscordBotProblem.NoToken),
            BotConnectionEvent.TokenBusy => (DiscordBotState.Error, DiscordBotProblem.TokenInUse),
            BotConnectionEvent.LoginRejected => (DiscordBotState.Error, DiscordBotProblem.LoginFailed),
            BotConnectionEvent.LoginUnreachable => (DiscordBotState.Error, DiscordBotProblem.ConnectionLost),
            BotConnectionEvent.GuildMissing => (DiscordBotState.Error, DiscordBotProblem.GuildNotFound),
            BotConnectionEvent.Failed => (DiscordBotState.Error, DiscordBotProblem.Unknown),
            BotConnectionEvent.Stopping => (DiscordBotState.Stopping, DiscordBotProblem.None),
            BotConnectionEvent.Stopped => (DiscordBotState.Stopped, DiscordBotProblem.None),
            BotConnectionEvent.ReadyVerified when live => (DiscordBotState.Connected, healthy),
            // Back after a resume: only connected when this connection had seen the server.
            BotConnectionEvent.GatewayConnected when live && guildVerified => (DiscordBotState.Connected, healthy),
            // While starting a lost connection is retried, the deadline decides.
            BotConnectionEvent.GatewayDisconnected when state == DiscordBotState.Connected => (DiscordBotState.Error, DiscordBotProblem.ConnectionLost),
            BotConnectionEvent.ConnectDeadline when state == DiscordBotState.Starting => (DiscordBotState.Error, DiscordBotProblem.ConnectionLost),
            BotConnectionEvent.SettingsApplied when state == DiscordBotState.Connected => (DiscordBotState.Connected, healthy),
            _ => (state, problem)
        };
    }

    /// <summary>
    /// Which servers the bot leaves. Only when the configured server is there: if it is missing the settings are likely
    /// wrong, the bot disconnects and leaves nothing, so a typo in the server ID does not throw it off the real server.
    /// </summary>
    public static (bool Found, IReadOnlyList<ulong> Leave) PlanGuilds(ulong configuredGuild, IEnumerable<ulong> guilds)
    {
        var all = guilds.Distinct().ToList();
        var found = BotId.IsValid(configuredGuild) && all.Contains(configuredGuild);
        return (found, found ? all.Where(x => x != configuredGuild).ToList() : []);
    }
}
