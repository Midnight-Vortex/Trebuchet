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
    public required string CountdownCancelled { get; init; }
    public required string NothingToCancel { get; init; }
    public required string NotRunning { get; init; }
    public required string AlreadyRunning { get; init; }
    public required string NothingPending { get; init; }
    public required string Busy { get; init; }
    public required string Failed { get; init; }
    public required string Running { get; init; }
    public required string PostponePlaceholder { get; init; }
    public required string DelayPlaceholder { get; init; }
    public required string Confirm { get; init; }
    public required string Cancel { get; init; }
    /// <summary>{0} minutes.</summary>
    public required string Postponed { get; init; }
    /// <summary>{0} minutes. Less than asked for: the longest postponement is reached.</summary>
    public required string PostponedLimited { get; init; }
    public required string PostponeLimit { get; init; }
    /// <summary>{0} server, {1} warning time text.</summary>
    public required string AskRestart { get; init; }
    public required string AskStop { get; init; }
    /// <summary>{0} server.</summary>
    public required string AskStart { get; init; }
    /// <summary>{0} server. The question that comes with the menu of warning times.</summary>
    public required string AskRestartDelay { get; init; }
    public required string AskStopDelay { get; init; }
    public required string AskUpdate { get; init; }
    public required string AskChannel { get; init; }
    public required string DoneRestart { get; init; }
    public required string DoneStop { get; init; }
    public required string DoneStart { get; init; }
    public required string DoneUpdate { get; init; }
    /// <summary>{0} number of mods with an update, {1} the server build text.</summary>
    public required string DoneCheckUpdates { get; init; }
    public required string ServerNewVersion { get; init; }
    public required string ServerCurrent { get; init; }
    public required string DoneChannel { get; init; }
    public required string Now { get; init; }
    /// <summary>The entry of the menu of warning times for "no warning".</summary>
    public required string NowChoice { get; init; }
    /// <summary>{0} minutes.</summary>
    public required string MinutesFormat { get; init; }
    /// <summary>{0} hours.</summary>
    public required string HoursFormat { get; init; }
    /// <summary>{0} hours, {1} minutes.</summary>
    public required string HoursMinutesFormat { get; init; }
    /// <summary>Title of the window for the reason, {0} the warning time or the postponement (at most 45 characters in all).</summary>
    public required string ModalStop { get; init; }
    public required string ModalRestart { get; init; }
    public required string ModalPostpone { get; init; }
    public required string ReasonLabel { get; init; }
    public required string ReasonPlaceholder { get; init; }

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
        CountdownCancelled = "Abgebrochen: Der angekündigte Neustart oder Stopp findet nicht statt, die Spieler wurden informiert.",
        NothingToCancel = "Es läuft kein Countdown, der abgebrochen werden könnte.",
        NotRunning = "Der Server läuft nicht.",
        AlreadyRunning = "Der Server läuft bereits.",
        NothingPending = "Es steht nichts an, das verschoben werden könnte.",
        Busy = "Gerade nicht möglich, es läuft schon etwas anderes.",
        Failed = "Das hat nicht geklappt. Details stehen im Protokoll von Trebuchet.",
        Running = "Wird ausgeführt, das dauert etwas. Der Status oben zeigt den Fortschritt.",
        PostponePlaceholder = "Um wie viel verschieben?",
        DelayPlaceholder = "Warnzeit für die Spieler wählen",
        Confirm = "Bestätigen",
        Cancel = "Abbrechen",
        Postponed = "Verschoben um {0}.",
        PostponedLimited = "Verschoben um {0}. Das ist die längste Verschiebung, weiter geht es erst nach dem Neustart.",
        PostponeLimit = "Die längste Verschiebung ist ausgeschöpft. Es kann erst nach dem Neustart wieder verschoben werden.",
        AskRestart = "Server {0} neu starten? Die Spieler werden {1} vorher gewarnt.",
        AskStop = "Server {0} stoppen? Die Spieler werden {1} vorher gewarnt.",
        AskStart = "Server {0} starten?",
        AskRestartDelay = "Server {0} neu starten: Wann? Bei einer Wartezeit werden die Spieler vorher gewarnt.",
        AskStopDelay = "Server {0} stoppen: Wann? Bei einer Wartezeit werden die Spieler vorher gewarnt.",
        AskUpdate = "Nach Updates suchen und sie installieren?",
        AskChannel = "Diesen Kanal zum Admin-Kanal machen? Der Bot antwortet danach nur noch hier.",
        DoneRestart = "Neustart von Server {0} gestartet.",
        DoneStop = "Stopp von Server {0} gestartet.",
        DoneStart = "Server {0} wird gestartet.",
        DoneUpdate = "Update-Prüfung gestartet.",
        DoneCheckUpdates = "Update-Prüfung fertig. Mod-Updates: {0}. Server-Version: {1}. Es wurde nichts installiert.",
        ServerNewVersion = "neue Version verfügbar",
        ServerCurrent = "aktuell",
        DoneChannel = "Dieser Kanal ist jetzt der Admin-Kanal. Die Status-Meldungen erscheinen gleich hier.",
        Now = "sofort",
        NowChoice = "Sofort",
        MinutesFormat = "{0} Min",
        HoursFormat = "{0} Std",
        HoursMinutesFormat = "{0} Std {1} Min",
        ModalStop = "Stopp ({0}) – Grund",
        ModalRestart = "Neustart ({0}) – Grund",
        ModalPostpone = "Verschieben um {0} – Grund",
        ReasonLabel = "Grund (kann leer bleiben)",
        ReasonPlaceholder = "z. B. Wartung, Update, Absprache"
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
        CountdownCancelled = "Cancelled: the announced restart or stop does not happen, the players were told.",
        NothingToCancel = "No countdown is running that could be cancelled.",
        NotRunning = "The server is not running.",
        AlreadyRunning = "The server is already running.",
        NothingPending = "Nothing is pending that could be postponed.",
        Busy = "Not possible right now, something else is going on.",
        Failed = "That did not work. Details are in the Trebuchet log.",
        Running = "Running, this takes a while. The status above shows the progress.",
        PostponePlaceholder = "Postpone by how much?",
        DelayPlaceholder = "Choose the warning time for the players",
        Confirm = "Confirm",
        Cancel = "Cancel",
        Postponed = "Postponed by {0}.",
        PostponedLimited = "Postponed by {0}. That is the longest postponement, it can only go on after the restart.",
        PostponeLimit = "The longest postponement is used up. It can only be postponed again after the restart.",
        AskRestart = "Restart server {0}? Players are warned {1} before.",
        AskStop = "Stop server {0}? Players are warned {1} before.",
        AskStart = "Start server {0}?",
        AskRestartDelay = "Restart server {0}: when? With a delay the players are warned first.",
        AskStopDelay = "Stop server {0}: when? With a delay the players are warned first.",
        AskUpdate = "Check for updates and install them?",
        AskChannel = "Make this channel the admin channel? The bot only answers here afterwards.",
        DoneRestart = "Restart of server {0} started.",
        DoneStop = "Stop of server {0} started.",
        DoneStart = "Server {0} is starting.",
        DoneUpdate = "Update check started.",
        DoneCheckUpdates = "Update check done. Mod updates: {0}. Server version: {1}. Nothing was installed.",
        ServerNewVersion = "new version available",
        ServerCurrent = "up to date",
        DoneChannel = "This channel is now the admin channel. The status messages appear here shortly.",
        Now = "right away",
        NowChoice = "Right away",
        MinutesFormat = "{0} min",
        HoursFormat = "{0} h",
        HoursMinutesFormat = "{0} h {1} min",
        ModalStop = "Stop ({0}) – reason",
        ModalRestart = "Restart ({0}) – reason",
        ModalPostpone = "Postpone by {0} – reason",
        ReasonLabel = "Reason (may stay empty)",
        ReasonPlaceholder = "e.g. maintenance, update, agreed with the team"
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
