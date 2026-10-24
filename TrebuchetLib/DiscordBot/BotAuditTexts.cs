using System.Globalization;
using System.Text.RegularExpressions;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// The lines of the audit channel in plain words, in the language of the bot: who asked for what, for which server, and how
/// it ended. The technical values of the audit log (<c>instance=0 step=Confirm</c>, <c>Done under way</c>) are translated
/// here. Nothing is passed through that could carry a mention, a link or a token: names and the reason come cleaned from the
/// bot, the rest are fixed texts.
/// </summary>
public sealed partial class BotAuditTexts
{
    // {0} what, {1} outcome (with its dash, or empty), {2} who.
    public required string Asked { get; init; }
    public required string Confirmed { get; init; }
    public required string ChoseTime { get; init; }
    public required string Done { get; init; }
    public required string Failed { get; init; }
    public required string Denied { get; init; }
    public required string Throttled { get; init; }
    public required string Cancelled { get; init; }
    /// <summary>{0} who.</summary>
    public required string LockedOut { get; init; }
    /// <summary>{0} name, {1} instance, {2} before, {3} after.</summary>
    public required string StateChange { get; init; }
    public required string Connected { get; init; }
    /// <summary>{0} what is wrong.</summary>
    public required string ProblemFormat { get; init; }

    /// <summary>{0} name, {1} instance.</summary>
    public required string ServerNamed { get; init; }
    public required string ServerNumbered { get; init; }
    public required string Warning { get; init; }
    public required string Later { get; init; }
    public required string Reason { get; init; }
    public required string UnknownRequest { get; init; }
    public required string OldQuestion { get; init; }
    public required string BusyFormat { get; init; }
    public required string ErrorFormat { get; init; }
    public required string FinishedFormat { get; init; }
    public required string FinishedOk { get; init; }
    public required string ServersFormat { get; init; }
    public required string ModUpdates { get; init; }
    public required string ServerUpdate { get; init; }
    public required string UpdatesAnd { get; init; }
    public required string NoUpdates { get; init; }

    public required IReadOnlyDictionary<BotCommand, string> Actions { get; init; }
    /// <summary>The texts of the audit log (English) and what they mean here.</summary>
    public required IReadOnlyDictionary<string, string> Phrases { get; init; }
    public required IReadOnlyDictionary<DiscordBotProblem, string> Problems { get; init; }

