# Plan: Discord-Bot in Trebuchet (Hauptpunkt: Sicherheit)

## Ziel

Ein Discord-Bot läuft **in Trebuchet** (kein extra Programm, kein extra Port). Berechtigte Personen steuern damit
die Server per Slash-Befehl, ohne sich auf den Server einloggen zu müssen. Der wichtigste Anwendungsfall:

> Ein Neustart steht an, es läuft ein Event. Per `/restart postpone` den Neustart um X Minuten verschieben.
> Ingame erscheint die schon vorhandene Meldung „Der Neustart von {servername} wurde um X Minuten verschoben.“

Neuer Menüpunkt **„Discord Bot“** in der linken Leiste direkt unter „Dashboard“. Dort wird der Bot gestartet,
gestoppt und eingestellt.

Heute gibt es in Trebuchet nur **ausgehende Webhooks** (`DiscordWebHooks.cs`, `Discord.Net.Webhook`). Die können
nichts empfangen. Für den Bot kommt eine echte Gateway-Verbindung dazu (`Discord.Net.WebSocket` und
`Discord.Net.Interactions`, gleiche Version wie das vorhandene Webhook-Paket, vor der Umsetzung gegen NuGet prüfen).

---

## 1. Sicherheit

### 1.1 Antwort auf „ist das über den Launcher sicher?“

Ja, wenn die Regeln unten eingehalten werden. Gegenüber der Lösung über einen Mod:

| | Mod im Spielserver | Bot im Launcher |
|---|---|---|
| Wer kann Befehle auslösen | wer im Mod freigeschaltet ist | wer Discord-User-ID **und** Rolle hat |
| Offener Port am Rechner | je nach Mod | **keiner**, der Bot baut nur eine ausgehende Verbindung zu Discord auf |
| Reichweite eines Fehlers | im Spielserver | im Launcher-Prozess (läuft mit den Rechten des Windows-Benutzers) |

Der zweite Punkt ist der Haken: Ein Fehler im Bot hätte mehr Reichweite als ein Fehler in einem Mod. Deshalb gilt
im ganzen Plan: **so wenig Funktionen wie möglich, feste Liste erlaubter Aktionen, nichts Freies** (kein RCon
durchreichen, keine Skripte, keine Dateipfade, keine Texteingabe, die irgendwo ausgeführt wird).

### 1.2 Bedrohungsmodell

| Angreifer / Fehler | Schutz |
|---|---|
| Beliebiger Nutzer auf dem Discord-Server ruft den Befehl auf | Doppelcheck User-ID + Rolle, sonst Ablehnung |
| Rollen-Admin gibt jemandem die Rolle | Rolle allein reicht nicht, die User-ID muss auch in der Liste stehen |
| Jemand trägt eine falsche User-ID ein oder kennt die ID eines Berechtigten | ID allein reicht nicht, die Rolle muss auch stimmen. Eine ID lässt sich nicht fälschen, sie kommt von Discord |
| Bot wird auf einen fremden Discord-Server eingeladen | Nur eine feste Server-ID (Guild) wird akzeptiert, der Bot verlässt alle anderen |
| Befehl per DM an den Bot | Befehle sind nur auf dem Server erlaubt, DMs werden abgelehnt |
| Token wird gestohlen (Config-Datei, Log, Screenshot, Backup) | Token liegt verschlüsselt (Windows-DPAPI), steht nie in Config, Log, Export, Crash-Report oder UI |
| Mit dem Token startet jemand einen eigenen Bot | Er kann damit nichts in Trebuchet auslösen, er bekäme nur Discord-Befehle für **seine** Instanz. Trotzdem: Token im Developer-Portal zurücksetzen (siehe 1.8) |
| Eingabe schleust Makros, Zeilenumbrüche oder `@everyone` ein | Alle Eingaben werden geprüft und gekürzt, Antworten erlauben keine Erwähnungen (1.5) |
| Spam oder Brute-Force von Befehlen | Wartezeit pro Nutzer, Limit für kritische Aktionen, Sperre nach mehreren Ablehnungen (1.6) |
| Doppelt ausgeführter Befehl (zwei Trebuchet mit demselben Token) | Pro Token läuft nur ein Bot (1.7) |
| Fehler in der Konfiguration (leere Liste, falsche Rolle) | Fail-closed: im Zweifel **nein** (1.9) |

