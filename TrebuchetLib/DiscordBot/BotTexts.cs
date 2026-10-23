namespace TrebuchetLib.DiscordBot;

/// <summary>The texts of the status message and the answers of the bot.</summary>
public sealed record BotTexts
{
    public required string Instance { get; init; }
    public required string StateField { get; init; }
    public required string PlayersField { get; init; }
    public required string UptimeField { get; init; }
    public required string PlannedField { get; init; }
    public required string PostponedField { get; init; }
    public required string StateOnline { get; init; }
    public required string StateStarting { get; init; }
    public required string StateStopping { get; init; }
    public required string StateStopped { get; init; }
    public required string StateCrashed { get; init; }
    public required string PlannedRestart { get; init; }
    public required string PlannedStop { get; init; }
    public required string PlannedUpdate { get; init; }
    public required string Start { get; init; }
    public required string Stop { get; init; }
    public required string Restart { get; init; }
    public required string PostponeRestart { get; init; }
    public required string PostponeStop { get; init; }
    public required string PostponeUpdate { get; init; }
    public required string Resume { get; init; }
    public required string UpdatedFooter { get; init; }
    /// <summary>Title of an old status message after the admin channel moved: no buttons, no state.</summary>
    public required string MovedTitle { get; init; }

    public static BotTexts German { get; } = new()
    {
        Instance = "Instanz",
        StateField = "Zustand",
        PlayersField = "Spieler",
        UptimeField = "Läuft",
        PlannedField = "Geplant",
        PostponedField = "Verschoben bis",
        StateOnline = "🟢 Online",
        StateStarting = "🟡 Startet",
        StateStopping = "🟡 Wird gestoppt",
        StateStopped = "🔴 Gestoppt",
        StateCrashed = "🔴 Abgestürzt",
        PlannedRestart = "Neustart",
        PlannedStop = "Stopp",
        PlannedUpdate = "Update-Neustart",
        Start = "Starten",
        Stop = "Stoppen",
        Restart = "Neustart",
        PostponeRestart = "Neustart verschieben",
        PostponeStop = "Stopp verschieben",
        PostponeUpdate = "Update verschieben",
        Resume = "Wieder freigeben",
        UpdatedFooter = "Aktualisiert",
        MovedTitle = "Der Admin-Kanal wurde gewechselt. Diese Meldung gilt nicht mehr."
    };

    public static BotTexts English { get; } = new()
    {
        Instance = "Instance",
        StateField = "State",
        PlayersField = "Players",
        UptimeField = "Running",
        PlannedField = "Planned",
        PostponedField = "Postponed until",
        StateOnline = "🟢 Online",
        StateStarting = "🟡 Starting",
        StateStopping = "🟡 Stopping",
        StateStopped = "🔴 Stopped",
        StateCrashed = "🔴 Crashed",
        PlannedRestart = "Restart",
        PlannedStop = "Stop",
        PlannedUpdate = "Update restart",
        Start = "Start",
        Stop = "Stop",
        Restart = "Restart",
        PostponeRestart = "Postpone restart",
        PostponeStop = "Postpone stop",
        PostponeUpdate = "Postpone update",
        Resume = "Release again",
        UpdatedFooter = "Updated",
        MovedTitle = "The admin channel was changed. This message is no longer used."
    };

    public static BotTexts For(string? language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? English : German;
}