    public static BotAuditTexts German { get; } = new()
    {
        Asked = "🔔 {2} fragt an: {0}",
        Confirmed = "👍 {2} bestätigt: {0}",
        ChoseTime = "🕒 {2} wählt die Zeit: {0}",
        Done = "✅ Erledigt: {0}{1} (von {2})",
        Failed = "❌ Hat nicht geklappt: {0}{1} (von {2})",
        Denied = "🚫 Abgelehnt: {0} (von {2}). Der genaue Grund steht im Protokoll auf dem Rechner.",
        Throttled = "⏳ Gebremst: {0}{1} (von {2})",
        Cancelled = "↩️ Abgebrochen: {0} (von {2})",
        LockedOut = "🔒 Gesperrt: {0} hat zu viele abgelehnte Anfragen gesendet und ist vorübergehend gesperrt.",
        StateChange = "🔄 Server {0} (Instanz {1}): {2} → {3}",
        Connected = "🟢 Der Discord-Bot ist verbunden.",
        ProblemFormat = "⚠️ Der Discord-Bot hat ein Problem: {0}",
        ServerNamed = "Server {0} (Instanz {1})",
        ServerNumbered = "Server {0}",
        Warning = "Vorwarnung {0}",
        Later = "um {0}",
        Reason = "Grund: {0}",
        UnknownRequest = "Unbekannte Anfrage",
        OldQuestion = "Rückfrage, die abgelaufen oder nicht von diesem Nutzer ist",
        BusyFormat = "gerade nicht möglich, {0}",
        ErrorFormat = "{0}",
        FinishedFormat = "im Hintergrund beendet: {0}",
        FinishedOk = "im Hintergrund beendet",
        ServersFormat = "{0} Server angezeigt",
        ModUpdates = "{0} Mod-Update(s) gefunden",
        ServerUpdate = "Update für den Server gefunden",
        UpdatesAnd = " und ",
        NoUpdates = "keine Updates gefunden",
        Actions = new Dictionary<BotCommand, string>
        {
            [BotCommand.Status] = "Status anzeigen",
            [BotCommand.Postpone] = "Geplante Aktion verschieben",
            [BotCommand.Resume] = "Verschiebung aufheben",
            [BotCommand.Cancel] = "Countdown abbrechen",
            [BotCommand.RestartNow] = "Server neu starten",
            [BotCommand.Stop] = "Server stoppen",
            [BotCommand.Start] = "Server starten",
            [BotCommand.Update] = "Updates installieren",
            [BotCommand.CheckUpdates] = "Nach Updates suchen",
            [BotCommand.SetChannel] = "Admin-Kanal festlegen"
        },
        Phrases = new Dictionary<string, string>
        {
            ["Done"] = "",
            ["Done under way"] = "läuft jetzt",
            ["NotRunning"] = "der Server läuft nicht",
            ["NotRunning starting"] = "der Server startet gerade erst",
            ["AlreadyRunning"] = "der Server läuft schon",
            ["NothingPending"] = "es ist nichts geplant, das sich verschieben lässt",
            ["stopping"] = "der Server wird gerade gestoppt",
            ["starting"] = "der Server startet gerade",
            ["save in use by another instance"] = "der Speicherstand wird von einer anderen Instanz benutzt",
            ["a start, update or download runs"] = "ein Start, Update oder Download läuft",
            ["sequence running"] = "eine Abfolge (Sequenz) läuft",
            ["restart under way"] = "ein Neustart läuft schon",
            ["invalid minutes"] = "ungültige Minutenangabe",
            ["invalid delay"] = "ungültige Wartezeit",
            ["invalid instance"] = "ungültige Instanz",
            ["instance not installed"] = "die Instanz ist nicht installiert",
            ["no save or mod list selected"] = "es ist kein Speicherstand oder keine Modliste gewählt",
            ["updates are switched off in the settings"] = "Updates sind in den Einstellungen abgeschaltet",
            ["update failed"] = "das Update ist fehlgeschlagen",
            ["the update check failed"] = "die Prüfung auf Updates ist fehlgeschlagen",
            ["Steam did not answer"] = "Steam hat nicht geantwortet",
            ["cancelled"] = "abgebrochen",
            ["the launcher did not answer"] = "der Launcher hat nicht geantwortet",
            ["the launcher could not do it"] = "der Launcher konnte es nicht ausführen",
            ["no result"] = "es kam kein Ergebnis",
            ["exception"] = "unerwarteter Fehler",
            ["error"] = "unerwarteter Fehler",
            ["not an action"] = "keine gültige Aktion",
            ["running"] = "dauert länger und läuft im Hintergrund weiter",
            ["finished with an error"] = "im Hintergrund mit einem Fehler beendet",
            ["admin channel set"] = "dieser Kanal ist jetzt der Admin-Kanal",
            ["Cooldown"] = "zu viele Befehle kurz hintereinander",
            ["CriticalLimit"] = "zu viele kritische Aktionen in kurzer Zeit",
            ["PostponeLimit"] = "die längste erlaubte Verschiebung ist erreicht",
            ["not acknowledged in time, nothing done"] = "Discord hat die Anfrage zu spät bestätigt, es wurde nichts getan"
        },
        Problems = new Dictionary<DiscordBotProblem, string>
        {
            [DiscordBotProblem.NoToken] = "Es ist kein Bot-Token hinterlegt.",
            [DiscordBotProblem.InvalidSettings] = "Die Einstellungen sind unvollständig oder ungültig.",
            [DiscordBotProblem.TokenInUse] = "Der Token wird schon von einem anderen Trebuchet benutzt.",
            [DiscordBotProblem.LoginFailed] = "Die Anmeldung bei Discord ist fehlgeschlagen.",
            [DiscordBotProblem.GuildNotFound] = "Der Discord-Server wurde nicht gefunden.",
            [DiscordBotProblem.ConnectionLost] = "Die Verbindung zu Discord ist abgebrochen.",
            [DiscordBotProblem.CommandsNotRegistered] = "Die Slash-Befehle konnten nicht angelegt werden (Einladung ohne applications.commands?).",
            [DiscordBotProblem.Unknown] = "Unbekannter Fehler."
        }
    };