### 1.3 Berechtigungsprüfung (der Doppelcheck)

Zugriff nur, wenn **alle** Schritte bestanden sind, in dieser Reihenfolge. Der erste Fehlschlag lehnt ab.

1. Befehl kommt von einem **Server**, nicht aus einer DM.
2. Server-ID == in den Einstellungen hinterlegte **Guild-ID**.
3. Absender ist ein echter Nutzer (kein Bot, kein Webhook).
4. Optional: Kanal-ID steht in der Liste erlaubter Kanäle (leere Liste = alle Kanäle). Der **Admin-Kanal** wird mit
   dem Befehl `/bot channel set` **aus dem Kanal selbst** gesetzt (siehe 1.4).
5. Wartezeit/Sperre des Nutzers (1.6) ist nicht aktiv.
6. **User-ID** steht in der Liste erlaubter Nutzer (Super-Admin zählt immer dazu).
7. Der Nutzer hat **mindestens eine** Rolle aus der Liste erlaubter Rollen. Die Rollen kommen **frisch aus der
   Discord-Nachricht des Befehls**, nicht aus einem Zwischenspeicher, damit eine entzogene Rolle sofort wirkt.
8. Der Befehl ist in den Einstellungen aktiviert und die Stufe reicht (1.4).
9. Parameter sind gültig (1.5).
10. Kritische Aktion: Bestätigungsknopf (1.6). Beim Klick laufen die Schritte 1–8 **noch einmal**.

Regeln dazu:

- **Fehlt eines von beiden** (keine Nutzer-IDs oder keine Rollen-IDs hinterlegt), lehnt der Bot **alles** ab. Er
  startet dann gar nicht und die Oberfläche sagt warum.
- Es zählen nur **IDs**, nie Namen, Spitznamen oder Texte.
- Die Rolle `@everyone` wird abgelehnt. Ihre ID ist gleich der Server-ID, sie hat jeder. Die Eingabe wird beim
  Speichern geprüft.
- IDs müssen reine Zahlen mit 17 bis 20 Stellen sein, sonst wird das Speichern abgelehnt.
- Nutzer ohne Berechtigung bekommen **eine einheitliche Antwort** („Keine Berechtigung“), ohne Hinweis ob ID oder
  Rolle gefehlt hat. Die Details stehen nur im lokalen Protokoll.
- Die Prüfung steht in **einer** Klasse (`BotAuthorizer`) und arbeitet auf einem unveränderlichen Abbild der
  Einstellungen. Änderungen in der Oberfläche gelten sofort, ohne Neustart des Bots.
- Die Prüfung bekommt keine Discord-Objekte, sondern nur eine kleine Datenklasse (User-ID, Guild-ID, Kanal-ID,
  Rollen-IDs, ist-Bot). So lässt sie sich vollständig testen.

### 1.4 Super-Admin und Stufen

- **Super-Admin** ist genau **eine** User-ID. Sie ist Pflicht (ohne sie startet der Bot nicht), steht ganz oben
  in der Liste und lässt sich dort **nicht entfernen**. Ändern geht nur über ein eigenes Feld mit Bestätigung.
- Der Super-Admin muss **ebenfalls die Rolle haben** (strenger Doppelcheck, siehe Entscheidung 1).
- Die Rechte aller Nutzer **ändert nur der Launcher**, nie ein Discord-Befehl. Wer Discord hat, kann also keine
  Rechte vergeben oder entziehen.
- **Einzige Ausnahme: der Admin-Kanal.** `/bot channel set` (nur Super-Admin, mit Bestätigung, im Audit-Protokoll)
  macht den Kanal, in dem der Befehl steht, zum einzigen Kanal des Bots. Das **schränkt nur ein**: es gibt keinen
  Discord-Befehl, der den Kanal wieder freigibt. Zurück auf „alle Kanäle“ geht nur im Launcher. Der Befehl wirkt
  auch außerhalb des bisherigen Admin-Kanals (sonst käme man nie in einen neuen Kanal), prüft aber Server, User-ID,
  Rolle und Super-Admin wie jeder andere.

