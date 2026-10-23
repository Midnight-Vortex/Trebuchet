# Discord-Bot einrichten

> **Stand:** Der Sicherheitskern des Bots ist fertig und getestet. Die Verbindung zu Discord und der Menüpunkt
> „Discord Bot“ im Launcher kommen in den nächsten Schritten, siehe `docs/plan-discord-bot.md`. Die Schritte 1 bis 4
> kannst du schon jetzt vorbereiten, ab Schritt 5 gilt die Anleitung, sobald der Menüpunkt da ist.

Der Bot läuft **in Trebuchet**. Er öffnet keinen Port, er verbindet sich nur ausgehend mit Discord. Zugriff hat nur,
wer **die Discord-User-ID in der Liste hat UND eine der erlaubten Rollen**. Fehlt eins von beiden, passiert nichts.

## 1. Bot bei Discord anlegen

1. <https://discord.com/developers/applications> öffnen, **New Application**, einen Namen geben.
2. Links **Bot**:
   - **Public Bot: aus.** Sonst kann ihn jeder auf seinen Server einladen.
   - Bei **Privileged Gateway Intents** bleibt **alles aus** (Presence, Server Members, Message Content).
   - **Reset Token**, den Token kopieren. Er wird nur **einmal** angezeigt.
3. Das Konto, mit dem du im Developer-Portal angemeldet bist, sollte **2-Faktor-Login** haben.

> Den Token **nie** in Discord, Screenshots oder Dateien weitergeben. Wer ihn hat, kann sich als dein Bot melden.
> Bei Verdacht: im Portal **Reset Token** und in Trebuchet den neuen eintragen.

## 2. Bot einladen

1. **OAuth2 → URL Generator**.
2. **Scopes:** `bot` und `applications.commands`.
3. **Bot Permissions:** nur **View Channels**, **Send Messages**, **Embed Links**.
   **Keine Administrator-Rechte.**
4. Die erzeugte URL öffnen, deinen Server wählen, einladen.

## 3. IDs sammeln

In Discord unter **Einstellungen → Erweitert** den **Entwicklermodus** einschalten. Dann geht überall
**Rechtsklick → ID kopieren**:

| Was | Wo |
|---|---|
| **Server-ID** | Rechtsklick auf den Server |
| **Deine User-ID** (Super-Admin) | Rechtsklick auf deinen Namen |
| **User-IDs weiterer Personen** | Rechtsklick auf den Namen |
| **Rollen-ID** | Servereinstellungen → Rollen → Rechtsklick auf die Rolle |

Die Rolle `@everyone` ist **nicht erlaubt** (jeder hat sie), Trebuchet lehnt sie ab.

## 4. Rolle und Kanal auf Discord

1. Eine **eigene Rolle** anlegen, z. B. `Trebuchet-Admin`, ohne besondere Rechte. Sie bekommt nur, wer den Bot
   benutzen darf. **Auch du** brauchst sie.
2. Einen **privaten Kanal** anlegen, den nur diese Rolle sieht, z. B. `#server-admin`.
3. Der Bot muss in diesem Kanal **Nachrichten sehen und senden** dürfen.

## 5. In Trebuchet eintragen (Menüpunkt „Discord Bot“)

1. **Bot-Token** einfügen. Er wird verschlüsselt (Windows-Benutzer) gespeichert und danach nie wieder angezeigt.
2. **Server-ID** eintragen.
3. **Super-Admin:** deine User-ID. Sie ist Pflicht und lässt sich nicht entfernen.
4. **Erlaubte Rollen:** die Rollen-ID.
5. **Erlaubte User:** die User-IDs weiterer Personen.
6. **Sicherheit prüfen** drücken. Alle Punkte müssen grün sein.
7. **Starten**.

Ohne Token, Server-ID, Super-Admin **und** mindestens eine Rolle startet der Bot nicht.

## 6. Admin-Kanal festlegen

Im privaten Kanal als **Super-Admin** den Befehl ausführen:

```
/bot channel set
```

