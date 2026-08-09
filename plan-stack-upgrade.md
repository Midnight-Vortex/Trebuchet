# Plan: Stack-Upgrade A → B → C (+ Upstream)

**Ziel:** Abhängigkeiten heben ohne Funktionsverlust. Reihenfolge fest — nach jeder Phase: `dotnet build -c Release` grün.

**Upstream-Hinweis:** Totchinuko `e61b381` („Compatibility with Enhanced“) — bool `_enhanced`/`_testlive`, Shipping-EXE, UI-Button. Unser Fork hat bereits `GameEdition` + mehr (INI, Workshop-Tags, Junctions, Fixes). Upstream **nicht blind mergen**; nach C gezielt vergleichen (Phase D).

---

## Phase A — Avalonia 11.3 + sichere Patches ✅/⏳

| Paket | Von → Nach | Hinweis |
|-------|------------|---------|
| Avalonia / Desktop / Fluent / Fonts / Diagnostics | 11.2.5 → **11.3.19** | Layout-Perf |
| Avalonia.ReactiveUI | 11.2.5 → **11.3.9** | an 11.3 koppeln |
| Avalonia.AvaloniaEdit | 11.2.0 → **11.3.0** | |
| Avalonia.Xaml.Behaviors (tot-gui-lib) | 11.2.0.14 → **11.3.0.6** | |
| AsyncImageLoader.Avalonia | 3.3.0 → **3.8.0** | |
| Projektanker.Icons… | 9.6.1 → **9.6.2** | |
| Microsoft.Extensions.* / System.Management | 9.0.4 → **9.0.x latest** | bleibt net9 |
| Serilog | 4.2.0 → **4.3.x/4.4.x stable** | kein Preview |

**Projekte:** `Trebuchet`, `tot-gui-lib` (Avalonia gemeinsam).  
**Nicht (damals Phase A):** Avalonia 12, SteamKit 3.4 — Humanizer 3 / Markdig 1 / Rx 7 später in Post-C erledigt.

**DoD A:** Release-Build 0 Errors; App startet; Game Build + Settings öffnen.

---

## Phase B — .NET 10 + SteamKit 3.4

| Schritt | Inhalt |
|---------|--------|
| B0 | .NET **10 SDK** installieren (lokal nur 8/9 vorhanden) |
| B1 | Alle `net9.0` → `net10.0` (Trebuchet, Boulder, TrebuchetLib, DepotDownloader, …) |
| B2 | SteamKit2 **3.4.0** + protobuf-net passend |
| B3 | DepotDownloader-Fork: `Utils.AdlerHash` → `DepotChunk.AdlerHash` und weitere 3.4-Breaks |
| B4 | Build + Smoke: Steam Connect, Workshop-Suche, Mod-Download |

**DoD B:** Release-Build; Steam-Login/Workshop ohne Crash.

---

## Phase C — Avalonia 12

| Schritt | Inhalt |
|---------|--------|
| C1 | Avalonia **12.x** + Desktop/Fluent/Fonts/Diagnostics |
| C2 | Avalonia.ReactiveUI / AvaloniaEdit / HtmlRenderer / HyperText / Icons auf 12-kompatible Versionen |
| C3 | Compile-Fixes (APIs, Themes, Behaviors) |
| C4 | Manuell: Fenster, Dashboard, Mods, Settings, Dialoge |

**DoD C:** Release-Build; Kern-UI bedienbar.

---

## Phase D — Upstream Totchinuko prüfen (nach C)

Commit: https://github.com/Totchinuko/Trebuchet/commit/e61b381845d3420cc2f352c32790722e68a36ff0

| Upstream | Unser Stand | Aktion |
|----------|-------------|--------|
| `--enhanced` + Shipping-EXE | bereits + GameEdition | behalten unser Model |
| `GetConfigPath(testlive, enhanced)` | `GetConfigPath(GameEdition)` | kein Downgrade auf bool |
| GameBuild 3 Buttons | vorhanden | ggf. Label/UX abgleichen |
| `PublishSingleFile` auskommentiert | bei uns oft an | nur übernehmen wenn Publish-Probleme |
| Validierung `IsEnhanced` | edition-aware Tools | prüfen ob Upstream-Edgecases fehlen |
| Process-Name Enhanced | prüfen Launcher | Diff gegen Upstream, fehlende Fixes übernehmen |

