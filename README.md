# Trebuchet

After installing, launch Trebuchet from the Start menu or desktop shortcut. The installer deliberately does not launch it: on affected Windows versions, a process started by Setup can inherit RedirectionGuard and fail to traverse Trebuchet's Saved/profile junctions. Conan then reports that it cannot ensure `Saved/ExtractedMods` exists, even when the directory is present. If this happens after an older setup, fully close Trebuchet and relaunch it from the Start menu; reinstalling mods is not required for this directory-access error.

## 0.9.28 faster Discord bot, cancel told at once, thrall relevance distance

- **The Discord bot answers faster.** A command or a button of the status message is answered with one call to Discord instead of two, the state of the servers is read once for everybody who asks at the same time (button, status message, community log), the status messages and the community log refresh side by side, and the status message can be edited every 2 seconds (it was 5). The answers of the bot no longer wait for the notice to the players in the game; that goes on in the background. If an answer takes longer than 1.5 seconds, Discord shows "thinking" as before. `/restart postpone` is still confirmed first, because it adds the minutes every time.
- **A cancelled restart or stop is told in the community log at once** ("✅ The restart is cancelled"), no longer after about 25 seconds. A restart or stop "now" is never taken for a cancellation, also not while the launcher still shows the sequence of the countdown. The notices to the players of one server keep their order (for example "postponed" before "cancelled").
- **New setting in the server save: "Thrall relevance distance"** (`dw.ThrallMaxRelevantDistance`). It is the greatest distance from a player at which a thrall still counts as relevant, in game units; it is written into the `[SystemSettings]` of the `Engine.ini` of the server every time the server starts. A foundation is about 2.6 m wide, roughly 260 units: 5 foundations are about 1300, 10 foundations about 2600. 0 (the default) writes nothing and removes a line that was written before, other lines of the section stay. Whether the game honours the value in this section is not confirmed yet.

## 0.9.27 postponing adds, cancel button, name and picture of the bot

- **Postponing a restart adds the minutes to the planned time.** A restart in 30 minutes, postponed by 30 minutes, now goes down after 60 minutes (before it was a pause of 30 minutes from now, so after about 37). Postponing again, while already postponed, adds to the pause that runs and never brings the restart forward. **"Release again" brings the old time back**: the restart goes down when it was planned for before (the players read "restarts in 27 minutes" with the old time), no longer with a new full countdown of 5 minutes from nothing.
- **Cancel button** ("Cancel restart", "Cancel stop", "Cancel update") on the status message while a countdown runs, also as `/restart cancel`. It ends the countdown at once without a question, the players read "cancelled" in the game, and the community log tells it too. A restart that the schedule or an update started on its own would begin again with the next tick, so the automatic restarts wait 30 minutes after such a cancel (this counts against the longest postponement of a server start, so an update can not be put off for ever by cancelling again and again). A countdown somebody started by hand just ends. Whoever may postpone may cancel (switched on once for existing settings, can be switched off like any command).
- **The community log tells when an announced restart or stop is cancelled or postponed** ("✅ The restart is cancelled", "⏸️ The restart was postponed" with the new time), and a stop planned instead of an announced restart says the restart does not happen. It is careful not to call a restart "now" that takes a moment to show itself a cancellation.
- **The bot account can take the name and picture of the community log** (new switch "Bot takes this name and picture", off by default): the status message with its buttons and the answers are written by the bot account itself, and only a webhook can have another name per message. Discord allows two changes of the name an hour, and the name counts for every server the bot is on (2 to 32 characters). The picture is fetched from your computer with care: https only, no redirects, only addresses on the internet, at most 4 MB, PNG, JPEG, GIF or WebP.
- **The postponement budget counts the time a countdown or a pause still has left** (it did only for stops), so a restart can not be put off further than the longest postponement either.
- The default of the configured internet speed (an experimental setting of the client profile) is 50000.

## 0.9.26 community log and audit log identity