| Stufe | Wer | Beispiele |
|---|---|---|
| **Lesen** | alle Berechtigten | `/status` |
| **Verwalten** | alle Berechtigten | Neustart verschieben, wieder freigeben |
| **Kritisch** | alle Berechtigten, **nur wenn in den Einstellungen eingeschaltet**, mit Bestätigung | Neustart jetzt, Stopp |
| **Super-Admin** | nur Super-Admin, mit Bestätigung | Server starten, Updates anstoßen, Admin-Kanal setzen |

Pro Befehl lässt sich einstellen: aus / Stufe. „Kritisch“ und „Super-Admin“ sind standardmäßig **aus**.

### 1.5 Eingaben und Ausgaben

- **Instanz** ist eine Zahl von 0 bis `ServerInstanceCount - 1` und wird serverseitig geprüft. Die Auswahlliste im
  Discord (Autocomplete) ist nur Komfort.
- **Dauer** ist eine ganze Zahl in Minuten, mindestens 1 und höchstens das eingestellte Maximum (Standard 6 h,
  harte Obergrenze 24 h). Es gibt kein unbegrenztes Verschieben, damit Updates nicht für immer ausbleiben.
- **Grund** (optional) wird auf 100 Zeichen gekürzt. Steuerzeichen, Zeilenumbrüche und `{` `}` (Makros von
  `ServerMacros.Expand`) werden entfernt. Der Grund geht nur ins Protokoll, nie in eine RCon-Zeile.
- Alle Antworten setzen **keine Erwähnungen** (`AllowedMentions.None`), damit niemand `@everyone` auslösen kann.
- Antworten sind standardmäßig **nur für den Aufrufer sichtbar** (ephemeral).
- Die Ausgabe enthält **nie**: RCon-Passwort, Admin-Passwort, Dateipfade, externe IP, Token, Fehler-Stacktraces.
  `/status` zeigt nur Instanz, Save-Name, Zustand, Spielerzahl, Laufzeit, Pausenstatus, offenes Update.
- Es gibt **keinen** Befehl für RCon, Skripte, Dateien, Mods, Einstellungen oder Rechte.

### 1.6 Missbrauchsschutz

- **Wartezeit** pro Nutzer (Standard 3 s zwischen zwei Befehlen).
- **Limit** für kritische Aktionen (Standard höchstens 3 pro 10 Minuten pro Server, gesamt).
- **Sperre**: nach 5 abgelehnten Versuchen eines Nutzers in 10 Minuten werden seine Befehle 10 Minuten lang ohne
  Antwort ignoriert. Der Super-Admin bekommt eine Meldung (Audit-Kanal), wenn er einen Kanal eingestellt hat.
- **Bestätigung** für kritische Aktionen: Antwort mit Knopf „Bestätigen“ / „Abbrechen“, gültig 30 Sekunden.
  Die Knopf-ID ist eine Zufallszahl, die im Bot nachgeschlagen wird (keine Daten in der ID, nicht fälschbar).
  Der Knopf gilt nur für den Nutzer, der den Befehl gestartet hat, und prüft die Berechtigung beim Klick erneut.
- Gleiche Interaktions-ID wird nur einmal verarbeitet.

### 1.7 Token und Geheimnisse

- Der Token liegt **nicht** in `settings.<edition>.json`, sondern verschlüsselt in einer eigenen Datei (Windows
  `ProtectedData`, Benutzer-Bereich). Auf einem anderen Windows-Benutzer oder Rechner ist die Datei wertlos.
- Das Eingabefeld ist **nur zum Schreiben**: es zeigt nach dem Speichern nur „Token gespeichert ✓“ mit
  „Ersetzen“ und „Entfernen“. Das Feld maskiert die Eingabe (`PasswordChar`). Das vorhandene `PasswordField`
  zeigt den Text offen, deshalb kommt ein eigenes `SecretField`.
- Der Token wird nie geloggt. Der Discord-Logger wird auf Trebuchets Logger umgeleitet, **mit Filter** für
  Token-Muster. Ein Test prüft, dass der Token in keiner Logausgabe auftaucht.