**DoD D:** Diff-Liste dokumentiert; sinnvolle Fixes gemerged; Build grün.

---

## Stop-Signale

- Phase schlägt Build fehl → fixen bevor nächste Phase  
- SteamKit ohne .NET-10-SDK → B0 zuerst  
- Upstream-Merge darf Enhanced-Tags / Junction-Isolation / UIConfig-pro-Edition **nicht** zurückdrehen  

---

## Status

- [x] A — Avalonia 11.3.19 + ME 9.0.18 + Serilog 4.4; FileDrop → DataTransfer; AsyncImageLoader 3.7 (Zwischenstand; 3.8 mit C)
- [x] C follow-up — AsyncImageLoader.Avalonia **3.8.0** (Avalonia 12)  
- [x] B — net10.0 + SteamKit 3.4 + DepotDownloader AdlerHash  
- [x] C — Avalonia 12.1.1 + ReactiveUI.Avalonia 12.1.1 + Xaml.Behaviors.Avalonia 12.0.5; RxVoid alias; WindowDecorations; $parent typed bindings
- [x] C follow-up — Diagnostics: `Avalonia.Diagnostics` hat kein 12.x (max 11.3.19, deprecated) → `AvaloniaUI.DiagnosticsSupport` **2.2.3** (Debug) + `AttachDeveloperTools()`  
- [x] D — Upstream `e61b381` ist schlankeres bool-Enhanced; unser `GameEdition`-Stack bleibt. Kein Downgrade. Watermark→PlaceholderText.  

**Binary:** `Trebuchet\bin\Release\net10.0\win-x64\Trebuchet.exe`

### Post-C cleanup
- [x] Debug+Release solution: **0 warnings / 0 errors** (DepotDownloader CS0414/IDE0055 fixed; DiagnosticsSupport always referenced)
- [x] Avalonia 12 leftover: `Watermark` → `PlaceholderText` (styles + OnBoardingNameSelection)
- [x] DynamicData **9.4.33** (Trebuchet + tot-gui-lib)
- [x] Bugbot round 1: Steam credential auth restored; Enhanced `account.config` path
- [x] Bugbot round 2 fixes: search empty-tags trust server; auth epoch guard; MaxPage ceiling + clear results
- [x] Bugbot round 3: backoff-reconnect also bumps `authEpoch` — re-review **0 findings**
- [x] Runtime: Optris Icons (start crash); WindowDecorations.None on custom titlebar (double chrome); TimeOfDayListField Skip(1) (edition StackOverflow)
- [x] Perf: lazy BuildFields (Settings/Server/Client); SetupFolders off UI thread; RefreshActivePanel before onboarding; GameBuild IsOpening UX
- [x] DepotDownloader: `TreatWarningsAsErrors` immer (wie übrige Projekte)
- [x] Post-C package cleanup (2026-08-09): **Markdig 1.3.2** (Trebuchet); **Humanizer 3.0.10** (Trebuchet + tot-gui-lib, `Duration.axaml.cs` Localisation removed, `AppResources` alias in 8 Humanizer+Assets files); **Pastel removed** (Boulder, unused); **System.Reactive 7.0.0** (Trebuchet + tot-gui-lib, direct ref overrides DynamicData 6.1.0 pin — build OK)
- [x] Post-C Icons: **Projektanker.Icons.Avalonia 9.6.2** → **Optris.Icons.Avalonia 12.0.7** (Avalonia 12 fork; XAML xmlns unchanged; fixes `MissingMethodException` on `TemplateBinding.ProvideValue` at startup)
- [x] Post-C perf: parallel `Launcher.Tick` process refresh (`Task.WhenAll` client+servers); `ConanClientProcess.RefreshAsync` offloads `Process.Refresh`; `Steam.CheckModsForUpdate` parallel disk resolve; `Tools.DeepCopyAsync` bounded `Parallel.ForEachAsync`; dashboard `ProcessRefresh` + ticking panels `WhenAll`
- [ ] Skipped: `Config.InstallPath` Obsolete/onboarding (`Operations.cs` CS0612) — kein Avalonia-Thema