- **Community log for your players** (Discord Bot panel, section "Community log"): a channel of its own, written through a webhook with its own name and picture (for example the name of your server). Shortly before a planned or automatic restart or stop (default 30 minutes, 1 to 60 can be set) it posts "⏰ The server restarts in 30 minutes" with the time as Discord shows it in the clock of whoever reads it, then "🔄 The server restarts now" and "🟢 The server is back online" with how long it was offline. It covers the restart schedule of the save, restarts for an update, countdowns of the launcher (also started by hand or from Discord) and postponed restarts. A crash says nothing, with several servers the name of the server comes first, and it speaks the language of the bot.
- **The audit channel can have its own name and picture.** Enter a webhook for the audit channel, a name and a picture link in the panel: the log is then written under that name instead of the name of the bot (the channel ID is not needed with a webhook, without a webhook everything works as before). If the webhook does not work, the bot writes into the audit channel.
- **Webhook addresses are secret like the token:** stored encrypted for your Windows user, never part of the settings or logs, never shown again. A webhook of another Discord server than yours is never written to; a thread or forum post works with `?thread_id=` at the end of the address. Names with "discord" or "clyde" (Discord refuses them), and names or picture links that hold a token or a webhook address are refused by the panel.
- **A stop by hand is a stop:** shortly before a planned restart, or in an update round, it is told as a stop, not as a restart. After the bot was away for a while it does not tell old news as "now".

## 0.9.25 reason window and higher limit

- **Restart, Stop and Postpone from the buttons of the status message now ask for a reason.** After the choice of the time a small window opens with one text field. It may stay empty; submitting it runs the action at once, there is no further confirmation. The reason goes to the log, the audit channel and the warnings of the server (`{reason}`); left empty, the reason from the settings of Trebuchet is used (it used to say "Discord"). The slash commands `/stop` and `/restart now` still ask for a confirmation.
- **The reason is plain words.** Because it ends up in the macros of the launcher (warnings, webhook, RCon, REST, arguments of programs of a sequence), only letters, digits, spaces and `. , : - ( ) ?` are kept, at most 100 characters.
- **Higher limit of critical actions:** 10 per server in 10 minutes by default (was 3), up to 50 can be set. A settings file that still holds the old default gets the new one once.
- Security fixes found by a review: a code of a question only answers that kind of question (a window can not use the code of a menu or of a confirmation), and the menu stays usable when the window was closed.

## 0.9.24 audit channel in plain words

- **The audit channel now reads like normal sentences**, in the language of the bot: "🔔 @User asks: Stop server: Server Main (instance 0)", "✅ Done: Stop server: Server Main (instance 0), warning 5 min – under way (by @User)", "🔄 Server Main (instance 0): 🟡 Stopping → 🔴 Stopped". Refused, slowed down, locked out and cancelled requests are told the same way. The technical values (`instance=0 step=Confirm`), the user ID next to the mention and the IDs of buttons no longer show; the exact reason of a refusal stays in the log file.
- **A stop is told once, not three times.** The launcher reported the server as stopped for a moment before it had marked its aftermath (backup, scripts) as running, so the bot saw "Stopping -> Stopped -> Stopping -> Stopped". That is fixed, and the bot also only tells a new state once it held for 10 seconds, so a flicker between two steps is never news.

## 0.9.23 server names and audit channel

- **The name of a server is the "Server name" of its server save** (first field of the server saves), no longer a name set in the Discord Bot panel. The bot (status message, answers, questions), the Trebuchet log ("Stopping server 0 (Main server)", restarts, postponements, crashes) and the audit channel use it. While the save keeps the default name, the name of the save is shown; the log then shows the number only. The name field in the bot settings is gone, old settings files still load.
- **The audit channel now gets everything the bot does**: every request with server, user and result (also refused, slowed down and locked out ones), every state change of a server ("Server Main (instance 0): Online -> Stopping") and the bot's own notes (connected, problem). One line each, with a pause between lines; the exact reason of a refusal stays in the log file. Use a channel of its own: in the admin channel the lines would push the status messages away.

## 0.9.22 stop fix

- **Stopping or restarting a server (from Discord, with a countdown or at once) ended Trebuchet itself**, without a log line or a crash report, while the server kept running. Cause: for Ctrl+C, Trebuchet attached to the console of the server, and the event reached Trebuchet too. Now a short-lived helper process (Trebuchet itself, started with `--send-ctrl-c <pid>`) sends it: Trebuchet never attaches to a console. A server that does not react to Ctrl+C gets 45 seconds, then the RCon `shutdown` follows (before: 3 minutes). The log says before each step what is sent.
- **Hard ends now leave a report**: while Trebuchet runs it keeps a small marker file in the log folder. If the next start finds it from a process that is gone (killed from outside, power loss, a native crash), it writes `crash-unclean-<time>.json` with the last 60 lines of the log and logs a warning. A normal exit logs "Trebuchet closes" and removes the marker.