- Der Token darf nicht in **Exporte, Backups, Crash-Reports und Sync-Profile** gelangen. Vor der Umsetzung wird
  geprüft, was `Operations` (Config-Import/Export) und die `crash-*.json` mitnehmen.
- Pro Token läuft **nur ein Bot**: ein benannter Mutex mit Hash des Tokens. Sonst bekämen zwei Trebuchet-Fenster
  (z. B. Legacy und Enhanced) denselben Befehl und würden ihn doppelt ausführen.
- Ohne Windows (Tests, später Boulder): Token aus Umgebungsvariable, nie aus einer Klartextdatei.

### 1.8 Einrichtung auf Discord-Seite (Checkliste, kommt als Hilfetext ins Panel)

- Bot im Developer-Portal anlegen und **„Public Bot“ ausschalten**, damit niemand ihn einladen kann.
- Einladen nur mit den Bereichen `bot` und `applications.commands`. **Keine Administrator-Rechte.** Der Bot braucht
  nur „Kanäle ansehen“ und „Nachrichten senden“.
- **Keine privilegierten Intents** (kein Message Content, kein Server Members, kein Presence). Slash-Befehle
  brauchen sie nicht. Falls die Rollen ohne den Members-Intent nicht in der Nachricht ankommen, holt der Bot sie
  per REST frisch (vor der Umsetzung gegen Discord.Net 3.x prüfen).
- Befehle werden nur für **diesen einen Server** registriert und mit „nicht in DMs“ markiert.
- Optional in Discord selbst die Befehle auf die Rolle einschränken. Das ist eine zusätzliche Schicht, Trebuchet
  prüft trotzdem immer selbst.
- 2-Faktor-Login für das Developer-Portal-Konto.
- Bei Verdacht (Token geteilt, Screenshot): im Portal **„Reset Token“** und in Trebuchet ersetzen.

### 1.9 Fail-closed und sichere Voreinstellung

- Der Bot ist **aus**, auch nach Updates. „Mit Trebuchet starten“ ist **aus**, bis es jemand einschaltet.
- Startbedingungen: Token, Guild-ID, Super-Admin, mindestens eine Rollen-ID. Fehlt etwas, startet er nicht.
- Beim Verbinden prüft der Bot: Guild existiert, Rollen existieren, keine Rolle ist `@everyone`, der Bot sitzt in
  **keinem** anderen Server (sonst verlässt er ihn und schreibt es ins Protokoll).
- Ist eine eingetragene Rolle gelöscht oder umbenannt, bleibt sie ohne Treffer, es wird also **abgelehnt**, nicht
  erlaubt.
- Jede Ausnahme in der Prüfung führt zu „abgelehnt“.
- Ist die Konfigurationsdatei kaputt, startet der Bot nicht. Es werden **keine Standardwerte** geladen, die etwas
  erlauben würden (`JsonFile` legt beschädigte Dateien schon als `.corrupt-…` ab).
- Fehler des Bots (Verbindung weg, Discord down) dürfen den Launcher nie blockieren. Der Bot läuft in eigenen
  Tasks mit Zeitlimit und Wiederverbindung.

### 1.10 Protokoll (Audit)

Jeder Befehl wird festgehalten, erlaubt oder abgelehnt:
Zeit, User-ID, Anzeigename (nur zur Info), Server-/Kanal-ID, Befehl, Parameter, Ergebnis, bei Ablehnung der
**genaue** Grund (nur lokal).

- Datei `discord-bot-audit-<Monat>.log` im Logordner, nur anhängen, nie mit Token.
- Letzte 200 Einträge im Panel.
- Optional: Audit-Kanal (Kanal-ID), in den der Bot kritische Aktionen und Ablehnungen meldet.

---

## 2. Befehle (erste Version)