    public static BotAuditTexts English { get; } = new()
    {
        Asked = "🔔 {2} asks: {0}",
        Confirmed = "👍 {2} confirms: {0}",
        ChoseTime = "🕒 {2} picks the time: {0}",
        Done = "✅ Done: {0}{1} (by {2})",
        Failed = "❌ Did not work: {0}{1} (by {2})",
        Denied = "🚫 Refused: {0} (by {2}). The exact reason is in the log file on the computer.",
        Throttled = "⏳ Slowed down: {0}{1} (by {2})",
        Cancelled = "↩️ Cancelled: {0} (by {2})",
        LockedOut = "🔒 Locked out: {0} sent too many refused requests and is locked for a while.",
        StateChange = "🔄 Server {0} (instance {1}): {2} → {3}",
        Connected = "🟢 The Discord bot is connected.",
        ProblemFormat = "⚠️ The Discord bot has a problem: {0}",
        ServerNamed = "Server {0} (instance {1})",
        ServerNumbered = "Server {0}",
        Warning = "warning {0}",
        Later = "by {0}",
        Reason = "reason: {0}",
        UnknownRequest = "Unknown request",
        OldQuestion = "Question that expired or belongs to another user",
        BusyFormat = "not possible right now, {0}",
        ErrorFormat = "{0}",
        FinishedFormat = "finished in the background: {0}",
        FinishedOk = "finished in the background",
        ServersFormat = "{0} server(s) shown",
        ModUpdates = "{0} mod update(s) found",
        ServerUpdate = "update for the server found",
        UpdatesAnd = " and ",
        NoUpdates = "no updates found",
        Actions = new Dictionary<BotCommand, string>
        {
            [BotCommand.Status] = "Show status",
            [BotCommand.Postpone] = "Postpone the planned action",
            [BotCommand.Resume] = "Release the postponement",
            [BotCommand.Cancel] = "Cancel the countdown",
            [BotCommand.RestartNow] = "Restart server",
            [BotCommand.Stop] = "Stop server",
            [BotCommand.Start] = "Start server",
            [BotCommand.Update] = "Install updates",
            [BotCommand.CheckUpdates] = "Check for updates",
            [BotCommand.SetChannel] = "Set the admin channel"
        },
        Phrases = new Dictionary<string, string>
        {
            ["Done"] = "",
            ["Done under way"] = "under way",
            ["NotRunning"] = "the server is not running",
            ["NotRunning starting"] = "the server is only just starting",
            ["AlreadyRunning"] = "the server already runs",
            ["NothingPending"] = "nothing is planned that could be postponed",
            ["stopping"] = "the server is stopping",
            ["starting"] = "the server is starting",
            ["save in use by another instance"] = "the save is in use by another instance",
            ["a start, update or download runs"] = "a start, update or download runs",
            ["sequence running"] = "a sequence runs",
            ["restart under way"] = "a restart is already under way",
            ["invalid minutes"] = "invalid number of minutes",
            ["invalid delay"] = "invalid waiting time",
            ["invalid instance"] = "invalid instance",
            ["instance not installed"] = "the instance is not installed",
            ["no save or mod list selected"] = "no save or mod list is selected",
            ["updates are switched off in the settings"] = "updates are switched off in the settings",
            ["update failed"] = "the update failed",
            ["the update check failed"] = "the update check failed",
            ["Steam did not answer"] = "Steam did not answer",
            ["cancelled"] = "cancelled",
            ["the launcher did not answer"] = "the launcher did not answer",
            ["the launcher could not do it"] = "the launcher could not do it",
            ["no result"] = "there was no result",
            ["exception"] = "unexpected error",
            ["error"] = "unexpected error",
            ["not an action"] = "not a valid action",
            ["running"] = "takes longer and goes on in the background",
            ["finished with an error"] = "ended with an error in the background",
            ["admin channel set"] = "this channel is now the admin channel",
            ["Cooldown"] = "too many commands in a row",
            ["CriticalLimit"] = "too many critical actions in a short time",
            ["PostponeLimit"] = "the longest allowed postponement is reached",
            ["not acknowledged in time, nothing done"] = "Discord did not get the answer in time, nothing was done"
        },
        Problems = new Dictionary<DiscordBotProblem, string>
        {
            [DiscordBotProblem.NoToken] = "No bot token is stored.",
            [DiscordBotProblem.InvalidSettings] = "The settings are incomplete or invalid.",
            [DiscordBotProblem.TokenInUse] = "The token is already used by another Trebuchet.",
            [DiscordBotProblem.LoginFailed] = "The login to Discord failed.",
            [DiscordBotProblem.GuildNotFound] = "The Discord server was not found.",
            [DiscordBotProblem.ConnectionLost] = "The connection to Discord was lost.",
            [DiscordBotProblem.CommandsNotRegistered] = "The slash commands could not be created (invite without applications.commands?).",
            [DiscordBotProblem.Unknown] = "Unknown error."
        }
    };

