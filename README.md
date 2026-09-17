# Trebuchet

After installing, launch Trebuchet from the Start menu or desktop shortcut. The installer deliberately does not launch it: on affected Windows versions, a process started by Setup can inherit RedirectionGuard and fail to traverse Trebuchet's Saved/profile junctions. Conan then reports that it cannot ensure `Saved/ExtractedMods` exists, even when the directory is present. If this happens after an older setup, fully close Trebuchet and relaunch it from the Start menu; reinstalling mods is not required for this directory-access error.

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