| Befehl | Wirkung im Launcher | Stufe |
|---|---|---|
| `/status [instanz]` | liest Zustand, Spieler, Laufzeit, Pause, offenes Update | Lesen |
| `/restart postpone instanz minuten [grund]` | `PauseAutomaticRestarts(instance, minuten)`: bricht den Countdown ab, schiebt den Neustart, Ingame-Meldung „verschoben um X Minuten“ | Verwalten |
| `/restart resume instanz` | `ResumeAutomaticRestarts(instance)` | Verwalten |
| `/restart now instanz [verzögerung]` | `RestartServerIn(instance, delay)` mit Warnungen | Kritisch + Bestätigung |
| `/stop instanz [verzögerung]` | `CloseServerIn(instance, delay)` | Kritisch + Bestätigung |
| `/start instanz` | `CatapultServer(instance)` | Super-Admin + Bestätigung |
| `/update` | Update-Prüfung/Update anstoßen | Super-Admin + Bestätigung, später |
| `/bot channel set` | macht den aktuellen Kanal zum Admin-Kanal und legt dort die Status-Meldungen an | Super-Admin + Bestätigung |

Nicht enthalten, mit Absicht: harter Kill, RCon, Skripte, Mods, Einstellungen, Rechteverwaltung (außer dem
Admin-Kanal, siehe 1.4).

### 2.1 Status-Meldung mit Buttons (im Admin-Kanal)

Pro Server-Instanz postet der Bot **eine Meldung als Embed** und ändert sie laufend (ändert nur bei Änderung,
höchstens alle ~10 Sekunden). Die Nachricht-ID steht in den Einstellungen, damit der Bot nach einem Neustart die
alte Meldung weiter benutzt statt eine neue zu posten.

Das Embed zeigt: Name und Instanz, Zustand, Spieler (nur online), Laufzeit, Geplantes („Neustart in …“), „Verschoben
bis …“. Es zeigt **keine** Pfade, IPs oder Passwörter. Die Farbe des Embeds folgt dem Zustand:

| Zustand | Farbe |
|---|---|
| online, nichts geplant | grün |
| startet, stoppt, oder Neustart/Stopp/Update steht an | gelb |
| Neustarts verschoben | blau |
| gestoppt, abgestürzt | rot |

Darunter stehen die Buttons, nur die, die zum Zustand passen **und** deren Befehl eingeschaltet ist
(Discord kennt nur diese vier Farben):

| Button | Farbe | Wann |
|---|---|---|
| **Starten** | grün | Server ist gestoppt oder abgestürzt (Super-Admin, aus) |
| **Neustart** | blau | Server läuft (kritisch, aus) |
| **Stoppen** | rot | Server läuft (kritisch, aus) |
| **Neustart verschieben** / **Stopp verschieben** / **Update verschieben** | grau | nur wenn gerade etwas ansteht. Der Text sagt, was ansteht |
| **Wieder freigeben** | grau | wenn verschoben wurde |

Nach der Entscheidung 2 sind Starten, Neustart und Stoppen zuerst **aus**. Dann zeigt die Meldung nur den
Verschieben-Button, wenn etwas ansteht. Die anderen Buttons erscheinen, sobald sie im Launcher eingeschaltet werden.

**Verschieben**: Der Klick öffnet eine **Auswahl nur für den Klickenden** (ephemeral) mit Vorgaben: **15 Min, 30 Min,
1 Std, 2 Std, 3 Std, 6 Std**. Die Vorgaben und die längste Zeit stellt man im Launcher ein. Nach der Auswahl wird
`PauseAutomaticRestarts` ausgeführt, die Ingame-Meldung „verschoben um X Minuten“ kommt von selbst. Ein anstehender
**Stopp** wird neu geplant: der Countdown wird abgebrochen und ein neuer Stopp in X Minuten gestartet.

**Sicherheit der Buttons**: Die Meldung liegt im Kanal, jeder dort kann auf die Buttons klicken. Deshalb wird **jeder
Klick** wie ein Befehl geprüft (Server, Kanal, User-ID **und** Rolle, Befehl an, Stufe, Wartezeit). Die Button-IDs sind
nur Namen (`tb1:a:stop:0`), sie geben keine Rechte. Starten, Neustart und Stoppen verlangen danach noch den
Bestätigungsknopf mit Zufallscode (nur für den, der geklickt hat, 30 Sekunden, einmal). Die Auswahl der Minuten läuft
über denselben Mechanismus und prüft, dass der Wert eine der angebotenen Vorgaben ist.