## 0.9.21 Discord bot follow-ups

- The **super admin always has every right** (the command switches only limit the other users). The status message always shows the buttons that fit the server state, whatever the switches say; every click is still checked.
- The status message is edited **right away on every real change** (start, online according to the Conan server log, stopping, stopped, crashed, countdowns, postponements, any action from Discord) and **every 5 minutes** as a check (every 15 seconds while a server starts or stops). The embed shows the time of the last check.
- **Restart and Stop buttons ask for the warning time first** (right away, 1, 5, 10, 15, 30 min, 1 h, restart also 2 h), like the dialog in Trebuchet, then for the confirmation.
- New **Updates check** button and `/updatecheck`: asks Steam about mod updates and a new server build, installs nothing.
- Bot settings: choose **which server instances the bot manages** and give each a **name** the bot uses in its messages.
- The Discord Bot panel has a **setup help** window (button next to Revert) that explains every step.

## 0.9.20 Discord bot

- New menu entry **Discord Bot** (below Dashboard): a bot that runs inside Trebuchet and lets authorized people postpone a restart, stop or update, and optionally restart, stop or start servers, from Discord. A status message per server with colored buttons is kept up to date in an admin channel.
- Security first: a user needs their Discord ID on the allowed list **and** one of the allowed roles (also the super admin), on the one configured server, in the admin channel. Everything is off until configured, critical actions are off by default and need a confirmation, postponing is bounded, every request is logged, the token is stored protected and never shown. Setup guide: [docs/INSTALLME.md](docs/INSTALLME.md), design: [docs/plan-discord-bot.md](docs/plan-discord-bot.md).
- `boulder bot` runs Trebuchet's launcher and the bot without a window.
- Not yet tested against a live Discord server: try it with a test server first.

## 0.9.7 parallel save copying

- Files now copy with four to eight concurrent workers, starting at four. Every second, Windows CPU/memory measurements and actual read/write latency guide the limit: two quiet samples allow one extra worker; high CPU/memory load, low available RAM or slow I/O remove two slots, down to four. In-flight files finish before excess workers pause. Missing metrics fall back to four. All workers share a 300 MB/s transfer budget (300,000,000 bytes/s) with a small bounded burst; idle periods do not accumulate unlimited burst credit.
- Each worker reuses a 256 KiB buffer (at most 2 MiB total). A bounded file queue prevents unbounded tasks/metadata; separate copy jobs still queue instead of multiplying disk load. Enumeration, progress and cancellation remain off the UI thread as before.
- Cancellation or a failed file stops the other workers and waits for their temporary-file cleanup. Progress remains monotonic and limited to ten updates per second. Regression checks cover concurrent transfers, the aggregate rate limit, checksums, cancellation and worker failure.

## 0.9.6 game management responsiveness

- Save/profile copies now enumerate and transfer files entirely in the background. A single copy queue replaces up to eight concurrent file copies; data is streamed through one 128 KiB buffer with a 32 MiB/s transfer budget. Metadata is streamed instead of building an array containing every file. Large-file transfers support cancellation and report progress at most ten times per second.
- Enabling game file management displays cancellable progress. Incomplete imports stay outside the profile list; cancellation preserves the original Saved folder and restores the previous management setting. File replacement uses temporary files so an interrupted copy does not corrupt an existing destination file.
- After import, the original Saved directory is retained until its junction has been created successfully. Junction failure restores the original directory. Cleanup and lock retries run off the UI thread. Size calculations stream entries and do not follow junction cycles.
- Regression coverage includes 32 MiB files, 2,000 small files, checksums, timestamps, empty folders, progress, cancellation during a large file, queued cancellation and overlapping-path rejection. This does not replace testing on the affected user's hardware.

## 0.9.5 console hotkey correction

The enabled console-hotkey setting writes `ConsoleKeys=Insert` (or the selected key) without a leading plus. Matching bindings from earlier versions are converted on the next game launch; other settings, encoding and console history are preserved.

## 0.9.4 server console fixes

