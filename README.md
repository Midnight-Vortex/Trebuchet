# Trebuchet

After installing, launch Trebuchet from the Start menu or desktop shortcut. The installer deliberately does not launch it: on affected Windows versions, a process started by Setup can inherit RedirectionGuard and fail to traverse Trebuchet's Saved/profile junctions. Conan then reports that it cannot ensure `Saved/ExtractedMods` exists, even when the directory is present. If this happens after an older setup, fully close Trebuchet and relaunch it from the Start menu; reinstalling mods is not required for this directory-access error.

## 0.9.1 maintenance update

- Updated all direct NuGet references with available stable releases (checked September 17, 2026), including Avalonia 12.1.2, Microsoft extensions 10.0.12, Discord.Net.Webhook 3.20.1 and protobuf-net 3.4.30. The installer is built with Inno Setup 7.1.0.
- Added Enhanced PTC selection from [upstream commit 3c6068fb](https://github.com/Totchinuko/Trebuchet/commit/3c6068fb). It uses separate `EnhancedPTC` profile/server folders and `settings.enhanced.ptc.json` / `settings.ui.enhanced.ptc.json`. Existing Legacy, Enhanced and TestLive profiles keep their locations.
- `--enhanced --ptc` selects Enhanced PTC and adds `-xls=ext` when launching the game. `--testlive` remains supported; `--live --ptc` selects Legacy PTC. GUI restart, autostart and Boulder use the same edition parsing.
- Switching a mod list to read-only preserves its entries. Workshop metadata refresh preserves disabled delete actions; loading state resets even if a refresh fails. Sorting and regex search continue to affect display only.

Build with the .NET 10 SDK and Inno Setup 7. Publish `Trebuchet/Trebuchet.csproj` and `Boulder/Boulder.csproj` for `win-x64` with `--self-contained true`, then compile `build_setup.iss`. For a solution build use `dotnet build Trebuchet.sln -c Release -m:1 -p:SelfContained=false -p:PublishSingleFile=false`. Run the two regression harnesses in `Tests/ConsoleHotkeyChecks` and `Tests/ModListDisplayChecks` with those same property overrides.

Trebuchet is a Client/Server launcher for Conan Exiles. Made using .net and Avalonia, it uses SteamKit2 to keep server binaries and mod files up to date. The project is still in heavy development, and a lot of planned features are still missing.

Trebuchet is an Open Source project covered by the GNU General Public License version 2. It uses a modified version of Depot Downloader, to simplify the interaction with SteamKit2.  Depot Downloader is also covered by the GNU General Public License version 2, and its modified sources are available in one of my forks, as a submodule of Trebuchet.

Pre-release of the App is currently being tested, we have a semi-private Discord channel to discuss it, instructions are available there too. [Please join the server](<https://discord.gg/fTaxD9SNS9>) and DM to get access.