---

## 3. Architektur

Alle Teile in `TrebuchetLib` (ohne Oberfläche), damit Boulder den Bot später auch ohne GUI starten kann. Nur das
Panel liegt in `Trebuchet`.

```
TrebuchetLib/DiscordBot/                         (Phase 1, fertig)
  BotId.cs                   Discord-IDs prüfen (17 bis 20 Ziffern)
  BotCommand.cs              die feste Liste der Befehle, ihre Stufen, was Bestätigung braucht
  DiscordBotConfig.cs        Einstellungen (Server, Super-Admin, IDs, Rollen, Kanal, Befehle, Limits), JsonFile, atomar
  BotPolicy.cs               unveränderliches Abbild, ungültige Einstellungen ergeben keine Richtlinie
  BotAuthorizer.cs           der Doppelcheck (1.3), reine Funktion
  BotRateLimiter.cs          Wartezeit, Limit kritischer Aktionen, Sperre, doppelte Interaktionen
  BotGuard.cs                Doppelcheck + Begrenzer in einem Aufruf: das Tor vor dem Launcher
  BotPendingActions.cs       Rückfragen (Bestätigung, Minutenauswahl) mit Zufallscode
  BotCustomId.cs             Button- und Menü-IDs, streng geprüft
  BotInputRules.cs           Instanz, Minuten, Texte prüfen und bereinigen
  BotAuditLog.cs             Protokoll, eine Datei pro Monat
  BotToken.cs / BotTokenStore.cs   Token prüfen, verschlüsselt speichern (DPAPI), aus Logs entfernen, ein Bot pro Token
  BotStatusMessage.cs / BotTexts.cs   Aufbau der Status-Meldung (Farbe, Felder, Buttons), Texte de/en
TrebuchetLib/DiscordBot/                         (Phase 2)
  BotActions.cs              einzige Brücke zum Launcher (Fassade mit fester Liste)
  DiscordBotService.cs       Verbindung, Slash-Befehle, Status-Meldungen, Wiederverbindung, Start/Stop
Trebuchet/ViewModels/Panels/DiscordBotPanel.cs   + Templates/DiscordBotPanel.axaml   (Phase 3)
Trebuchet/ViewModels/SettingFields/SecretField.cs
```

- **Ein Weg in den Launcher**: nur `BotActions`. Der Bot kennt `Launcher` sonst nicht. So ist die Angriffsfläche
  klein und prüfbar.
- **Thread**: Die Tabellen im `Launcher` (`_serverProcesses`, `_automaticRestartsPausedUntil` usw.) sind einfache
  `Dictionary` und werden vom Tick im UI-Thread benutzt. Der Bot läuft im Hintergrund. `BotActions` führt deshalb
  **jeden** Zugriff über den UI-Thread aus (`Dispatcher.UIThread`) und liefert für `/status` ein Abbild
  (`record`), keine lebenden Objekte.
- **Panel**: `DiscordBotPanel : IBottomPanel, ITickingPanel, IRefreshablePanel`, Registrierung in
  `App.axaml.cs` zwischen `DashboardPanel` und `ToolboxPanel`. Symbol `mdi-robot`.
- **Launcher-Änderungen** (klein): `RestartServerIn` und `CloseServerIn` bekommen einen optionalen `reason`
  (z. B. „Discord“) für Log und Meldung; ein Lese-Abbild `GetServerStatuses()` (`BotServerStatus`) mit Instanz,
  Save-Name, Zustand, Spielern, Start, Pause bis und **was ansteht** (Neustart, Stopp oder Update, und wann).
  Daraus entscheidet sich der Text des Verschieben-Buttons. Dazu eine Methode, die einen anstehenden Stopp neu
  plant (Countdown abbrechen, `CloseServerIn` mit der gewählten Zeit).
- Sprachen: neue Texte in `Resources.resx` (EN), `.de.resx`, `.fr.resx`.
- Ablauf-Reihenfolge im Bot: **eingehend → Prüfung (1.3) → Eingaben → Ausführen → Protokoll → Antwort**.
  Ausführen ist immer der letzte Schritt nach allen Prüfungen.

---

## 4. Panel „Discord Bot“

