# Plan: Conan Exiles Enhanced Support in Trebuchet

## Ziel

Trebuchet soll drei Game Builds unterstützen:

| Button (UI) | Intern | Bedeutung |
|-------------|--------|-----------|
| **Legacy** | bisheriges „Live“ | UE4 / Legacy-Branch — heutiges Verhalten |
| **Enhanced** | neu | UE5 Enhanced — neuer Support-Pfad |
| **Test Live** | unverändert | bestehender TestLive-Pfad |

Ausgangspunkt UI: `GameBuildWindow` (heute zwei Buttons: Live | Test Live).  
Label **Live → Legacy** (Inhalt/Verhalten von heutigem Live bleibt).  
Neuer Button **Enhanced** — daran hängt der gesamte Enhanced-Support.

---

## Ausgangslage (Research)

### Installationsunterschiede (verifiziert)

| Thema | Legacy (UE4) | Enhanced (UE5) |
|-------|--------------|----------------|
| Steam AppID Client | `440900` | `440900` (gleich) |
| Client Win64 EXE | `ConanSandbox.exe` | `ConanSandbox-Win64-Shipping.exe` |
| BattlEye | `ConanSandbox_BE.exe` | gleich |
| Client-INI-Ordner | `Saved\Config\WindowsNoEditor\` | `Saved\Config\Windows\` |
| Content | klassische `*.pak` | IoStore (`.ucas` / `.utoc` / `pakchunk*`) |
| Workshop-Tag | `legacy` (Filter) | **`Enhanced`** (Modkit setzt automatisch) |
| Workshop-Pfad | `steamapps\workshop\content\440900\{id}\*.pak` | gleich (gemeinsamer Namespace) |

Portierte Mods bekommen **neue Workshop-IDs**. UE4-Paks laufen nicht unter Enhanced.

### Trebuchet heute (relevant)

- Build-Wahl: `bool testlive` → `OpenApp(false|true)`
- Args: `--live` / `--testlive`
- Config: `settings.live.json` / `settings.testlive.json`
- Datenordner: `Live` / `TestLive`
- Client-Validierung: erwartet `...\Binaries\Win64\ConanSandbox.exe` → **Enhanced fällt durch**
- Launch: `GetBinFile` → `ConanSandbox.exe`
- Workshop-Suche: nur AppID Live vs TestLive, **kein Tag-Filter**
- `QueryFilesQuery.RequiredTags` existiert in SteamWorksWebAPI, ist aber nicht angebunden

### Referenzpfade (lokal)

- Legacy-Kopie: `D:\Games\Conan Exiles ue4`
- Enhanced: `G:\SteamLibrary\steamapps\common\Conan Exiles`
- Workshop: `G:\SteamLibrary\steamapps\workshop\content\440900`

---

## Architektur-Entscheidung

### `GameEdition` Enum (statt nur `bool IsTestLive`)

```csharp
public enum GameEdition
{
    Legacy,    // bisher Live / UE4
    Enhanced,  // neu / UE5
    TestLive   // unverändert
}
```

**Warum Enum statt zweites Bool:** Legacy und Enhanced teilen AppID `440900`, unterscheiden sich aber bei EXE, INI-Pfad und Workshop-Tags. Ein Bool `IsTestLive` reicht nicht.

### Mapping pro Edition

| | Legacy | Enhanced | TestLive |
|---|--------|----------|----------|
| CLI-Arg | `--live` (Kompatibilität) oder `--legacy` | `--enhanced` | `--testlive` |
| Config-Datei | `settings.live.json` (bestehend) | `settings.enhanced.json` (neu) | `settings.testlive.json` |
| Datenordner | `Live` | `Enhanced` | `TestLive` |
| Client AppID | `440900` | `440900` | `931180` |
| Server AppID | `443030` | `443030`* | `931580` |
| Client EXE (ohne BE) | `ConanSandbox.exe` | `ConanSandbox-Win64-Shipping.exe` | wie Legacy/je nach Build |
| INI User | `WindowsNoEditor` | `Windows` | wie bisher |
| Workshop `RequiredTags` | `legacy` (oder Exclude `Enhanced`) | `Enhanced` | (TestLive-AppID; Tags prüfen) |

\* Server Enhanced vs Legacy ggf. separat verifizieren (Depot/Build). In Phase 1 Client zuerst.

### Kompatibilität

- `--live` bleibt Alias für **Legacy** (Autostart / Shortcuts nicht brechen).
- Resource-Key `Live` → Anzeige **Legacy**; interner Ordner `Live` kann vorerst bleiben (weniger Migration).
- Optional später: Ordner `Live` → `Legacy` umbenennen + Migration — **nicht** in Phase 1.

---

## Phasen (eine Funktion pro Schritt)

### Phase 0 — Fundament: Edition-Modell ✅

**Ziel:** Eine Edition durch die App tragen, ohne Enhanced-Logik noch zu brauchen.

**Dateien:**
- `TrebuchetLib\GameEdition.cs` (neu) — Enum
- `TrebuchetLib\Constants.cs` — `argEnhanced`, `FileEnhancedConfig`, `FolderEnhanced`, `FileClientBinShipping`
- `TrebuchetLib\Services\AppSetup.cs` — `GameEdition` statt/neben `IsTestLive`; Properties abgeleitet
- `Trebuchet\App.axaml.cs` — `OpenApp(GameEdition)`; Args `--enhanced` / `--live`→Legacy; Restart-Arg
- `Trebuchet\Utils\Utils.cs`, `AppConstants.cs` — Autostart-Einträge für Enhanced
- `Boulder\Program.cs` — CLI-Args an `GameEdition` anbinden (heute nur `--testlive`)

**Done wenn:** App startet mit drei Args; Legacy und TestLive verhalten sich wie heute.

**Off-limits:** Workshop-Tags, EXE-Erkennung noch nicht zwingend (folgt Phase 2).

---

### Phase 1 — Game Build UI ✅

**Ziel:** Drei Buttons: Legacy | Enhanced | Test Live.

**Dateien:**
- `Trebuchet\Windows\GameBuildWindow.axaml` — drittes Button-Slot; Label Live→Legacy-Resource
- `Trebuchet\ViewModels\GameBuildViewModel.cs` — `EnhancedCommand` → `OpenApp(GameEdition.Enhanced)`
- `Trebuchet\Assets\Resources.resx` (+ `.de` / `.fr`) — `Legacy`, `Enhanced`; `Live`-Text auf „Legacy“ oder neuen Key nutzen

**Layout-Vorschlag:** Fenster etwas breiter (~560–600); drei 128×128 Buttons in einer Reihe (Legacy | Enhanced | Test Live). Icon Enhanced z. B. `mdi-rocket-launch` oder `mdi-star-four-points` (von Live/TestLive unterscheidbar).

**Done wenn:** Klick auf Enhanced startet App mit `GameEdition.Enhanced` und eigener Config/Datenordner.

---

### Phase 2 — Client-Pfad + Launch-EXE (erster technischer Baustein Enhanced) ✅

**Ziel:** Enhanced-Install wird erkannt und korrekt gestartet.

**Dateien:**
- `TrebuchetLib\Tools.cs` — `IsClientInstallValid` edition-aware
- `TrebuchetLib\Services\AppSetup.cs` — `GetBinFile` → Shipping-EXE bei Enhanced
- `TrebuchetLib\Services\Launcher.cs` — Prozessname für Child-Process (Shipping vs `ConanSandbox`)
- ggf. Onboarding in `Trebuchet\Services\Operations.cs`

**Erkennung Enhanced-Install (Priorität):**
1. Existiert `ConanSandbox\Binaries\Win64\ConanSandbox-Win64-Shipping.exe` und fehlt `ConanSandbox.exe` → Enhanced-Layout
2. Optional: IoStore (`*.ucas` in `Content\Paks`) als Zusatzsignal
3. **Nicht** allein auf `appmanifest` Name „Conan Exiles Enhanced“ verlassen (auch lokale UE4-Kopie kann den Namen tragen)

**Done wenn:** Enhanced-Pfad als gültig gilt; Start ohne BattlEye startet Shipping-EXE; mit BattlEye weiter `_BE.exe`.

---

### Phase 3 — Client-INI-Pfad ✅

**Ziel:** Profile/INI schreiben unter dem richtigen Platform-Ordner.

**Dateien:**
- `TrebuchetLib\Constants.cs` — edition-abhängiger User-INI-Pfad oder Helper
- `TrebuchetLib\YuuIni\YuuIniClientFiles.cs` — `Windows` vs `WindowsNoEditor`

**Done wenn:** Client-Einstellungen unter Enhanced in `Saved\Config\Windows\` landen.

---

### Phase 4 — Workshop: Enhanced-Tag ✅ (server-side RequiredTags/ExcludedTags + return_tags in DepotDownloader)

**Ziel:** Im Enhanced-Modus nur Enhanced-Mods suchen / laden / warnen.

**Dateien:**
- Steam-Session / `QueryPublishedFileSearch` — `RequiredTags` durchreichen
- `TrebuchetLib\Services\Steam.cs` — Suche + Details mit Tags
- `Trebuchet\ViewModels\WorkshopSearchViewModel.cs` — bei Enhanced: `RequiredTags=Enhanced`
- Mod-Add / Import / Launch-Pfad — Tag prüfen; fehlendes Tag → ablehnen oder warnen
- `GetPublishedFileDetails` — `IncludeTags` / `return_tags` aktivieren
- UI-Badges in `WorkshopModFile.cs` — Legacy / Enhanced / TestLive

**Regeln:**
- **Enhanced-Edition:** Workshop-Suche mit `RequiredTags=Enhanced`; Listen-Einträge ohne Tag blocken/warnen
- **Legacy-Edition:** `RequiredTags=legacy` **oder** `ExcludeTags=Enhanced` (exakte Steam-Tag-Strings an einem bekannten Item verifizieren: `Enhanced` / `legacy`)
- **TestLive:** weiter AppID `931180`; Tag-Policy separat klären

**Done wenn:** Enhanced-Suche liefert nur Enhanced-Mods; Legacy-Pak in Enhanced-Liste wird nicht stillschweigend gestartet.

---

### Phase 5 — Hardening & UX ✅

- Klare Fehlermeldung bei falscher EXE / gemischter Modliste ✅
- Edition im Fenstertitel (`Tot!Trebuchet — Legacy|Enhanced|Test Live`) ✅
- Workshop-Badges Enhanced/Legacy/TestLive + Styles ✅
- Launch blockt inkompatible Workshop-Mods (Client + Server) ✅
- Add-from-Workshop prüft Kompatibilität ✅
- Autostart Enhanced (`TotTrebuchetEnhanced`) ✅ (Phase 0)
- `ResolveMod`: weiter `*.pak` (IoStore-Workshop später)
- Server-Depot Enhanced vs Legacy: weiterhin gemeinsames `443030` — manuell prüfen

---

## Empfohlene Reihenfolge

```
Phase 0 (Enum + Args + Config)
    → Phase 1 (UI: Legacy | Enhanced | Test Live)
        → Phase 2 (Client valid + Launch EXE)   ← erster „Enhanced funktioniert“-Meilenstein
            → Phase 3 (INI Windows)
                → Phase 4 (Workshop Enhanced-Tag)
                    → Phase 5 (Hardening)
