namespace TrebuchetLib.DiscordBot;

/// <summary>The answers of the bot to commands, buttons and menus. Short, no details of the system.</summary>
public sealed record BotMessages
{
    public required string NoPermission { get; init; }
    public required string Wait { get; init; }
    public required string CriticalLimit { get; init; }
    public required string Expired { get; init; }
    public required string Cancelled { get; init; }
    public required string InvalidInput { get; init; }
    public required string UnknownServer { get; init; }
    public required string UnknownRequest { get; init; }
    public required string NoServers { get; init; }
    public required string StatusTitle { get; init; }
    public required string Resumed { get; init; }
    public required string NotRunning { get; init; }
    public required string AlreadyRunning { get; init; }
    public required string NothingPending { get; init; }
    public required string Busy { get; init; }
    public required string Failed { get; init; }
    public required string Running { get; init; }
    public required string PostponePlaceholder { get; init; }
    public required string Confirm { get; init; }
    public required string Cancel { get; init; }
    /// <summary>{0} minutes.</summary>
    public required string Postponed { get; init; }
    /// <summary>{0} server, {1} warning time text.</summary>
    public required string AskRestart { get; init; }
    public required string AskStop { get; init; }
    /// <summary>{0} server.</summary>
    public required string AskStart { get; init; }
    public required string AskUpdate { get; init; }
    public required string AskChannel { get; init; }
    public required string DoneRestart { get; init; }
    public required string DoneStop { get; init; }
    public required string DoneStart { get; init; }
    public required string DoneUpdate { get; init; }
    public required string DoneChannel { get; init; }
    public required string Now { get; init; }
    /// <summary>{0} minutes.</summary>
    public required string MinutesFormat { get; init; }
    /// <summary>{0} hours.</summary>
    public required string HoursFormat { get; init; }
    /// <summary>{0} hours, {1} minutes.</summary>
    public required string HoursMinutesFormat { get; init; }

    public static BotMessages German { get; } = new()
    {
        NoPermission = BotDecision.PublicMessage,
        Wait = "Bitte kurz warten.",
        CriticalLimit = "Zu viele kritische Aktionen für diesen Server. Bitte später noch einmal versuchen.",
        Expired = "Diese Anfrage ist abgelaufen. Bitte noch einmal starten.",
        Cancelled = "Abgebrochen.",
        InvalidInput = "Ungültige Eingabe.",
        UnknownServer = "Diesen Server gibt es nicht.",
        UnknownRequest = "Das kenne ich nicht.",
        NoServers = "Keine Server eingerichtet.",
        StatusTitle = "Status",
        Resumed = "Die automatischen Neustarts laufen wieder.",
        NotRunning = "Der Server läuft nicht.",
        AlreadyRunning = "Der Server läuft bereits.",
        NothingPending = "Es steht nichts an, das verschoben werden könnte.",
        Busy = "Gerade nicht möglich, es läuft schon etwas anderes.",
        Failed = "Das hat nicht geklappt. Details stehen im Protokoll von Trebuchet.",
        Running = "Wird ausgeführt, das dauert etwas. Der Status oben zeigt den Fortschritt.",
        PostponePlaceholder = "Um wie viel verschieben?",
        Confirm = "Bestätigen",
        Cancel = "Abbrechen",
        Postponed = "Verschoben um {0}.",
        AskRestart = "Server {0} neu starten? Die Spieler werden {1} vorher gewarnt.",
        AskStop = "Server {0} stoppen? Die Spieler werden {1} vorher gewarnt.",
        AskStart = "Server {0} starten?",
        AskUpdate = "Nach Updates suchen und sie installieren?",
        AskChannel = "Diesen Kanal zum Admin-Kanal machen? Der Bot antwortet danach nur noch hier.",
        DoneRestart = "Neustart von Server {0} gestartet.",
        DoneStop = "Stopp von Server {0} gestartet.",
        DoneStart = "Server {0} wird gestartet.",
        DoneUpdate = "Update-Prüfung gestartet.",
        DoneChannel = "Dieser Kanal ist jetzt der Admin-Kanal. Die Status-Meldungen erscheinen gleich hier.",
        Now = "sofort",
        MinutesFormat = "{0} Min",
        HoursFormat = "{0} Std",
        HoursMinutesFormat = "{0} Std {1} Min"
    };

    public static BotMessages English { get; } = new()
    {
        NoPermission = "No permission.",
        Wait = "Please wait a moment.",
        CriticalLimit = "Too many critical actions for this server. Please try again later.",
        Expired = "This request has expired. Please start again.",
        Cancelled = "Cancelled.",
        InvalidInput = "Invalid input.",
        UnknownServer = "That server does not exist.",
        UnknownRequest = "I do not know that.",
        NoServers = "No servers set up.",
        StatusTitle = "Status",
        Resumed = "Automatic restarts run again.",
        NotRunning = "The server is not running.",
        AlreadyRunning = "The server is already running.",
        NothingPending = "Nothing is pending that could be postponed.",
        Busy = "Not possible right now, something else is going on.",
        Failed = "That did not work. Details are in the Trebuchet log.",
        Running = "Running, this takes a while. The status above shows the progress.",
        PostponePlaceholder = "Postpone by how much?",
        Confirm = "Confirm",
        Cancel = "Cancel",
        Postponed = "Postponed by {0}.",
        AskRestart = "Restart server {0}? Players are warned {1} before.",
        AskStop = "Stop server {0}? Players are warned {1} before.",
        AskStart = "Start server {0}?",
        AskUpdate = "Check for updates and install them?",
        AskChannel = "Make this channel the admin channel? The bot only answers here afterwards.",
        DoneRestart = "Restart of server {0} started.",
        DoneStop = "Stop of server {0} started.",
        DoneStart = "Server {0} is starting.",
        DoneUpdate = "Update check started.",
        DoneChannel = "This channel is now the admin channel. The status messages appear here shortly.",
        Now = "right away",
        MinutesFormat = "{0} min",
        HoursFormat = "{0} h",
        HoursMinutesFormat = "{0} h {1} min"
    };

    public static BotMessages For(string? language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? English : German;

    /// <summary>"15 Min", "1 Std", "1 Std 30 Min".</summary>
    public string FormatMinutes(int minutes)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (minutes < 60) return string.Format(inv, MinutesFormat, minutes);
        return minutes % 60 == 0
            ? string.Format(inv, HoursFormat, minutes / 60)
            : string.Format(inv, HoursMinutesFormat, minutes / 60, minutes % 60);
    }
}