Mit dem Bestätigungsknopf bestätigen. Danach

- antwortet der Bot **nur noch in diesem Kanal**,
- postet er dort pro Server eine **Status-Meldung** mit Buttons und hält sie aktuell.

Den Kanal wieder freigeben (alle Kanäle) geht nur im Launcher, nie über Discord.

## 7. Benutzen

Die Status-Meldung zeigt den Zustand (grün online, gelb es steht etwas an, blau verschoben, rot gestoppt).

- **Neustart verschieben / Stopp verschieben / Update verschieben:** erscheint, wenn gerade etwas ansteht. Nach dem
  Klick wählst du aus: **15 Min, 30 Min, 1 Std, 2 Std, 3 Std, 6 Std**. Ingame steht danach „Der Neustart von
  {servername} wurde um X Minuten verschoben.“
- **Wieder freigeben:** hebt die Verschiebung auf.
- **Neustart, Stoppen, Starten:** sind zuerst **ausgeschaltet**. Im Launcher unter *Befehle* einschaltbar. Dann
  fragt der Bot vor der Ausführung noch einmal per Knopf nach. Starten dürfen nur Super-Admins.

## 8. Sicherheit auf einen Blick

- Zugriff nur mit **User-ID in der Liste und Rolle**. Der Super-Admin braucht die Rolle auch.
- Nur dein **einer Server**, nur der **Admin-Kanal**, nie in Direktnachrichten.
- Rechte (User, Rollen, Befehle) änderst du **nur im Launcher**, nie über Discord.
- Kein RCon, keine Skripte, keine Dateien: der Bot kann nur die Aktionen der Liste.
- Zu schnelle oder abgelehnte Versuche werden gebremst und gesperrt. Alles steht im Protokoll
  (`discord-bot-audit-<Jahr><Monat>.log` im Logordner von Trebuchet).
- Der Token liegt verschlüsselt, nicht in Einstellungen, Logs, Exporten oder Crash-Berichten.
- Ein Token darf nur von **einem** Trebuchet gleichzeitig benutzt werden.

## 9. Ohne Oberfläche (Boulder)

Der Bot läuft auch ohne Fenster, z. B. auf einem Server, an dem niemand angemeldet bleibt:

```
boulder bot --enhanced
```

- Die Edition wählst du wie bei den anderen Boulder-Befehlen mit `--live`, `--enhanced`, `--ptc` (ohne Angabe: Legacy).
- `boulder bot` **ersetzt das Fenster** für diese Installation: es überwacht die Server, macht die automatischen
  Neustarts und Updates und betreibt den Bot. Läuft das Fenster, startet es **nicht**. Umgekehrt öffnet sich das
  Fenster nicht, solange `boulder bot` läuft.
- Eingerichtet wird im Fenster (Schritt 5). Danach das Fenster schließen und `boulder bot` starten. Fehlen
  Einstellungen oder Token, startet nichts und es steht da, was fehlt. Den Admin-Kanal (Schritt 6) setzt du wie
  gewohnt in Discord, auch während `boulder bot` läuft.
- **Token:** Unter Windows nimmt es den im Fenster gespeicherten Token (gleicher Windows-Benutzer) oder die
  Umgebungsvariable `TREBUCHET_DISCORD_TOKEN`. Ohne Windows geht es **nur** über diese Umgebungsvariable. Den Token nie
  in die Befehlszeile schreiben, die können andere Benutzer sehen.
- **Beenden mit Strg+C.** Die Server laufen weiter, wie beim Schließen des Fensters.
- Rückgabewerte für Skripte: `0` beendet, `1` Trebuchet läuft schon, `2` Einstellungen fehlen oder sind ungültig,
  `3` kein Token, `4` der Token wird schon von einem anderen Trebuchet benutzt, `5` Discord lehnt den Token ab oder
  der Bot ist nicht auf dem Server.
- Das Protokoll steht wie beim Fenster im Logordner (`boulder.log` und `discord-bot-audit-<Jahr><Monat>.log`).