1. **Status**: Zustand (Gestoppt / Verbindet / Verbunden / Fehler mit Grund), Servername, Latenz, Laufzeit;
   Knöpfe **Starten**, **Stoppen**, **Neu verbinden**; Schalter „Mit Trebuchet starten“ (aus).
2. **Sicherheit** (direkt nach dem Status, mit Hinweistext: *„Zugriff nur mit User-ID **und** Rolle.“*)
   - Super-Admin-ID (Pflicht, gesperrte Zeile, Ändern mit Bestätigung)
   - Erlaubte User-IDs (Liste hinzufügen/entfernen)
   - Erlaubte Rollen-IDs (Liste)
   - Optional: erlaubte Kanal-IDs, Audit-Kanal
   - Limits: Wartezeit, Maximalpause, Limit kritischer Aktionen
   - Knopf **„Sicherheit prüfen“**: zeigt eine Ampelliste (IDs gültig, Guild erreichbar, Rollen vorhanden, keine
     `@everyone`-Rolle, Bot nur auf diesem Server, kein Administrator-Recht, Token gesetzt)
3. **Verbindung**: Token (`SecretField`), Guild-ID.
4. **Befehle**: Schalter und Stufe pro Befehl (Kritisch/Super-Admin standardmäßig aus).
5. **Protokoll**: letzte Einträge, Knopf „Ordner öffnen“.
6. **Einrichtungshilfe**: die Checkliste aus 1.8.

Änderungen gelten erst nach „Speichern“. Beim Speichern wird alles geprüft (Zahlenformat, Pflichtfelder,
`@everyone`). Ungültige Eingaben werden **nicht** gespeichert.

---

## 5. Tests (kommen in `Tests/ConsoleHotkeyChecks`, laufen in der CI)

- **Doppelcheck**: alle Kombinationen aus ID ok/nicht ok × Rolle ok/nicht ok × Guild ok/nicht ok × Kanal ×
  Super-Admin. Nur die richtige Kombination erlaubt.
- Leere Listen, `@everyone`, falsche Zahlenformate, DM, Bot-Nutzer, fremde Guild.
- Entzogene Rolle wirkt sofort; Änderung der Einstellungen wirkt sofort.
- Stufen: Kritisch aus → abgelehnt, Super-Admin-Befehl durch Normalnutzer → abgelehnt.
- Eingaben: Grenzen der Dauer, Instanz außerhalb, Grund mit Makros/Umbruch/Mentions.
- Wartezeit, Limit, Sperre.
- Bestätigungsknopf: falscher Nutzer, abgelaufen, zweimal geklickt, Berechtigung zwischendurch entzogen.
- Token: DPAPI hin und zurück, **kommt in keinem Log vor**, nicht in Export/Crash-Report.
- Ausnahme in der Prüfung → abgelehnt.
- `BotActions`: ruft nur die erlaubten Launcher-Methoden, immer im UI-Thread.
- Mutex: zweiter Bot mit gleichem Token startet nicht.

---

## 6. Phasen

| Phase | Inhalt | Ergebnis |
|---|---|---|
| 1 | **Erledigt.** Konfiguration, Token-Schutz, `BotAuthorizer`, Eingabeprüfung, Limits, Protokoll, Button-IDs, Bestätigungen, Aufbau der Status-Meldung, Tests | Sicherheitskern ohne Discord, komplett getestet |
| 2 | **Erledigt.** `LauncherBotActions`, Launcher-Lese-Abbild (mit „was steht an“), Verbindung, Status-Meldung mit Verschieben-Button und Auswahl, `/status`, `/restart resume`, `/bot channel set` | Der Hauptanwendungsfall |
| 3 | **Erledigt.** Panel mit Status, Sicherheit, Verbindung, Befehle, Protokoll, Sprachen | Bedienung im Launcher |
| 4 | **Erledigt.** Buttons Neustart, Stoppen, Starten mit Bestätigung, Audit-Kanal | Neustart jetzt, Stopp, Start |
| 5 (optional) | Boulder-Befehl `bot` ohne Oberfläche | Betrieb ohne GUI |