- Local server selectors show the server name, instance and running/online state independently of RCON. The first running instance is selected automatically until the user chooses one; stopped instances clear stale process references. With no local server, the selector explicitly says so. This console manages local Trebuchet servers, not the remote server a game client has joined.
- Console text binds correctly when opening, switching or restoring an undocked console. Long lines trim safely within the existing memory limit. New console instances enable server/Trebuchet log filters by default while preserving saved filter choices.
- Attaching to a running server reads the last 64 KiB of existing logs. Incremental reads track byte offsets, preserve split UTF-8 characters and incomplete lines, and recover after log truncation.
- Failed RCON connections/authentication release the command lock so later attempts can retry. Errors appear in the console, and commands are available for running servers with RCON enabled before the query service reports online. Windows process detection ignores path casing.
- Run server-console regression checks with the ModListDisplayChecks harness and `-- --console-checks`; these use a real text editor, temporary log files and loopback connections without contacting a game server.

## 0.9.3 edition cleanup

Only Legacy, Enhanced and Enhanced PTC remain available. The obsolete fourth edition, its configuration/migration paths and workshop selector have been removed. `--ptc` selects Enhanced PTC; `--enhanced --ptc` remains supported. Combining `--live` with either Enhanced option is rejected by Boulder. Existing Enhanced PTC profile paths are unchanged, and no existing profile files are deleted by this update.

## 0.9.2 synchronization and performance update

- Synchronize profiles can be deleted with confirmation using the visible delete button or the selected profile's menu. Deleting the last profile leaves Synchronize empty, including after restarting; stale dashboard selections no longer recreate it. Other profile types keep their existing default-profile behavior.
- Empty Synchronize panels explain how to create/import a profile and disable actions that require a selection. Changing profiles updates both mods and client connections. A cancelled URL prompt no longer starts a sync.
- Profile selection changes are delivered even while an earlier workshop lookup is running, including when deleting the selected profile. A regression test reproduces the dropped-selection bug found during review.
- Workshop metadata refresh deduplicates requested IDs and indexes results by ID instead of repeatedly scanning them. Display filters reuse unchanged regex expressions, and profile file-watcher events are coalesced on the UI dispatcher. Search semantics, regex timeouts and persisted mod load order are unchanged.
- Product versions are shared by Trebuchet, Boulder and the setup through the root `VERSION` file. Regression tests cover confirmation/cancellation, last-profile deletion, reopening an empty list, deleting a different profile, deletion failure and required-default compatibility.

## 0.9.1 maintenance update

- Updated all direct NuGet references with available stable releases (checked September 17, 2026), including Avalonia 12.1.2, Microsoft extensions 10.0.12, Discord.Net.Webhook 3.20.1 and protobuf-net 3.4.30. The installer is built with Inno Setup 7.1.0.
- Added Enhanced PTC selection from [upstream commit 3c6068fb](https://github.com/Totchinuko/Trebuchet/commit/3c6068fb). It uses separate `EnhancedPTC` profile/server folders and `settings.enhanced.ptc.json` / `settings.ui.enhanced.ptc.json`. Existing Legacy and Enhanced profiles keep their locations.
- `--enhanced --ptc` selects Enhanced PTC and adds `-xls=ext` when launching the game. GUI restart, autostart and Boulder use the same edition parsing.
- Switching a mod list to read-only preserves its entries. Workshop metadata refresh preserves disabled delete actions; loading state resets even if a refresh fails. Sorting and regex search continue to affect display only.
- Bug review caught unregistered Boulder edition switches: the actual command-line parser now accepts them before or after subcommands, with regression coverage. Restart no longer accumulates duplicate Enhanced PTC switches.

Build with the .NET 10 SDK and Inno Setup 7. Publish `Trebuchet/Trebuchet.csproj` and `Boulder/Boulder.csproj` for `win-x64` with `--self-contained true`, then compile `build_setup.iss`. For a solution build use `dotnet build Trebuchet.sln -c Release -m:1 -p:SelfContained=false -p:PublishSingleFile=false`. Run the two regression harnesses in `Tests/ConsoleHotkeyChecks` and `Tests/ModListDisplayChecks` with those same property overrides.

Trebuchet is a Client/Server launcher for Conan Exiles. Made using .net and Avalonia, it uses SteamKit2 to keep server binaries and mod files up to date. The project is still in heavy development, and a lot of planned features are still missing.

Trebuchet is an Open Source project covered by the GNU General Public License version 2. It uses a modified version of Depot Downloader, to simplify the interaction with SteamKit2.  Depot Downloader is also covered by the GNU General Public License version 2, and its modified sources are available in one of my forks, as a submodule of Trebuchet.

Pre-release of the App is currently being tested, we have a semi-private Discord channel to discuss it, instructions are available there too. [Please join the server](<https://discord.gg/fTaxD9SNS9>) and DM to get access.