    public static BotAuditTexts For(string? language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? English : German;

    // ---- one entry of the audit log

    /// <summary>The line for an entry, empty when it adds nothing (the click on "Cancel" is told by its own entry).</summary>
    public string Format(BotAuditEntry entry, string? serverName, string? language)
    {
        var inv = CultureInfo.InvariantCulture;
        var who = $"<@{entry.UserId.ToString(inv)}>";
        var request = Parameters.Read(entry.Parameters);
        if (entry.Result == BotAuditResult.LockedOut) return string.Format(inv, LockedOut, who);
        if (entry.Result == BotAuditResult.Allowed && request.Step == "Cancel") return string.Empty;

        var what = What(entry, request, serverName, language);
        var outcome = Describe(entry.Detail, language);
        var tail = outcome.Length > 0 ? " – " + outcome : string.Empty;
        var template = entry.Result switch
        {
            BotAuditResult.Allowed => request.Step switch { "Confirm" or "Reason" => Confirmed, "PostponeChoice" => ChoseTime, _ => Asked },
            BotAuditResult.Executed => Done,
            BotAuditResult.Failed => Failed,
            BotAuditResult.Denied => Denied,
            BotAuditResult.Throttled => Throttled,
            BotAuditResult.Cancelled => Cancelled,
            _ => Asked
        };
        // The exact reason of a refusal is for the log file only.
        if (entry.Result == BotAuditResult.Denied) tail = string.Empty;
        return string.Format(inv, template, what, tail, who);
    }

    private string What(BotAuditEntry entry, Parameters request, string? serverName, string? language)
    {
        var inv = CultureInfo.InvariantCulture;
        if (request.Code is not null) return OldQuestion;
        if (entry.Command is not { } command) return UnknownRequest;
        var text = Actions.TryGetValue(command, out var action) ? action : command.ToString();
        if (request.Instance is { } instance)
            text += ": " + (string.IsNullOrWhiteSpace(serverName)
                ? string.Format(inv, ServerNumbered, instance)
                : string.Format(inv, ServerNamed, serverName, instance));
        var messages = BotMessages.For(language);
        var minutes = request.Minutes ?? request.Delay;
        if (minutes is { } m && m > 0)
            text += ", " + string.Format(inv, command == BotCommand.Postpone ? Later : Warning, messages.FormatMinutes((int)Math.Min(m, 24 * 60)));
        if (!string.IsNullOrEmpty(request.Reason)) text += ", " + string.Format(inv, Reason, request.Reason);
        return text;
    }

    /// <summary>The outcome as the audit log has it ("Busy stopping", "Done mods=2 server=False", "CriticalLimit") in words.</summary>
    public string Describe(string? detail, string? language)
    {
        var text = (detail ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        if (Phrases.TryGetValue(text, out var known)) return known;

        var inv = CultureInfo.InvariantCulture;
        var space = text.IndexOf(' ');
        var head = space < 0 ? text : text[..space];
        var rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();

        if (text.StartsWith("finished with an error", StringComparison.Ordinal))
            return Phrases["finished with an error"];
        if (head == "finished" && rest.Length > 0)
        {
            var inner = Describe(rest, language);
            return inner.Length == 0 ? FinishedOk : string.Format(inv, FinishedFormat, inner);
        }
        if (head == "Done" && UpdateResult().Match(rest) is { Success: true } updates)
            return Updates(int.Parse(updates.Groups[1].Value, inv), updates.Groups[2].Value == "True");
        if (head is "Done" or "NotRunning" or "AlreadyRunning" or "NothingPending" && rest.Length > 0)
        {
            var more = Describe(rest, language);
            var start = Phrases.TryGetValue(head, out var first) ? first : head;
            return more.Length == 0 ? start : start.Length == 0 ? more : $"{start}: {more}";
        }
        if (head == "Busy" && rest.Length > 0) return string.Format(inv, BusyFormat, Describe(rest, language));
        if (head == "Failed" && rest.Length > 0) return string.Format(inv, ErrorFormat, Describe(rest, language));
        if (ServerCount().Match(text) is { Success: true } count) return string.Format(inv, ServersFormat, count.Groups[1].Value);
        // A text this class does not know: shown as it is (it is a short fixed text of the bot, cleaned).
        return BotInputRules.SanitizeForLog(text);
    }

    private string Updates(int mods, bool server)
    {
        var inv = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        if (mods > 0) parts.Add(string.Format(inv, ModUpdates, mods));
        if (server) parts.Add(ServerUpdate);
        return parts.Count == 0 ? NoUpdates : string.Join(UpdatesAnd, parts);
    }

    /// <summary>The values of the request of an entry (<c>instance=0 minutes=5 reason=… step=Confirm</c>).</summary>
    private sealed record Parameters(int? Instance, long? Minutes, long? Delay, string? Reason, string? Step, string? Code)
    {
        public static Parameters Read(string? text)
        {
            var inv = CultureInfo.InvariantCulture;
            var all = text ?? string.Empty;
            // The reason is free text and comes last of the request (a step is appended after it by the bot only without a reason).
            string? reason = null;
            var at = all.IndexOf("reason=", StringComparison.Ordinal);
            if (at >= 0)
            {
                reason = all[(at + 7)..].Trim();
                all = all[..at];
            }
            int? instance = null;
            long? minutes = null, delay = null;
            string? step = null, code = null;
            foreach (Match match in Pair().Matches(all))
            {
                var value = match.Groups[2].Value;
                switch (match.Groups[1].Value)
                {
                    case "instance" when int.TryParse(value, NumberStyles.None, inv, out var i): instance = i; break;
                    case "minutes" when long.TryParse(value, NumberStyles.None, inv, out var m): minutes = m; break;
                    case "delay" when long.TryParse(value, NumberStyles.None, inv, out var d): delay = d; break;
                    case "step": step = value; break;
                    case "code": code = value; break;
                }
            }
            return new Parameters(instance, minutes, delay, reason, step, code);
        }
    }

    [GeneratedRegex(@"\b(instance|minutes|delay|step|code)=(\w+)")]
    private static partial Regex Pair();

    [GeneratedRegex(@"^mods=(\d+) server=(True|False)$")]
    private static partial Regex UpdateResult();

    [GeneratedRegex(@"^(\d+) servers$")]
    private static partial Regex ServerCount();

    // ---- what the bot notices itself

    public string StateChanged(string? name, int instance, BotServerState before, BotServerState after, BotTexts states)
    {
        var inv = CultureInfo.InvariantCulture;
        var shown = string.IsNullOrWhiteSpace(name) ? instance.ToString(inv) : name;
        return string.Format(inv, StateChange, shown, instance, BotStatusMessageBuilder.StateText(before, states), BotStatusMessageBuilder.StateText(after, states));
    }

    public string ProblemText(DiscordBotProblem problem) =>
        string.Format(CultureInfo.InvariantCulture, ProblemFormat, Problems.TryGetValue(problem, out var text) ? text : problem.ToString());
}