Erst nach Phase 1 und der Prüfung durch mich selbst und die Review-Agenten kommt Phase 2. Der Bot wird nie
ausgeliefert, bevor die Sicherheitstests grün sind.

---

## 7. Entscheidungen

Alle fünf sind getroffen:

1. **Super-Admin braucht auch die Rolle: ja.** Strenger Doppelcheck für alle.
2. **Neustart jetzt, Stopp (und Start): zuerst aus, im Launcher einschaltbar.** Ohne diese Befehle zeigen die
   Meldungen keine Buttons dafür.
3. **Admin-Kanal: ja.** Gesetzt mit `/bot channel set` aus dem Kanal selbst. Die Einrichtung steht in
   `docs/INSTALLME.md`.
4. **Längste Verschiebung: 6 Stunden** (einstellbar bis 24 Stunden). Das ist die längste Zeit, um die man einen
   Neustart, Stopp oder ein Update verschieben darf. Der Rest ist die Auswahl mit Vorgaben (15 Min bis 6 Std).
5. **Der Discord-Name steht nicht Ingame im Text**, nur im Protokoll.

Noch offen: Sollen Instanz-Kanäle getrennt sein (ein Admin-Kanal pro Server)? Vorerst ein Kanal für alle Server.

## 8. Stand der Prüfung, offene Punkte und Risiken

Geprüft: Sicherheitskern, Befehlslogik, Launcher-Anbindung, Discord-Schicht und Panel haben zusammen über 2300 automatische
Prüfungen (Windows-Build in GitHub Actions grün). Die Sicherheitsprüfung durch einen unabhängigen Reviewer hat keinen Weg
gefunden, wie ein Nichtberechtigter etwas auslösen kann, und keinen Weg, wie der Token in Dateien, Logs oder die Oberfläche
gelangt. Seine Funde sind behoben: Verschieben ist nach oben begrenzt, ein Stopp wird nur nach hinten geschoben, Null-Listen
in der Konfiguration sind „nichts“, eine beschädigte Datei wird nicht aus dem Backup repariert, Abbrechen und abgelaufene
Rückfragen zählen nicht als Ablehnung, Notizen enthalten keine freien Namen, das Minuten-Menü gilt nur im eigenen Kanal.

**Noch nicht an einem echten Discord-Server getestet** (nur mit Fakes und der dekompilierten Bibliothek geprüft):
- Ob Discord die Slash-Befehle mit `contexts`/`integration_types` für einen Server annimmt (sonst registriert der Bot sie ohne).
- Ob ein Klick auf einen Button der öffentlichen Meldung wirklich eine neue private Antwort anlegt und die Bestätigung die
  eigene private Frage ersetzt.
- Dass eine entzogene Rolle sofort wirkt, das Verlassen fremder Server, Wiederverbindung nach Ausfall, falscher Token.
- Windows-Verschlüsselung des Tokens auf einem echten Konto, das Fenster mit dem Panel (nur als Bild im Testfenster geprüft).
- Alles zusammen mit einem echten Conan-Server (Neustart, Stopp, Verschieben): bitte zuerst mit einem Test-Save.

**Bewusst offen gelassen** (klein, siehe Reviewer-Bericht):
- Die Slash-Befehle sind für alle Mitglieder sichtbar (Trebuchet prüft jeden Aufruf selbst). Wer sie verstecken will, stellt sie
  in Discord unter Servereinstellungen → Integrationen auf die Rolle ein.
- Die Umgebungsvariable `TREBUCHET_DISCORD_TOKEN` wird an Kindprozesse vererbt. Unter Windows besser den verschlüsselten Token
  im Panel speichern.
- Verschiebt man einen Server mitten in einer gemeinsamen Update-Runde, können die anderen zweimal neu starten.
- Buttons von Befehlen, die ein Nutzer nicht darf, zählen beim Klick als Ablehnung für dessen Sperre.

**Ohne Fenster (`boulder bot`)**: ersetzt das Fenster für diese Installation und startet nicht, solange das Fenster läuft.
Nicht übernommen: „System wach halten“, der Selbst-Neustart nach hängenden Ticks, das Schließen des Konsolenfensters beendet
den Bot nicht sauber (die Server laufen weiter).