```

Nach jeder Phase: manuell prüfen (Start-Dialog → Edition → Client-Pfad), dann nächste Phase.  
Kein „alles auf einmal“.

---

## Risiken

1. **Gleiche AppID** — ohne Tag-Filter mischen sich UE4- und UE5-Mods.
2. **Zwei Installs, ein Workshop-Ordner** — Steam lädt denselben `440900`-Content; Trebuchet-Cache (`Workshop\440900`) kann falsche Generation halten.
3. **Prozess-Erkennung** — Shipping-Prozessname ≠ `ConanSandbox`.
4. **Server** — Dedicated-Server-Depot (`443030`) muss zur Client-Edition passen; separat testen.
5. **Breaking Autostart** — `--live` muss Legacy bleiben.

---

## Definition of Done (Gesamt)

- [x] Game Build zeigt **Legacy | Enhanced | Test Live**
- [x] Legacy = bisheriges Live-Verhalten (`--live`, `settings.live.json`, `FolderLive`)
- [x] Enhanced: eigene Config/Daten, Shipping-EXE, `Windows`-INI, Workshop-Tag `Enhanced`
- [x] TestLive unverändert
- [x] Keine stillen Misch-Modlisten Enhanced↔Legacy (Add + Launch-Check)
- [x] `--live` / `--testlive` / Autostart kompatibel; `--enhanced` neu

---

## Status

**Phasen 0–5 implementiert** (Stand dieses Plans).

Manuell prüfen:
1. Start → drei Buttons → Titel zeigt Edition
2. Enhanced + Enhanced-Install-Pfad → Client startet Shipping-EXE
3. Workshop Enhanced → nur Enhanced-Mods; falsche Mod → Fehlerdialog
4. Legacy weiter mit `--live` / Autostart

Build erfordert gefüllte Git-Submodule (`tot-lib`, `tot-gui-lib`, `DepotDownloader`).
